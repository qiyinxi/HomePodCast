using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HomePodCast.Audio;

/// <summary>The Windows default output as 推流时电脑静音 sees it: which device, whether it is muted, its master level.</summary>
internal readonly record struct PcOutputState(AudioEndpoint Endpoint, bool Muted, float Level)
{
    public string Id => Endpoint.Id;
}

/// <summary>
/// The Windows default output's mute, for <see cref="PcMute"/>: <see cref="WindowsPcOutput"/> in the app, a fake in the
/// tests (which must never mute or unmute the real PC).
/// </summary>
internal interface IPcOutput
{
    /// <summary>The default output now; null when there is none or it can't be read.</summary>
    PcOutputState? Default();

    /// <summary>Whether endpoint <paramref name="id"/> is muted now; null when it is gone or can't be read.</summary>
    bool? IsMuted(string id);

    /// <summary>Mute or unmute endpoint <paramref name="id"/> with <see cref="PcMute.EventContext"/>; false when that failed.</summary>
    bool SetMute(string id, bool muted);

    /// <summary>The friendly name of endpoint <paramref name="id"/> (for the log); null when it is gone or can't be read.</summary>
    string? Name(string id);
}

/// <summary>Where the stream is, as far as the PC's mute goes (<see cref="PcMute.PhaseFor"/>).</summary>
internal enum PcMutePhase
{
    /// <summary>Streaming: the moment a mute is begun.</summary>
    Streaming,

    /// <summary>Connecting, retrying, or idle for a moment between a stop and the next start: a mute in place stays, none is begun.</summary>
    Between,

    /// <summary>The session is over: a mute of ours is undone.</summary>
    Ended,
}

/// <summary>What is being sent, to tell whether the pushed sound is what plays on the Windows default output.</summary>
/// <param name="SystemCapture">The system capture (not the wireless cinema's file, which never plays on the PC's output).</param>
/// <param name="DeviceId">The chosen capture device (AppRouting.CaptureDeviceId); null = follow the default output.</param>
/// <param name="LocalOutputs">This PC's render endpoints the selected group plays on (GroupPlan.LocalOutputs).</param>
internal readonly record struct PcCapture(bool SystemCapture, string? DeviceId, IReadOnlyList<string>? LocalOutputs = null)
{
    /// <summary>
    /// The pushed sound is what plays on the default output <paramref name="defaultId"/>, and nothing of ours is meant to
    /// be heard there: the system capture of the default output (followed, or chosen and the same device, whole or per
    /// app), and no output of the group on it. A capture routed to another device (Mixer → 采集设备, "routing by sound
    /// card") is not: what plays on the default output then, such as voice chat on the headset, is meant to stay there.
    /// </summary>
    public bool PlaysOn(string? defaultId) =>
        SystemCapture && !string.IsNullOrEmpty(defaultId)
        && (CaptureEndpoint.FollowsDefault(DeviceId) || CaptureEndpoint.Same(DeviceId, defaultId))
        && !(LocalOutputs ?? []).Any(id => CaptureEndpoint.Same(id, defaultId));
}

