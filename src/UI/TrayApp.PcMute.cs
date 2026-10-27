using System.Diagnostics;
using HomePodCast.Audio;

namespace HomePodCast.UI;

// 推流时电脑静音 (experimental since 1.2) and 首页's 「电脑也在出声」 hint. Audio.PcMute decides; this part tells it the
// stream's state and what is captured, and when to look again. All on the UI thread.
internal sealed partial class TrayApp
{
    private PcMute? _pcMute;
    private TimerDebounce? _pcMuteCheck; // looks again once an Idle has lasted, and once the Windows volume has settled
    private long _idleSince;             // Stopwatch timestamp the controller was first seen Idle; 0 while it isn't
    private int _pcChangePending;        // a Windows volume change waits to be posted (notification thread → UI)

    /// <summary>Called once from InitSound, after the Windows volume watcher and before anything can stream.</summary>
    private void InitPcMute()
    {
        UsePcOutput(new WindowsPcOutput());
        var check = new TimerDebounce(PcMute.IdleGraceMs);
        check.Tick += () =>
        {
            check.Stop();
            UpdatePcMute();
        };
        _pcMuteCheck = check;
        if (_forwarder != null) _forwarder.Notified += OnWindowsVolumeNotified;
    }

    /// <summary>
    /// The default output 推流时电脑静音 works on (the tests give a fake). First the sound a killed or crashed last run left
    /// muted comes back (AppConfig.PcMutedEndpointId); the record is cleared and saved whatever happened to it.
    /// </summary>
    internal void UsePcOutput(IPcOutput output)
    {
        if (Config.PcMutedEndpointId is { } leftover)
        {
            PcMute.RestoreLeftover(output, leftover);
            Config.PcMutedEndpointId = null;
            Config.Save();
        }
        _pcMute = new PcMute(output, RememberPcMute);
    }

    /// <summary>PcMute's mute became ours (an endpoint id) or stopped being ours (null): saved at once, for a crash.</summary>
    private void RememberPcMute(string? id)
    {
        if (Config.PcMutedEndpointId == id) return;
        Config.PcMutedEndpointId = id;
        Config.TrySave(); // called inside PcMute.Update (OnControllerChanged), which has to go on
    }

    /// <summary>Quitting (also for Windows: QuitForSystem): the PC's sound comes back, if the mute is ours and still there.</summary>
    private void DisposePcMute()
    {
        _pcMuteCheck?.Dispose();
        _pcMuteCheck = null;
        _pcMute?.Release("HomePodCast quits");
    }

    /// <summary>首页 →「仅 HomePod 输出」. Applies at once while streaming: muted now, or unmuted if the mute was ours.</summary>
    public void SetMutePcWhileStreaming(bool on)
    {
        if (Config.MutePcWhileStreaming == on) return;
        Config.MutePcWhileStreaming = on;
        Config.Save();
        Log.Info($"mute the PC while streaming: {(on ? "on" : "off")}");
        // FollowWindows must not see the mute: on, the keys stop following Windows first; off, the mute is undone before
        // they follow it again (the watcher also ignores PcMute's own changes).
        if (on)
        {
            UpdateVolumeKeys();
            UpdatePcMute();
        }
        else
        {
            UpdatePcMute();
            UpdateVolumeKeys();
        }
        _form?.ShowPcSound();
    }

    /// <summary>
    /// The hint's 「只用 HomePod 播放」: 推流时电脑静音 on (the PC is muted now), and volume keys that control the HomePod
    /// while streaming. Off would leave the keys on Windows, where the first press unmutes the PC speakers again;
    /// FollowWindows is handled as WhileStreaming anyway (VolumeKeyRules.Effective), and the setting now says what
    /// happens. WhenWindowsMuted already works on a muted Windows and stays.
    /// </summary>
    public void PlayOnlyOnHomePod()
    {
        Log.Info("首页 hint: only the HomePod plays");
        if (Config.VolumeKeys is VolumeKeyMode.FollowWindows or VolumeKeyMode.Off) SetVolumeKeyMode(VolumeKeyMode.WhileStreaming);
        SetMutePcWhileStreaming(true); // the hint goes with it
    }

    /// <summary>The hint's 「不再提示」.</summary>
    public void DismissPcSoundHint()
    {
        Config.PcSoundHintDismissed = true;
        Config.Save();
        Log.Info("首页 hint 「电脑也在出声」: dismissed");
        _form?.ShowPcSound();
    }

    /// <summary>
    /// 首页's 「电脑也在出声」 is shown now. From what was read last: no Core Audio call (the page asks every 500 ms), and
    /// the settings are looked at before the group plan is.
    /// </summary>
    internal bool PcSoundHintShown =>
        _pcMute?.Output is { } output && !Config.MutePcWhileStreaming && !Config.PcSoundHintDismissed
        && PcMute.HintShown(Controller.State == StreamState.Streaming, Config.MutePcWhileStreaming, Config.PcSoundHintDismissed,
            Routing.Active, PcCaptureNow(), output);

    /// <summary>首页 was opened: the default output is read again.</summary>
    internal void RefreshPcSound() => UpdatePcMute();

    /// <summary>What is sent now: the system capture or the cinema's file, from which device, and a group's outputs on this PC.</summary>
    private PcCapture PcCaptureNow() => new(Controller.GroupSource == null, Routing.CaptureDeviceId,
        Mixer != null ? Mixer.LocalOutputIds.ToArray()
        : GroupPlan.FromConfig(Config)?.LocalOutputs.Select(o => o.DeviceId).ToArray());

    /// <summary>
    /// The stream's state, 推流时电脑静音, the captured device or the default output may have changed: PcMute acts on it,
    /// and 首页's hint follows. Core Audio is read only while streaming or while a mute of ours stands.
    /// </summary>
    private void UpdatePcMute()
    {
        if (_pcMute == null || _quitting) return;
        var state = Controller.State;
        long now = Stopwatch.GetTimestamp();
        if (state != StreamState.Idle) _idleSince = 0;
        else if (_idleSince == 0) _idleSince = now;
        double idleMs = _idleSince == 0 ? 0 : Stopwatch.GetElapsedTime(_idleSince, now).TotalMilliseconds;
        var phase = PcMute.PhaseFor(state, idleMs);
        bool localIntent = Mixer != null && Routing.Active && MixerRoutes.Read(Config).Settings is { } routes &&
            new[] { routes.Default }.Concat(routes.Apps.Values).Any(r => r.Output.Kind == MixerOutputKind.Local || r.AlsoLocal);
        _pcMute.Update(phase, Config.MutePcWhileStreaming && !localIntent, PcCaptureNow());
        // Idle with our mute in place: look again once the Idle has lasted, then the session is over.
        if (phase == PcMutePhase.Between && state == StreamState.Idle && _pcMute.Holding != null && _pcMuteCheck is { Enabled: false } check)
            check.Restart();
        _form?.ShowPcSound();
    }

    /// <summary>
    /// Windows' notification thread: the watched output's volume or mute changed. Not ours: looked at once it has settled
    /// (<see cref="PcMute.IdleGraceMs"/>), so an unmute by hand is respected and the hint follows a mute by hand, while a
    /// volume key's brief unmute that 仅在 Windows 静音时 puts straight back is not taken for one.
    /// </summary>
    private void OnWindowsVolumeNotified(Guid context)
    {
        if (context == PcMute.EventContext) return;
        if (Interlocked.Exchange(ref _pcChangePending, 1) != 0) return;
        _ui.Post(_ =>
        {
            Volatile.Write(ref _pcChangePending, 0);
            if (!_quitting) _pcMuteCheck?.Restart();
        }, null);
    }
}
