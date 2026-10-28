using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

// experimental since 1.4
/// <summary>A capture for the HomePod's 5.1 stream that also says whether it can give 5.1 (StreamController.Surround.cs).</summary>
internal interface ISurroundCapture : ICaptureSource
{
    /// <summary>
    /// Translated; non-null: the session must end, because the captured endpoint is no longer 5.1-capable (R4) or per-app
    /// routing came on (R2). The next connect attempt then streams stereo.
    /// </summary>
    string? Problem { get; }

    /// <summary>
    /// Null once 5.1 is being captured; otherwise why not (translated): the endpoint isn't 5.1 or 7.1 (R4), or there is no
    /// device to capture, or none opened within <paramref name="timeout"/> (R5).
    /// </summary>
    Task<string?> WaitReadyAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Loopback of the whole output endpoint as 5.1 at the FIFO's rate (48 kHz, 6 channels, FL FR FC LFE Ls Rs) for the
/// HomePod's 5.1 stream (experimental since 1.4). The endpoint is chosen exactly as <see cref="RoutedCapture"/> chooses it
/// in endpoint mode: the Windows default output, or the device chosen with 按声卡分流, never the default instead of a
/// chosen device that is gone (<see cref="CaptureEndpoint"/>). Per-app capture is never 6-channel, so there is no routed
/// mode here: routing that comes on ends the session through <see cref="Problem"/>.
/// </summary>
/// <remarks>
/// Only an endpoint whose mix format Windows gives as 5.1 or 7.1 (the speaker setup in Windows' sound settings, spatial
/// sound off) can be captured: on a stereo endpoint Windows has already folded a game's or a film's 5.1 down before
/// loopback. The capture's clock is steered to the FIFO's depth as on the stereo path; the sender reads the FIFO on the
/// HomePod's PTP time, so this locks the sound card to the HomePod's clock.
/// </remarks>
internal sealed class SurroundCapture : ISurroundCapture
{
    private const long BufferDuration = 200_000; // 20 ms
    private static readonly long DeviceCheckInterval = Stopwatch.Frequency;
    private static readonly long ResumeGap = Stopwatch.Frequency / 5; // 200 ms without data: the resamplers start afresh

    private readonly AudioFifo _fifo;
    private readonly Func<string?> _captureDeviceId;
    private readonly Func<string?> _blocked;
    private readonly int _outRate;
    private readonly Thread _thread;
    private readonly AutoResetEvent _ready = new(false);
    private volatile bool _stop;
    private volatile bool _devicesChanged;
    private double _avgDepth;
    private long _maxGapTicks;

    /// <summary>What the last open found: null while not known yet; <see cref="Outcome.Refusal"/> null while capturing 5.1.</summary>
    private volatile Outcome? _outcome;

    private volatile string? _layoutProblem;
    private volatile string? _blockedProblem;
    private volatile CaptureState _state = CaptureState.Capturing;
    private volatile string? _endpointId;

    private sealed record Outcome(string? Refusal);

    private static readonly Outcome Capturing51 = new((string?)null);

    /// <param name="fifo">48 kHz, 6 channels.</param>
    /// <param name="captureDeviceId">The device chosen with 按声卡分流 (null: follow the Windows default output), read live.</param>
    /// <param name="blocked">Non-null (translated) while 5.1 can't be captured for another reason (per-app routing on).</param>
    public SurroundCapture(AudioFifo fifo, Func<string?> captureDeviceId, Func<string?> blocked)
    {
        if (fifo.Channels != SurroundLayout.Slots)
            throw new ArgumentException($"a 5.1 capture needs a {SurroundLayout.Slots}-channel FIFO, not {fifo.Channels}", nameof(fifo));
        _fifo = fifo;
        _captureDeviceId = captureDeviceId;
        _blocked = blocked;
        _outRate = fifo.SampleRate;
        _avgDepth = fifo.TargetFrames;
        _thread = new Thread(Run) { IsBackground = true, Name = "Surround capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public string? DeviceName { get; private set; }
    public double DriftPpm { get; private set; }
    public float Peak { get; private set; }
    public int ExtraLatencyMs => 0;
    public bool NoOutputDevice => _state == CaptureState.NoOutputDevice;
    public bool CaptureDeviceMissing => _state == CaptureState.ChosenDeviceMissing;
    public string? EndpointId => _endpointId;
    public string? Problem => _blockedProblem ?? _layoutProblem;

    public event Action<string>? DeviceChanged;

    public double TakeMaxGapMs() => Interlocked.Exchange(ref _maxGapTicks, 0) * 1000.0 / Stopwatch.Frequency;

    public void Start()
    {
        AudioDeviceWatcher.Changed += OnDevicesChanged; // a device plugged in, or the chosen one back: look at once
        _thread.Start();
    }

    public async Task<string?> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (Problem is { } problem) return problem;
            if (_outcome is { } known) return known.Refusal;
            if (waited.Elapsed >= timeout) return L.T("Windows 输出设备不可用");
            await Task.Delay(20, ct);
        }
    }