/// <summary>
/// 推流时电脑静音 (AppConfig.MutePcWhileStreaming, experimental since 1.2). User report 2026-10-18: 「连接后电脑和 HomePod
/// 都在放，怎么只让 HomePod 放」. The capture is taken before the endpoint volume (VolumeKeyForwarder), so muting the
/// Windows default output silences the PC and leaves what is sent alone; until now that was a step by hand, and the
/// PC stayed muted after streaming.
/// <list type="bullet">
/// <item>When a session reaches Streaming, the default output is muted, unless it is muted already (then that mute is not
/// ours, and it is left as it is at the end). Which endpoint we muted is remembered.</item>
/// <item>Retrying, connecting and the moment of Idle between a stop and the next start keep it muted: a short dropout or
/// a reconnect must not blast the PC speakers.</item>
/// <item>The end of the session (Idle for <see cref="IdleGraceMs"/>), the setting turned off, the capture leaving the
/// default output, the default output changing and quitting unmute it: only an endpoint we muted, and only while it is
/// still muted.</item>
/// <item>Unmuted by someone else while streaming (the user, in Windows): left as it is, not muted again this session, not
/// touched at the end.</item>
/// </list>
/// Our own changes carry <see cref="EventContext"/>, so the volume watcher can tell them apart. Every mute and unmute is
/// logged, and the endpoint whose mute is ours is remembered (AppConfig.PcMutedEndpointId): a PC left muted by a crash
/// or 「结束任务」 gets its sound back at the next start (<see cref="RestoreLeftover"/>; review of #77). UI thread only.
/// </summary>
/// <param name="remember">
/// Called with the endpoint id when a mute becomes ours, and with null once it no longer is (given back, unmuted by
/// someone else); saved right away by the app. Not called after an unmute that failed: the next start tries again.
/// </param>
internal sealed class PcMute(IPcOutput output, Action<string?>? remember = null)
{
    /// <summary>The event context of our mute changes (AUDIO_VOLUME_NOTIFICATION_DATA.guidEventContext); any fixed GUID.</summary>
    public static readonly Guid EventContext = new("3f0c5e2a-9b7d-4c61-8e45-1d2a7b9c6e10");

    /// <summary>
    /// How long the controller stays Idle before the session counts as over. A reconnect (a new latency, a profile, a
    /// wake from sleep) stops the old loop and starts the next one, and the controller is Idle for a few milliseconds in
    /// between (CinemaSession has the same Idle): that is not the end. Also the delay before an unmute by someone else is
    /// believed (TrayApp): in 仅在 Windows 静音时 a volume key unmutes Windows for a moment until the forwarder puts it back.
    /// </summary>
    public const int IdleGraceMs = 1000;

    private string? _id;      // the default output this session's decision is about (null: none made yet)
    private string _name = "";
    private bool _ours;       // we muted _id, and that mute still stands
    private bool _handsOff;   // unmuted by someone else in this session: no more mutes until it ends

    /// <summary>The endpoint whose mute is ours, or null.</summary>
    public string? Holding => _ours ? _id : null;

    /// <summary>The default output as read at the last update while streaming (首页's hint); null otherwise.</summary>
    public PcOutputState? Output { get; private set; }

    public static PcMutePhase PhaseFor(StreamState state, double idleMs) => state switch
    {
        StreamState.Streaming => PcMutePhase.Streaming,
        StreamState.Idle when idleMs >= IdleGraceMs => PcMutePhase.Ended,
        _ => PcMutePhase.Between,
    };

    /// <summary>The stream's state, the setting or what is captured may have changed, or the default output: act on it.</summary>
    /// <param name="enabled">AppConfig.MutePcWhileStreaming.</param>
    public void Update(PcMutePhase phase, bool enabled, PcCapture capture)
    {
        if (phase == PcMutePhase.Ended)
        {
            Release("the session ended");
            _id = null;
            _handsOff = false;
            Output = null;
            return;
        }
        if (phase == PcMutePhase.Between && !_ours)
        {
            Output = null;
            return; // nothing to keep, and nothing is begun before it streams
        }

        var now = output.Default();
        Output = phase == PcMutePhase.Streaming ? now : null;
        if (_id != null && !CaptureEndpoint.Same(now?.Id, _id))
        {
            // Windows plays on another device now: ours gets its sound back, the new one is decided afresh.
            Release(now == null ? "no default output any more" : "the default output changed");
            _id = null;
        }
        if (!enabled || now is not { } current || !capture.PlaysOn(current.Id))
        {
            Release(!enabled ? "turned off" : "the stream no longer plays on the default output");
            _id = null;
            if (!enabled) _handsOff = false; // turned on again later: a new decision
            return;
        }
        if (_id != null)
        {
            if (_ours && !current.Muted)
            {
                _ours = false;
                _handsOff = true;
                remember?.Invoke(null);
                Log.Info($"PC mute: \"{_name}\" was unmuted by someone else; left as it is for this session");
            }
            return;
        }
        if (phase != PcMutePhase.Streaming || _handsOff) return;

        _id = current.Id;
        _name = current.Endpoint.Name;
        if (current.Muted)
        {
            Log.Info($"PC mute: \"{_name}\" is muted already; left as it is, also when streaming ends");
            return;
        }
        if (output.SetMute(current.Id, true))
        {
            _ours = true;
            remember?.Invoke(current.Id);
            Log.Info($"PC mute: muted the Windows default output \"{_name}\" ({current.Id}) while streaming; " +
                     "it is unmuted again when the session ends or HomePodCast quits");
        }
        else Log.Warn($"PC mute: muting \"{_name}\" ({current.Id}) failed");
    }

