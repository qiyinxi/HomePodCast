using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

[Flags]
internal enum VolumeKeys { None = 0, Up = 1, Down = 2, Mute = 4 }

/// <summary>The Windows output endpoint's mute flag and master level (scalar 0..1).</summary>
internal readonly record struct EndpointState(bool Muted, float Level)
{
    public const float SilentLevel = 0.005f;
    public bool IsSilent => Muted || Level <= SilentLevel;
}

/// <summary>What one change means: move the speaker volume by Percent points, or toggle its mute; Restore = put Windows back.</summary>
internal readonly record struct ForwardAction(double Percent, bool ToggleMute, bool Restore);

/// <summary>How the Windows output endpoint is watched (<see cref="VolumeKeyRules.EndpointModeFor"/>).</summary>
internal enum EndpointMode
{
    Off,

    /// <summary>VolumeKeyMode.WhenWindowsMuted: key steps on a silent Windows go to the HomePod, Windows is put back.</summary>
    Forward,

    /// <summary>
    /// VolumeKeyMode.FollowWindows: every change of the Windows volume or mute that isn't ours is reported; levels asked
    /// for with RequestLevel are written to the watched endpoint with our event context (never followed back).
    /// </summary>
    Follow,
}

/// <summary>
/// The Windows side of <see cref="VolumeKeyMode.FollowWindows"/>: turns endpoint notifications into transitions
/// to follow. Our own writes (our event context) and repeated states (channel-only changes) are not followed, and
/// nothing is followed before a baseline is known, so taking the baseline (stream start, mode or device change)
/// never moves the HomePod. Not thread-safe; the forwarder locks around it.
/// </summary>
internal sealed class WindowsVolumeFollower
{
    private EndpointState? _last;

    public void Reset(EndpointState? baseline = null) => _last = baseline;

    public (EndpointState From, EndpointState To)? Next(EndpointState now, bool ours)
    {
        var last = _last;
        _last = now;
        if (ours || last is not { } from || from == now) return null;
        return (from, now);
    }
}

/// <summary>
/// Watches the Windows output endpoint's volume for two modes (<see cref="Mode"/>):
/// <para><see cref="EndpointMode.Forward"/> (volume-key forwarding). Loopback capture is taken before the endpoint
/// volume, so a common setup is Windows muted (or at 0 %) while the HomePod plays. The keyboard's volume keys would
/// then just unmute the PC speakers; instead, while Windows is silent and we are streaming, each key step becomes a
/// HomePod volume step and Windows is put straight back to silent. Changes that don't look like a key press
/// (dragging the Windows slider, clicking unmute) are left alone, so Windows audio can always be taken back.
/// Known limit: at 0 % (not muted) Windows sends nothing for "volume down", so only muting gives both keys.</para>
/// <para><see cref="EndpointMode.Follow"/>: every change of the Windows level or mute is raised as
/// <see cref="WindowsChanged"/> (the HomePod follows it), a fresh baseline as <see cref="FollowStarted"/> (both sides are
/// aligned), and <see cref="RequestLevel"/> writes the level that stands for the HomePod volume (FollowWindowsLink).</para>
/// </summary>
internal sealed partial class VolumeKeyForwarder : IDisposable
{
    /// <summary>Step of Windows' volume keys and of the mouse wheel on the tray icon.</summary>
    public const float ShellStep = 0.02f;

    /// <summary>A key press can arrive as two notifications (unmute + level); key repeat is ≥ 33 ms apart.</summary>
    private static readonly long EchoTicks = Stopwatch.Frequency / 40;

    private readonly Guid _context = Guid.NewGuid();
    private readonly AutoResetEvent _wake = new(false);
    private readonly EndpointVolumeCallback _callback;
    private readonly IntPtr _callbackPtr;
    private readonly Thread _thread;
    private readonly WindowsVolumeFollower _follower = new();
    private volatile EndpointMode _mode;
    private volatile bool _stop;
    private int _keys;              // VolumeKeys seen held at notification time (callback thread → worker)
    private EndpointState _baseline;
    private bool _tracking;         // _baseline was taken for _trackedMode
    private EndpointMode _trackedMode;
    private long _lastForward;

