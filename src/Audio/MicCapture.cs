using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Microphone input for the voice chain: event-driven shared-mode WASAPI capture from a chosen input
/// (null = the default one, followed when it changes) at the smallest period the driver offers
/// (IAudioClient3), first channel only → input gain → optional noise gate → 44.1 kHz stereo → the effect
/// chain → every attached <see cref="AudioTap"/> (the HomePod mixer, the local monitor).
/// Only levels are measured; the audio itself is never stored or analysed.
/// </summary>
public sealed class MicCapture : ITapSource, IDisposable
{
    public const int OutputRate = 44100;
    private static readonly long DeviceCheckInterval = Stopwatch.Frequency;

    private readonly Thread _thread;
    private readonly object _tapLock = new();
    private volatile AudioTap[] _taps = [];
    private volatile IStereoEffect[] _effects = [];
    private volatile bool _stop;
    private volatile MicPipeline? _pipeline;
    private double _ageMs;

    /// <summary>Requested input device id; null = the default input.</summary>
    public string? DeviceId { get; }

    public string? DeviceName { get; private set; }
    public int DeviceRate { get; private set; }
    public int DeviceChannels { get; private set; }
    public string? FormatText { get; private set; }
    public int PeriodFrames { get; private set; }
    public double PeriodMs { get; private set; }

    /// <summary>True when IAudioClient3 gave us a period below the engine's default (usually 10 ms).</summary>
    public bool LowLatency { get; private set; }

    /// <summary>"IAudioClient3" or "IAudioClient".</summary>
    public string? ClientMode { get; private set; }

    public double StreamLatencyMs { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Input level after gain, before the gate (decaying peak, 0..1).</summary>
    public float Peak { get; private set; }

    /// <summary>RMS of the last packet after gain.</summary>
    public float Rms { get; private set; }

    public bool GateOpen => _pipeline?.Gate.IsOpen ?? true;

    public float GainDb { get; set; }
    public bool GateEnabled { get; set; }
    public float GateThresholdDb { get; set; } = -50;

    /// <summary>Effects run in order on the capture thread at 44.1 kHz stereo (e.g. mic EQ, reverb).</summary>
    public IStereoEffect[] Effects { get => _effects; set => _effects = value ?? []; }

    public int Rate => OutputRate;
    public double ChunkMs => Running ? PeriodMs : 0;
    public double LatencyMs => Volatile.Read(ref _ageMs) + (_pipeline?.ResamplerDelayMs ?? 0);

    /// <summary>Raised on the capture thread when the device opens, fails or closes.</summary>
    public event Action? StatusChanged;

    public MicCapture(string? deviceId = null)
    {
        DeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        _thread = new Thread(Run) { IsBackground = true, Name = "Mic capture", Priority = ThreadPriority.Highest };
    }

    public void Start() => _thread.Start();

    public void Attach(AudioTap tap)
    {
        if (tap.InputRate != OutputRate) throw new ArgumentException("tap input rate must be 44.1 kHz", nameof(tap));
        lock (_tapLock)
        {
            if (_taps.Contains(tap)) return;
            tap.Clear();
            _taps = [.. _taps, tap];
        }
    }

    public void Detach(AudioTap tap)
    {
        lock (_tapLock) _taps = _taps.Where(t => t != tap).ToArray();
    }

    private void Run()
    {
        using var mmcss = Native.EnterMmcss("Pro Audio");
        while (!_stop)
        {
            try
            {
                CaptureOnce();
            }
            catch (Exception ex) when (!_stop)
            {
                Error = ex.Message;
                Running = false;
                StatusChanged?.Invoke();
                Log.Warn($"mic: {ex.Message}; retrying");
                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
            }
        }
        Running = false;
        StatusChanged?.Invoke();
    }

    /// <summary>Capture until stopped, the device goes away, or (following the default) the default changes.</summary>
    private unsafe void CaptureOnce()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        SharedStream? stream = null;
        IAudioCaptureClient? capture = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            device = AudioEndpoints.Open(enumerator, EDataFlow.Capture, DeviceId)
                     ?? throw new InvalidOperationException(DeviceId == null ? L.T("没有可用的麦克风") : L.T("找不到所选的麦克风"));
            device.GetId(out var openedId);
            DeviceName = CoreAudio.FriendlyName(device);
            stream = SharedStream.Open(device, ready.SafeWaitHandle.DangerousGetHandle());
            var fmt = stream.Format;
            CoreAudio.Check(stream.Client.GetService(ref CoreAudio.IidAudioCaptureClient, out var svc), "capture client");
            capture = (IAudioCaptureClient)svc;

            DeviceRate = fmt.SampleRate;
            DeviceChannels = fmt.Channels;
            FormatText = $"{fmt.SampleRate} Hz {fmt.Channels} ch {(fmt.IsFloat ? "float" : "pcm")}{fmt.BitsPerSample}";
            PeriodFrames = stream.PeriodFrames;
            PeriodMs = stream.PeriodMs;
            LowLatency = stream.LowLatency;
            ClientMode = stream.Mode;
            StreamLatencyMs = stream.StreamLatencyMs;
            var pipeline = new MicPipeline(fmt, OutputRate);
            _pipeline = pipeline;
            Volatile.Write(ref _ageMs, PeriodMs);
            Log.Info($"mic \"{DeviceName}\" {FormatText}, {stream.Mode} period {PeriodFrames} frames ({PeriodMs:F2} ms), " +
                     $"buffer {stream.BufferFrames}, stream latency {StreamLatencyMs:F1} ms");

            CoreAudio.Check(stream.Client.Start(), "start");
            Running = true;
            Error = null;
            StatusChanged?.Invoke();
            long nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
            double ticksTo100ns = 1e7 / Stopwatch.Frequency;
            int timestamps = 0, badTimestamps = 0;

            while (!_stop)
            {
                ready.WaitOne(100);
                while (true)
                {
                    int hr = capture.GetNextPacketSize(out uint pending);
                    if (hr == CoreAudio.DeviceInvalidated) return;
                    CoreAudio.Check(hr, "packet size");
                    if (pending == 0) break;

                    hr = capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc);
                    if (hr == CoreAudio.DeviceInvalidated) return;
                    CoreAudio.Check(hr, "get buffer");
                    long now = Stopwatch.GetTimestamp();
                    var bytes = new ReadOnlySpan<byte>((void*)data, (int)frames * fmt.BlockAlign);
                    pipeline.Load(bytes, (int)frames, (flags & CoreAudio.BufferFlagsSilent) != 0, GainDb, GateEnabled, GateThresholdDb);
                    capture.ReleaseBuffer(frames);
                    pipeline.Deliver(_effects, _taps);

                    Peak = Math.Max(Peak * 0.9f, pipeline.Peak);
                    Rms = pipeline.Rms;
                    // Age of the packet's middle sample when it reaches the taps, from the device timestamp.
                    double ageMs = (now * ticksTo100ns - qpc) / 10_000.0 - frames * 500.0 / fmt.SampleRate;
                    if ((flags & 0x4) == 0 && qpc != 0 && ageMs is > 0 and < 500) // 0x4: AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR
                    {
                        Volatile.Write(ref _ageMs, _ageMs + 0.05 * (ageMs - _ageMs));
                        timestamps++;
                    }
                    else if (++badTimestamps == 100 && timestamps == 0)
                    {
                        Log.Info($"mic: no usable device timestamps (flags 0x{flags:X}, qpc {qpc}, age {ageMs:F1} ms); " +
                                 "latency estimate assumes one period");
                    }
                }

                if (DeviceId == null && Stopwatch.GetTimestamp() >= nextDeviceCheck)
                {
                    nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
                    if (AudioEndpoints.DefaultId(enumerator, EDataFlow.Capture) is { } current && current != openedId)
                    {
                        Log.Info("default input device changed, switching");
                        return;
                    }
                }
            }
        }
        finally
        {
            Running = false;
            _pipeline = null;
            if (capture != null) Marshal.ReleaseComObject(capture);
            stream?.Dispose();
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(2000);
    }
}