    private void OnDevicesChanged()
    {
        _devicesChanged = true;
        _ready.Set();
    }

    /// <summary>Logged and announced once per change, not on every 2 s retry (as <see cref="RoutedCapture"/> does).</summary>
    private void SetState(CaptureState state)
    {
        if (_state == state) return;
        _state = state;
        if (state == CaptureState.NoOutputDevice) Log.Warn("surround capture: Windows has no output device");
        else if (state == CaptureState.ChosenDeviceMissing)
            Log.Warn("surround capture: the chosen capture device is unplugged, disabled or gone: capturing nothing (not the default output)");
        else Log.Info("surround capture: an output device to capture is available again");
        DeviceChanged?.Invoke(state == CaptureState.Capturing ? DeviceName ?? "" : "");
    }

    private void Run()
    {
        using var mmcss = Native.EnterMmcss("Audio");
        while (!_stop)
        {
            try
            {
                CaptureDevice();
            }
            catch (Exception ex) when (!_stop)
            {
                Log.Warn($"surround capture: {ex.Message}; retrying");
                Thread.Sleep(1000);
            }
            catch (Exception)
            {
                break; // failed while being stopped: an unhandled one would end the app
            }
        }
    }

    /// <summary>
    /// Capture until stopped, the device to capture changes (the default output while following it, or the setting), the
    /// chosen device goes away, or the device is invalidated (also when its speaker setup changes in Windows).
    /// </summary>
    private void CaptureDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        LoopbackStream? endpoint = null;
        IntPtr ready = _ready.SafeWaitHandle.DangerousGetHandle();
        try
        {
            _devicesChanged = false;
            NoteBlocked();
            string? chosen = _captureDeviceId();
            bool follow = CaptureEndpoint.FollowsDefault(chosen);
            if (follow)
            {
                int found = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
                if (found == CoreAudio.NotFound) device = null;
                else CoreAudio.Check(found, "default device");
            }
            else
            {
                device = AudioEndpoints.Open(enumerator, EDataFlow.Render, chosen); // never the default instead
            }
            string? deviceId = null;
            if (device != null && device.GetId(out var id) >= 0) deviceId = id;
            var target = CaptureEndpoint.Resolve(chosen, follow ? deviceId : null, chosenActive: !follow && deviceId != null);
            if (target.State != CaptureState.Capturing || device == null || deviceId == null)
            {
                _endpointId = null;
                SetState(target.State == CaptureState.Capturing ? CaptureState.NoOutputDevice : target.State);
                _outcome = new(L.T("Windows 输出设备不可用"));
                WaitForDevices(chosen);
                return;
            }
            SetState(CaptureState.Capturing);
            string name = CoreAudio.FriendlyName(device);

            endpoint = LoopbackStream.OpenEndpoint(device, ready, BufferDuration);
            var fmt = endpoint.Format;
            var layout = SurroundLayout.For(fmt, out string? refusal);
            if (layout == null)
            {
                // Nothing is written: a stereo endpoint's sound goes to the HomePod over the stereo path instead.
                if (_layoutProblem != refusal)
                    Log.Info($"surround capture: \"{name}\" {fmt.SampleRate} Hz {fmt.Channels} ch mask 0x{fmt.ChannelMask:X} is not 5.1 or 7.1");
                _layoutProblem = refusal;
                _outcome = new(refusal);
                WaitForDevices(chosen);
                return;
            }
            _layoutProblem = null;
            _endpointId = deviceId;
            DeviceName = name;
            Log.Info($"surround capture: \"{name}\" {fmt.SampleRate} Hz {(fmt.IsFloat ? "float" : "pcm")}{fmt.BitsPerSample}, " +
                     $"{layout.Describe}{(follow ? "" : ", chosen capture device")}");
            DeviceChanged?.Invoke(name);

            var resampler = new SurroundResampler(fmt.SampleRate, _outRate);
            var drained = new List<float>(fmt.SampleRate / 10 * SurroundLayout.Slots);
            var resampled = new List<float>(_outRate / 10 * SurroundLayout.Slots);
            endpoint.Start();
            _outcome = Capturing51;
            long nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
            long lastData = Stopwatch.GetTimestamp();

            while (!_stop)
            {
                _ready.WaitOne(20); // loopback signals nothing while the endpoint is silent
                long now = Stopwatch.GetTimestamp();

                drained.Clear();
                if (!endpoint.DrainSurround(drained, layout)) return; // invalidated: open it again (and check its layout)
                if (drained.Count > 0)
                {
                    if (now - lastData > ResumeGap) resampler.Reset(); // resume after silence, all three pairs together
                    else if (now - lastData > Volatile.Read(ref _maxGapTicks)) Volatile.Write(ref _maxGapTicks, now - lastData);
                    lastData = now;
                    resampled.Clear();
                    resampler.Process(CollectionsMarshal.AsSpan(drained), resampled);
                    var output = CollectionsMarshal.AsSpan(resampled);
                    if (output.Length > 0)
                    {
                        _fifo.Write(output);
                        SteerDrift(resampler);
                        TrackPeak(output);
                    }
                }

                string? choice = _captureDeviceId();
                bool choiceChanged = !CaptureEndpoint.SameChoice(choice, chosen);
                if (choiceChanged || _devicesChanged || now >= nextDeviceCheck)
                {
                    _devicesChanged = false;
                    nextDeviceCheck = now + DeviceCheckInterval;
                    NoteBlocked();
                    string? defaultId = CaptureEndpoint.FollowsDefault(choice) ? AudioEndpoints.DefaultId(enumerator, EDataFlow.Render) : null;
                    if (CaptureEndpoint.ShouldReopen(choice, deviceId, defaultId, AudioEndpoints.IsActive(device)))
                    {
                        Log.Info(choiceChanged ? "surround capture: capture device setting changed, switching"
                            : CaptureEndpoint.FollowsDefault(choice) ? "surround capture: default output device changed, switching"
                            : "surround capture: chosen capture device is no longer available");
                        return;
                    }
                    chosen = choice;
                }
            }
        }
        finally
        {
            _endpointId = null;
            endpoint?.Dispose();
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Another reason 5.1 can't be captured (per-app routing came on) ends the session; logged once per change.</summary>
    private void NoteBlocked()
    {
        string? blocked = _blocked();
        if (blocked != _blockedProblem && blocked != null) Log.Info($"surround capture: no longer 5.1: {blocked}");
        _blockedProblem = blocked;
    }

    /// <summary>Look again in 2 s, or as soon as a device comes or goes or another one is chosen.</summary>
    private void WaitForDevices(string? chosen)
    {
        for (int i = 0; i < 20 && !_stop && !_devicesChanged && CaptureEndpoint.SameChoice(_captureDeviceId(), chosen); i++)
            Thread.Sleep(100);
    }

    private void SteerDrift(SurroundResampler resampler)
    {
        _avgDepth += 0.02 * (_fifo.Depth - _avgDepth);
        double errorSeconds = (_avgDepth - _fifo.TargetFrames) / _outRate;
        resampler.Adjust = Math.Clamp(errorSeconds * 0.02, -500e-6, 500e-6);
        DriftPpm = resampler.Adjust * 1e6;
    }

    private void TrackPeak(ReadOnlySpan<float> samples)
    {
        float peak = Peak * 0.95f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        Peak = peak;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        AudioDeviceWatcher.Changed -= OnDevicesChanged;
        _ready.Set();
        if (_thread.IsAlive) _thread.Join(1000);
    }
}

/// <summary>
/// 5.1 frames (6 floats) from the device rate to the stream rate: <see cref="Resampler"/> is stereo, so three of them run
/// over the pairs (FL,FR), (FC,LFE), (Ls,Rs) with one shared ratio trim (the <see cref="CinemaFileSource"/> pattern). Fed
/// the same frames with the same trim they produce the same counts; a frame is only put out once all three have it, so
/// the channels can never slip against each other.
/// </summary>
internal sealed class SurroundResampler(int inputRate, int outputRate)
{
    private const int Pairs = SurroundLayout.Slots / 2;
    private readonly Resampler[] _pairs = [new(inputRate, outputRate), new(inputRate, outputRate), new(inputRate, outputRate)];
    private readonly List<float> _input = new(4096);
    private readonly List<float>[] _output = [new(4096), new(4096), new(4096)];