    /// <summary>Raised on the worker thread with ± percent points for the speaker (Forward).</summary>
    public event Action<double>? VolumeStep;

    /// <summary>Raised on the worker thread when the mute key was pressed (Forward).</summary>
    public event Action? MuteToggled;

    /// <summary>Raised on Windows' notification thread when the Windows level or mute changed, not by us (Follow).</summary>
    public event Action<EndpointState, EndpointState>? WindowsChanged;

    /// <summary>
    /// Raised on the worker thread with the endpoint's state when following starts from a fresh baseline: the mode was
    /// switched to Follow (stream start, reconnect, mode change) or another endpoint is watched now.
    /// </summary>
    public event Action<EndpointState>? FollowStarted;

    private volatile bool _rebaseline;   // the mode was switched: take a new baseline even if the worker never saw the old mode
    private int _levelPending;           // 1 when _pendingLevel is to be written (UI → worker)
    private float _pendingLevel;

    /// <summary>
    /// Follow: set the watched endpoint's master level (0..1), with our event context so the change isn't followed back.
    /// Done on the worker thread, latest request wins; dropped when not following or without an endpoint. Thread-safe.
    /// </summary>
    public void RequestLevel(float level)
    {
        Volatile.Write(ref _pendingLevel, Math.Clamp(level, 0f, 1f));
        Volatile.Write(ref _levelPending, 1);
        _wake.Set();
    }

    private readonly Func<string?> _endpointId;

    /// <param name="endpointId">
    /// The render endpoint whose volume is watched, as an IMMDevice id; null (or a null result) = the Windows default
    /// output. Asked again at every device check (once a second, on the worker thread), so a new answer re-subscribes
    /// to that endpoint within a second. Must be quick and thread-safe.
    /// </param>
    public VolumeKeyForwarder(Func<string?>? endpointId = null)
    {
        _endpointId = endpointId ?? (() => null);
        _callback = new EndpointVolumeCallback(OnNotify);
        _callbackPtr = Marshal.GetComInterfaceForObject<EndpointVolumeCallback, IAudioEndpointVolumeCallback>(_callback);
        _thread = new Thread(Run) { IsBackground = true, Name = "Volume keys" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>What to do with endpoint changes (Off unless streaming). Thread-safe.</summary>
    public EndpointMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            lock (_follower) _follower.Reset(); // a new baseline is taken before anything is followed
            _mode = value;
            _rebaseline = true;
            _wake.Set();
        }
    }

    /// <summary>Decide what a change of the Windows endpoint from <paramref name="baseline"/> to <paramref name="now"/> means.</summary>
    public static ForwardAction Decide(EndpointState baseline, EndpointState now, VolumeKeys keys, float deviceStep)
    {
        if (!baseline.IsSilent) return default; // Windows audio is in normal use: not ours to touch
        float delta = now.Level - baseline.Level;
        bool levelChanged = MathF.Abs(delta) > 0.001f;
        bool muteChanged = now.Muted != baseline.Muted;
        if (!levelChanged && !muteChanged) return default;

        bool keyStep = levelChanged && (IsStep(delta, ShellStep) || IsStep(delta, deviceStep));
        if ((keys & (VolumeKeys.Up | VolumeKeys.Down)) != 0 || keyStep)
        {
            // At 100 % (or 0 % muted) the key only unmutes Windows: take the direction from the key.
            int dir = levelChanged ? Math.Sign(delta) : (keys & VolumeKeys.Up) != 0 ? 1 : -1;
            return new ForwardAction(levelChanged ? delta * 100.0 : dir * ShellStep * 100.0, false, true);
        }
        if (muteChanged && !levelChanged && (keys & VolumeKeys.Mute) != 0) return new ForwardAction(0, true, true);
        return default; // slider dragged or unmute clicked: the user wants Windows audio back
    }