/// <summary>
/// The mic path after WASAPI, separate from the device so it can be tested with synthetic buffers:
/// first channel → gain (glides) → noise gate → stereo → 44.1 kHz → effects → taps.
/// Allocation-free once warmed up (buffers grow only for packets larger than any seen before).
/// </summary>
internal sealed class MicPipeline
{
    private readonly WaveFormat _fmt;
    private readonly Resampler? _resampler;
    private readonly List<float> _resampled;
    private readonly float _gainCoef;
    private float[] _mono = new float[4096];
    private float[] _stereo = new float[8192];
    private int _frames;
    private float _gain = 1;

    public NoiseGate Gate { get; }
    public float Peak { get; private set; }
    public float Rms { get; private set; }
    public double ResamplerDelayMs => _resampler == null ? 0 : 32 * 1000.0 / _fmt.SampleRate;

    public MicPipeline(WaveFormat fmt, int outRate)
    {
        _fmt = fmt;
        if (fmt.SampleRate != outRate) _resampler = new Resampler(fmt.SampleRate, outRate);
        _resampled = new List<float>(outRate / 5 * 2);
        _gainCoef = (float)(1 - Math.Exp(-1 / (0.01 * fmt.SampleRate)));
        Gate = new NoiseGate(fmt.SampleRate);
    }

    /// <summary>Load + Deliver in one go (tests).</summary>
    public void Process(ReadOnlySpan<byte> data, int frames, bool silent, float gainDb, bool gate, float gateDb,
        IStereoEffect[] effects, AudioTap[] taps)
    {
        Load(data, frames, silent, gainDb, gate, gateDb);
        Deliver(effects, taps);
    }