    /// <summary>Undo our mute: unmute the endpoint we muted while it is still muted. Nothing when no mute is ours.</summary>
    public void Release(string why)
    {
        if (!_ours || _id is not { } id) return;
        _ours = false;
        switch (output.IsMuted(id))
        {
            case true when output.SetMute(id, false):
                Log.Info($"PC mute: unmuted \"{_name}\" ({id}) again ({why})");
                break;
            case true:
                // Still remembered: the next start tries again (RestoreLeftover).
                Log.Warn($"PC mute: unmuting \"{_name}\" ({id}) failed ({why}); it can be unmuted in Windows");
                return;
            case false:
                Log.Info($"PC mute: \"{_name}\" is unmuted already ({why}); nothing to undo");
                break;
            default:
                Log.Info($"PC mute: \"{_name}\" ({id}) is gone ({why}); nothing to undo");
                break;
        }
        remember?.Invoke(null);
    }

    /// <summary>
    /// At startup, before anything streams: the last run kept <paramref name="id"/> muted and never gave it back (it was
    /// killed, crashed, or Windows ended it). Unmuted if it is still muted, with <see cref="EventContext"/>; left as it is
    /// when it is unmuted already; forgotten when it is gone. The caller clears the record either way.
    /// </summary>
    public static void RestoreLeftover(IPcOutput output, string id)
    {
        string name = output.Name(id) ?? "?";
        switch (output.IsMuted(id))
        {
            case true when output.SetMute(id, false):
                Log.Info($"PC mute: unmuted \"{name}\" ({id}) left muted by the last run");
                break;
            case true:
                Log.Warn($"PC mute: \"{name}\" ({id}) was left muted by the last run and unmuting it failed; it can be unmuted in Windows");
                break;
            case false:
                Log.Info($"PC mute: \"{name}\" ({id}), muted by the last run, is unmuted already; left as it is");
                break;
            default:
                Log.Info($"PC mute: {id}, muted by the last run, is gone or can't be read; forgotten");
                break;
        }
    }

    // ---------------------------------------------------------------- 首页's hint 「电脑也在出声」

    /// <summary>
    /// Shown while streaming with 推流时电脑静音 off and the hint not dismissed, when the pushed sound plays on the default
    /// output (<see cref="PcCapture.PlaysOn"/>), that is a device someone listens to (<see cref="IsListeningDevice"/>), and
    /// it is neither muted nor at 0 %. Not with per-app routing: there the apps sent to the HomePod are silent on this PC
    /// already, and what plays here (apps set to 本机 or 两者, system sounds) was chosen so.
    /// </summary>
    public static bool HintShown(bool streaming, bool mutePcOn, bool dismissed, bool perAppRouting, PcCapture capture, PcOutputState? output) =>
        streaming && !mutePcOn && !dismissed && !perAppRouting && output is { } o && capture.PlaysOn(o.Id)
        && IsListeningDevice(o.Endpoint) && !o.Muted && o.Level > EndpointState.SilentLevel;

    /// <summary>
    /// Speakers, headphones or a headset by the endpoint's form factor, and not a virtual card by its name: virtual cards
    /// often say they are speakers (VB-CABLE, Steam Streaming Speakers, NetEase UU's card). HDMI and DisplayPort audio, S/PDIF,
    /// line out and unknown form factors never count. When unsure, it is not one.
    /// </summary>
    public static bool IsListeningDevice(AudioEndpoint endpoint) =>
        endpoint.FormFactor is EndpointFormFactor.Speakers or EndpointFormFactor.Headphones or EndpointFormFactor.Headset
        && !LooksVirtual(endpoint.Name);