    private static bool IsStep(float delta, float step) => step > 0 && MathF.Abs(MathF.Abs(delta) - step) < 0.003f;

    private void OnNotify(Guid context, bool muted, float level)
    {
        switch (_mode)
        {
            case EndpointMode.Follow:
                (EndpointState From, EndpointState To)? change;
                lock (_follower) change = _follower.Next(new EndpointState(muted, level), ours: context == _context);
                if (change is { } c) WindowsChanged?.Invoke(c.From, c.To);
                return;
            case EndpointMode.Forward when context != _context: // not our own restore
                Interlocked.Or(ref _keys, (int)KeysHeld());
                _wake.Set();
                return;
        }
    }

    private static VolumeKeys KeysHeld()
    {
        var k = VolumeKeys.None;
        if (GetAsyncKeyState(0xAF) < 0) k |= VolumeKeys.Up;     // VK_VOLUME_UP
        if (GetAsyncKeyState(0xAE) < 0) k |= VolumeKeys.Down;   // VK_VOLUME_DOWN
        if (GetAsyncKeyState(0xAD) < 0) k |= VolumeKeys.Mute;   // VK_VOLUME_MUTE
        return k;
    }

    /// <summary>
    /// Windows has a default output device whose volume we can watch. Checked once a second (also while Off), so the
    /// volume keys can be taken over while there is none (<see cref="VolumeKeyRules.RouteFor"/>). True until the first check.
    /// </summary>
    public bool OutputDevicePresent => _outputDevice;

    /// <summary>Raised on the worker thread when <see cref="OutputDevicePresent"/> changes.</summary>
    public event Action<bool>? OutputDeviceChanged;

    private volatile bool _outputDevice = true;
    private bool _outputDeviceChecked;

    private void SetOutputDevice(bool present, string? problem)
    {
        if (_outputDeviceChecked && _outputDevice == present) return;
        bool first = !_outputDeviceChecked;
        _outputDeviceChecked = true;
        if (present) { if (!first) Log.Info("volume keys: a Windows output device is back"); }
        else Log.Warn($"volume keys: no usable Windows output device ({problem}); while streaming the volume keys go to the HomePod");
        if (_outputDevice == present) return;
        _outputDevice = present;
        OutputDeviceChanged?.Invoke(present);
    }

    private string? WantedEndpointId()
    {
        try { return _endpointId() is { Length: > 0 } id ? id : null; }
        catch (Exception ex)
        {
            Log.Warn($"volume keys: endpoint choice failed, using the default output: {ex.Message}");
            return null;
        }
    }

