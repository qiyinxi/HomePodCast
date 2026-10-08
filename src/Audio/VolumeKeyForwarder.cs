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

/// <summary>
/// Volume-key forwarding. Loopback capture is taken before the endpoint volume, so the usual setup is
/// Windows muted (or at 0 %) while the HomePod plays. The keyboard's volume keys would then just unmute
/// the PC speakers; instead, while Windows is silent and we are streaming, each key step becomes a HomePod
/// volume step and Windows is put straight back to silent. Changes that don't look like a key press
/// (dragging the Windows slider, clicking unmute) are left alone, so Windows audio can always be taken back.
/// Known limit: at 0 % (not muted) Windows sends nothing for "volume down", so only muting gives both keys.
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
    private volatile bool _active, _stop;
    private int _keys;              // VolumeKeys seen held at notification time (callback thread → worker)
    private EndpointState _baseline;
    private bool _tracking;         // _baseline was taken while active
    private long _lastForward;

    /// <summary>Raised on the worker thread with ± percent points for the speaker.</summary>
    public event Action<double>? VolumeStep;

    /// <summary>Raised on the worker thread when the mute key was pressed.</summary>
    public event Action? MuteToggled;

    public VolumeKeyForwarder()
    {
        _callback = new EndpointVolumeCallback(OnNotify);
        _callbackPtr = Marshal.GetComInterfaceForObject<EndpointVolumeCallback, IAudioEndpointVolumeCallback>(_callback);
        _thread = new Thread(Run) { IsBackground = true, Name = "Volume keys" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Forward only while true (streaming and the option is on). Thread-safe.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
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
        if (context == _context || !_active) return; // our own restore, or nothing to do
        Interlocked.Or(ref _keys, (int)KeysHeld());
        _wake.Set();
    }

    private static VolumeKeys KeysHeld()
    {
        var k = VolumeKeys.None;
        if (GetAsyncKeyState(0xAF) < 0) k |= VolumeKeys.Up;     // VK_VOLUME_UP
        if (GetAsyncKeyState(0xAE) < 0) k |= VolumeKeys.Down;   // VK_VOLUME_DOWN
        if (GetAsyncKeyState(0xAD) < 0) k |= VolumeKeys.Mute;   // VK_VOLUME_MUTE
        return k;
    }

    private void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        Endpoint? endpoint = null;
        long nextDeviceCheck = 0;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            while (!_stop)
            {
                try
                {
                    if (Stopwatch.GetTimestamp() >= nextDeviceCheck)
                    {
                        nextDeviceCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                        if (endpoint == null || !endpoint.IsDefault(enumerator))
                        {
                            endpoint?.Dispose();
                            endpoint = Endpoint.TryOpen(enumerator, _callbackPtr);
                            _tracking = false; // new device: take a fresh baseline
                        }
                    }
                    if (endpoint != null) Handle(endpoint);
                }
                catch (Exception ex) when (!_stop)
                {
                    Log.Warn($"volume keys: {ex.Message}");
                    endpoint?.Dispose();
                    endpoint = null;
                }
                _wake.WaitOne(1000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"volume keys disabled: {ex.Message}");
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
        if (!_active || !_tracking)
        {
            if (_active && now.IsSilent)
                Log.Info($"volume keys: Windows output is {(now.Muted ? "muted" : "at 0 %")}, keys go to the speaker");
            _baseline = now;
            _tracking = _active;
            return;
        }
        if (now == _baseline) return;

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

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(2000);
        Marshal.Release(_callbackPtr);
    }

    /// <summary>The default render endpoint's volume control with our callback registered.</summary>
    private sealed class Endpoint : IDisposable
    {
        private readonly IMMDevice _device;
        private readonly IAudioEndpointVolume _volume;
        private readonly IntPtr _callback;
        private readonly string _id;

        public float Step { get; }

        private Endpoint(IMMDevice device, IAudioEndpointVolume volume, IntPtr callback, string id, float step)
        {
            _device = device;
            _volume = volume;
            _callback = callback;
            _id = id;
            Step = step;
        }

        public static Endpoint? TryOpen(IMMDeviceEnumerator enumerator, IntPtr callback)
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device) < 0) return null;
            device.GetId(out var id);
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj) < 0)
            {
                Marshal.ReleaseComObject(device);
                return null;
            }
            var volume = (IAudioEndpointVolume)obj;
            float step = volume.GetVolumeStepInfo(out _, out uint count) >= 0 && count > 1 ? 1f / (count - 1) : ShellStep;
            int hr = volume.RegisterControlChangeNotify(callback);
            if (hr < 0)
            {
                Log.Warn($"volume keys: register notify failed 0x{hr:X8}");
                Marshal.ReleaseComObject(volume);
                Marshal.ReleaseComObject(device);
                return null;
            }
            return new Endpoint(device, volume, callback, id, step);
        }

        public bool IsDefault(IMMDeviceEnumerator enumerator)
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var current) < 0) return false;
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