    /// <summary>Relative speed correction for all channels (<see cref="Resampler.Adjust"/>).</summary>
    public double Adjust
    {
        get => _pairs[0].Adjust;
        set { foreach (var r in _pairs) r.Adjust = value; }
    }

    /// <summary>After a gap: all pairs start afresh together, and nothing half-done is kept.</summary>
    public void Reset()
    {
        foreach (var r in _pairs) r.Reset();
        foreach (var o in _output) o.Clear();
    }

    /// <summary>Appends the resampled 6-float frames to <paramref name="output"/>; returns how many frames it appended.</summary>
    public int Process(ReadOnlySpan<float> frames, List<float> output)
    {
        int count = frames.Length / SurroundLayout.Slots;
        for (int p = 0; p < Pairs; p++)
        {
            _input.Clear();
            for (int f = 0; f < count; f++)
            {
                _input.Add(frames[f * SurroundLayout.Slots + p * 2]);
                _input.Add(frames[f * SurroundLayout.Slots + p * 2 + 1]);
            }
            _pairs[p].Process(CollectionsMarshal.AsSpan(_input), _output[p]);
        }
        int ready = Math.Min(_output[0].Count, Math.Min(_output[1].Count, _output[2].Count)) / 2;
        for (int f = 0; f < ready; f++)
            for (int p = 0; p < Pairs; p++)
            {
                output.Add(_output[p][f * 2]);
                output.Add(_output[p][f * 2 + 1]);
            }
        foreach (var o in _output) o.RemoveRange(0, ready * 2);
        return ready;
    }
}