    private void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        Endpoint? endpoint = null;
        long nextDeviceCheck = 0;
        try
        {
            while (!_stop)
            {
                try
                {
                    if (Stopwatch.GetTimestamp() >= nextDeviceCheck)
                    {
                        nextDeviceCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                        if (enumerator == null)
                        {
                            try { enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
                            catch (Exception ex) { SetOutputDevice(false, $"audio service unavailable: {ex.Message}"); }
                        }
                        string? wanted = WantedEndpointId();
                        if (enumerator != null && (endpoint == null || !endpoint.IsCurrent(enumerator, wanted)))
                        {
                            endpoint?.Dispose();
                            endpoint = null;
                            lock (_follower) _follower.Reset(); // another device: its level is not a change to follow
                            endpoint = Endpoint.TryOpen(enumerator, wanted, _callbackPtr, out var problem);
                            _tracking = false; // new device: take a fresh baseline
                            SetOutputDevice(endpoint != null, problem);
                        }
                    }
                    if (endpoint != null)
                    {
                        Handle(endpoint);
                        WritePendingLevel(endpoint);
                    }
                    else Interlocked.Exchange(ref _levelPending, 0); // nothing to write to
                }
                catch (Exception ex) when (!_stop)
                {
                    Log.Warn($"volume keys: {ex.Message}");
                    try { endpoint?.Dispose(); } catch { }
                    endpoint = null; // opened again (or found missing) at the next check
                }
                _wake.WaitOne(1000);
            }
        }
        finally
        {
            endpoint?.Dispose();
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private void Handle(Endpoint endpoint)
    {
        var keys = (VolumeKeys)Interlocked.Exchange(ref _keys, 0);
        if (!endpoint.TryRead(out var now)) return;
        var mode = _mode;
        if (mode == EndpointMode.Off || !_tracking || mode != _trackedMode || _rebaseline)
        {
            _rebaseline = false;
            if (mode == EndpointMode.Forward && now.IsSilent)
                Log.Info($"volume keys: Windows output is {(now.Muted ? "muted" : "at 0 %")}, keys go to the speaker");
            _baseline = now;
            _trackedMode = mode;
            _tracking = mode != EndpointMode.Off;
            if (mode == EndpointMode.Follow)
            {
                lock (_follower) _follower.Reset(now);
                Log.Info($"volume keys: the HomePod follows the Windows volume (now {now.Level:P0}{(now.Muted ? ", muted" : "")})");
                FollowStarted?.Invoke(now);
            }
            return;
        }
        if (mode != EndpointMode.Forward || now == _baseline) return;

        var action = Decide(_baseline, now, keys, endpoint.Step);
        if (!action.Restore)
        {
            _baseline = now;
            return;
        }
        endpoint.Restore(_baseline, _context);
        long t = Stopwatch.GetTimestamp();
        if (t - _lastForward < EchoTicks) return; // second half of the same key press
        _lastForward = t;
        Log.Debug($"volume key: {(action.ToggleMute ? "mute" : $"{action.Percent:+0.#;-0.#}%")} → speaker");
        if (action.ToggleMute) MuteToggled?.Invoke();
        else VolumeStep?.Invoke(action.Percent);
    }

    /// <summary>Follow: a level asked for by <see cref="RequestLevel"/>, written with our context (not followed back).</summary>
    private void WritePendingLevel(Endpoint endpoint)
    {
        if (Interlocked.Exchange(ref _levelPending, 0) == 0) return;
        if (_mode != EndpointMode.Follow || !_tracking || _trackedMode != EndpointMode.Follow) return;
        float level = Volatile.Read(ref _pendingLevel);
        int hr = endpoint.SetLevel(level, _context);
        if (hr < 0) Log.Warn($"volume keys: setting the Windows volume failed 0x{hr:X8}");
        else Log.Debug($"volume keys: Windows volume → {level:P0} (follows the HomePod)");
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(2000);
        Marshal.Release(_callbackPtr);
    }

    /// <summary>A render endpoint's volume control (the default output, or the one asked for) with our callback registered.</summary>
    private sealed class Endpoint : IDisposable
    {
        private const int DeviceStateActive = 1;

        private readonly IMMDevice _device;
        private readonly IAudioEndpointVolume _volume;
        private readonly IntPtr _callback;
        private readonly string _id;
        private readonly bool _isDefault; // opened as "the default output" (else by id)

        public float Step { get; }

        private Endpoint(IMMDevice device, IAudioEndpointVolume volume, IntPtr callback, string id, bool isDefault, float step)
        {
            _device = device;
            _volume = volume;
            _callback = callback;
            _id = id;
            _isDefault = isDefault;
            Step = step;
        }

        /// <summary>
        /// The volume of endpoint <paramref name="wantedId"/> (null = the default output), or null with the reason:
        /// no device (E_NOTFOUND 0x80070490), the device not active (unplugged, disabled), no endpoint volume.
        /// </summary>
        public static Endpoint? TryOpen(IMMDeviceEnumerator enumerator, string? wantedId, IntPtr callback, out string? problem)
        {
            int hr = wantedId == null
                ? enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device)
                : enumerator.GetDevice(wantedId, out device);
            if (hr < 0 || device == null)
            {
                problem = wantedId == null ? $"no default output device, 0x{hr:X8}" : $"output device {wantedId} not found, 0x{hr:X8}";
                return null;
            }
            if (wantedId != null && (device.GetState(out int state) < 0 || state != DeviceStateActive))
            {
                problem = $"output device {wantedId} not active";
                Marshal.ReleaseComObject(device);
                return null;
            }
            device.GetId(out var id);
            id ??= wantedId ?? "";
            var iid = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj);
            if (hr < 0 || obj is not IAudioEndpointVolume volume)
            {
                problem = $"endpoint volume unavailable, 0x{hr:X8}";
                Marshal.ReleaseComObject(device);
                return null;
            }
            float step = volume.GetVolumeStepInfo(out _, out uint count) >= 0 && count > 1 ? 1f / (count - 1) : ShellStep;
            hr = volume.RegisterControlChangeNotify(callback);
            if (hr < 0)
            {
                problem = $"volume notifications unavailable, 0x{hr:X8}";
                Marshal.ReleaseComObject(volume);
                Marshal.ReleaseComObject(device);
                return null;
            }
            problem = null;
            Log.Info($"volume keys: watching {(wantedId == null ? "the default output" : "output")} \"{CoreAudio.FriendlyName(device)}\"");
            return new Endpoint(device, volume, callback, id, wantedId == null, step);
        }

        /// <summary>Still the endpoint to watch: the default output (when asked for that), or <paramref name="wantedId"/> and active.</summary>
        public bool IsCurrent(IMMDeviceEnumerator enumerator, string? wantedId)
        {
            if (wantedId != null)
                return !_isDefault && wantedId == _id && _device.GetState(out int state) >= 0 && state == DeviceStateActive;
            if (!_isDefault) return false;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var current) < 0 || current == null) return false;
            current.GetId(out var id);
            Marshal.ReleaseComObject(current);
            return id == _id;
        }

        public bool TryRead(out EndpointState state)
        {
            state = default;
            if (_volume.GetMute(out bool muted) < 0 || _volume.GetMasterVolumeLevelScalar(out float level) < 0) return false;
            state = new EndpointState(muted, level);
            return true;
        }

        /// <summary>The master level only (the mute state stays as it is).</summary>
        public int SetLevel(float level, Guid context) => _volume.SetMasterVolumeLevelScalar(level, ref context);

        /// <summary>Back to the silent state, mute first when it was muted so the PC speakers stay quiet.</summary>
        public void Restore(EndpointState s, Guid context)
        {
            if (s.Muted)
            {
                _volume.SetMute(true, ref context);
                _volume.SetMasterVolumeLevelScalar(s.Level, ref context);
            }
            else
            {
                _volume.SetMasterVolumeLevelScalar(s.Level, ref context);
                _volume.SetMute(false, ref context);
            }
        }

        public void Dispose()
        {
            try { _volume.UnregisterControlChangeNotify(_callback); } catch { }
            Marshal.ReleaseComObject(_volume);
            Marshal.ReleaseComObject(_device);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}

/// <summary>Our IAudioEndpointVolumeCallback (a COM-callable wrapper); Windows calls OnNotify on an RPC thread.</summary>
internal sealed class EndpointVolumeCallback(Action<Guid, bool, float> notified) : IAudioEndpointVolumeCallback
{
    public int OnNotify(IntPtr notifyData)
    {
        try
        {
            if (notifyData == IntPtr.Zero) return 0;
            var d = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
            notified(d.EventContext, d.Muted != 0, d.MasterVolume);
        }
        catch (Exception ex)
        {
            Log.Warn($"volume notify: {ex.Message}");
        }
        return 0;
    }
}