    /// <summary>Parts of the names of virtual sound cards (case-insensitive).</summary>
    private static readonly string[] VirtualMarks = ["cable", "vb-audio", "voicemeeter", "virtual", "steam streaming", "虚拟", "虛擬"];

    /// <summary>"UU" (NetEase UU's card) only as a word of its own: as a part, it is in ordinary names.</summary>
    private static readonly Regex VirtualWord = new(@"(?<![a-z])uu(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The name looks like a virtual card's, or there is no usable name ("?" when Windows gave none).</summary>
    internal static bool LooksVirtual(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim() == "?"
        || VirtualMarks.Any(mark => name.Contains(mark, StringComparison.OrdinalIgnoreCase)) || VirtualWord.IsMatch(name);
}

/// <summary>
/// The real Windows default output through Core Audio (the IAudioEndpointVolume of VolumeKeyForwarder). Called on the UI
/// thread, like AppRouting.LocalOutputSilent; each call opens and releases what it needs. Never used by the tests.
/// </summary>
internal sealed class WindowsPcOutput : IPcOutput
{
    public PcOutputState? Default()
    {
        try
        {
            using var opened = Opened.Open(null);
            if (opened == null || AudioEndpoints.Describe(opened.Device) is not { } endpoint) return null;
            if (opened.Volume.GetMute(out bool muted) < 0 || opened.Volume.GetMasterVolumeLevelScalar(out float level) < 0) return null;
            return new PcOutputState(endpoint, muted, level);
        }
        catch (Exception ex)
        {
            Log.Warn($"PC mute: the default output can't be read: {ex.Message}");
            return null;
        }
    }

    public bool? IsMuted(string id)
    {
        try
        {
            using var opened = Opened.Open(id);
            return opened != null && opened.Volume.GetMute(out bool muted) >= 0 ? muted : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"PC mute: {id} can't be read: {ex.Message}");
            return null;
        }
    }

    public bool SetMute(string id, bool muted)
    {
        try
        {
            using var opened = Opened.Open(id);
            if (opened == null) return false;
            var context = PcMute.EventContext;
            int hr = opened.Volume.SetMute(muted, ref context);
            if (hr < 0) Log.Warn($"PC mute: SetMute({muted}) on {id} failed 0x{hr:X8}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"PC mute: SetMute({muted}) on {id} failed: {ex.Message}");
            return false;
        }
    }

    public string? Name(string id)
    {
        try
        {
            using var opened = Opened.Open(id);
            return opened == null ? null : CoreAudio.FriendlyName(opened.Device);
        }
        catch (Exception ex)
        {
            Log.Warn($"PC mute: {id} can't be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>An active render endpoint (by id, or the default output) with its volume control.</summary>
    private sealed class Opened : IDisposable
    {
        private readonly IMMDeviceEnumerator _enumerator;

        private Opened(IMMDeviceEnumerator enumerator, IMMDevice device, IAudioEndpointVolume volume)
        {
            _enumerator = enumerator;
            Device = device;
            Volume = volume;
        }

        public IMMDevice Device { get; }
        public IAudioEndpointVolume Volume { get; }

        /// <param name="id">The endpoint; null = the Windows default output.</param>
        public static Opened? Open(string? id)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice? device = null;
            try
            {
                device = AudioEndpoints.Open(enumerator, EDataFlow.Render, id); // null when gone or not active
                if (device == null)
                {
                    Marshal.ReleaseComObject(enumerator);
                    return null;
                }
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj) < 0 || obj is not IAudioEndpointVolume volume)
                {
                    Marshal.ReleaseComObject(device);
                    Marshal.ReleaseComObject(enumerator);
                    return null;
                }
                return new Opened(enumerator, device, volume);
            }
            catch
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
                throw;
            }
        }

        public void Dispose()
        {
            Marshal.ReleaseComObject(Volume);
            Marshal.ReleaseComObject(Device);
            Marshal.ReleaseComObject(_enumerator);
        }
    }
}