    /// <summary>Copy the packet out of the device buffer: first channel, gain, gate, levels.</summary>
    public void Load(ReadOnlySpan<byte> data, int frames, bool silent, float gainDb, bool gate, float gateDb)
    {
        if (_mono.Length < frames)
        {
            _mono = new float[frames];
            _stereo = new float[frames * 2];
        }
        _frames = frames;
        var mono = _mono.AsSpan(0, frames);
        if (silent) mono.Clear();
        else FirstChannel(data, frames, _fmt, mono);

        float target = MathF.Pow(10, Math.Clamp(gainDb, -60f, 40f) / 20);
        float g = _gain, peak = 0, sum = 0;
        for (int i = 0; i < mono.Length; i++)
        {
            g += (target - g) * _gainCoef;
            float s = mono[i] * g;
            mono[i] = s;
            float a = MathF.Abs(s);
            if (a > peak) peak = a;
            sum += s * s;
        }
        _gain = g;
        Peak = peak;
        Rms = frames > 0 ? MathF.Sqrt(sum / frames) : 0;

        Gate.Enabled = gate;
        Gate.ThresholdDb = gateDb;
        Gate.Process(mono);
    }

    /// <summary>Mono → stereo, resample to 44.1 kHz, run the effects, push to every tap.</summary>
    public void Deliver(IStereoEffect[] effects, AudioTap[] taps)
    {
        var mono = _mono.AsSpan(0, _frames);
        var stereo = _stereo.AsSpan(0, _frames * 2);
        for (int i = 0; i < mono.Length; i++) stereo[i * 2] = stereo[i * 2 + 1] = mono[i];

        Span<float> output = stereo;
        if (_resampler != null)
        {
            _resampled.Clear();
            _resampler.Process(stereo, _resampled);
            output = CollectionsMarshal.AsSpan(_resampled);
        }
        int frames = output.Length / 2;
        if (frames == 0) return;
        foreach (var effect in effects) effect.Process(output, frames);
        foreach (var tap in taps) tap.Write(output);
    }

    /// <summary>Converts the first channel of each frame to float (mono mics, or the left/first capsule).</summary>
    internal static void FirstChannel(ReadOnlySpan<byte> data, int frames, WaveFormat fmt, Span<float> mono)
    {
        int bytes = fmt.BitsPerSample / 8, block = fmt.BlockAlign;
        for (int i = 0; i < frames; i++)
        {
            var s = data.Slice(i * block, bytes);
            mono[i] = fmt.IsFloat
                ? (bytes == 8 ? (float)MemoryMarshal.Read<double>(s) : MemoryMarshal.Read<float>(s))
                : bytes switch
                {
                    1 => (s[0] - 128) / 128f,
                    2 => MemoryMarshal.Read<short>(s) / 32768f,
                    3 => (s[0] << 8 | s[1] << 16 | s[2] << 24) / 2147483648f,
                    4 => MemoryMarshal.Read<int>(s) / 2147483648f,
                    _ => 0f,
                };
        }
    }
}

/// <summary>
/// Simple downward noise gate for a voice mic: opens fast when the level passes the threshold, holds
/// 100 ms, closes 6 dB lower with an 80 ms fade, so breaths and room noise between phrases go quiet.
/// </summary>
public sealed class NoiseGate
{
    private readonly float _envAttack, _envRelease, _gainAttack, _gainRelease;
    private readonly int _holdFrames;
    private float _env, _gain = 1;
    private int _hold;
    private bool _open = true;

    public bool Enabled { get; set; }
    public float ThresholdDb { get; set; } = -50;
    public bool IsOpen => _open;

    public NoiseGate(int sampleRate)
    {
        float Coef(double seconds) => (float)(1 - Math.Exp(-1 / (seconds * sampleRate)));
        _envAttack = Coef(0.001);
        _envRelease = Coef(0.05);
        _gainAttack = Coef(0.002);
        _gainRelease = Coef(0.08);
        _holdFrames = sampleRate / 10;
    }

    public void Process(Span<float> mono)
    {
        bool enabled = Enabled;
        if (!enabled && _gain == 1)
        {
            _open = true;
            return;
        }
        float open = MathF.Pow(10, ThresholdDb / 20), close = open * 0.5f;
        for (int i = 0; i < mono.Length; i++)
        {
            float a = MathF.Abs(mono[i]);
            _env += (a - _env) * (a > _env ? _envAttack : _envRelease);
            if (_env < 1e-12f) _env = 0;

            if (!enabled || _env >= open)
            {
                _open = true;
                _hold = _holdFrames;
            }
            else if (_hold > 0) _hold--;
            else if (_env < close) _open = false;

            float want = _open ? 1 : 0;
            _gain += (want - _gain) * (want > _gain ? _gainAttack : _gainRelease);
            if (_gain < 1e-5f) _gain = 0;          // -100 dB: closed
            else if (_gain > 1 - 1e-6f) _gain = 1;
            mono[i] *= _gain;
        }
    }
}
