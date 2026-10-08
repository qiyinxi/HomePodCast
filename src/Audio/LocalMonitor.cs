using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Plays a live source (the mic after its effects) on a local output, e.g. headphones, with as little
/// delay as WASAPI shared mode allows: IAudioClient3 at the engine's minimum period when the driver
/// supports it, the device buffer kept at two periods, and a small drift-steered FIFO in between whose
/// target grows by 1 ms after any dropout. Never falls back to another device if the chosen one is gone
/// (the fallback could be a speaker next to the mic).
/// </summary>
public sealed class LocalMonitor : IDisposable
{
    private static readonly long DeviceCheckInterval = Stopwatch.Frequency;
    private const int MaxExtraMs = 20;

    private readonly ITapSource _source;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile AudioTap? _tap;
    private double _fifoMs, _outputMs;

    /// <summary>Requested output device id; null = the default output.</summary>
    public string? DeviceId { get; }

    public string? DeviceName { get; private set; }
    public int DeviceRate { get; private set; }
    public int PeriodFrames { get; private set; }
    public double PeriodMs { get; private set; }
    public uint BufferFrames { get; private set; }
    /// <summary>True when IAudioClient3 gave us a period below the engine's default (usually 10 ms).</summary>
    public bool LowLatency { get; private set; }

    /// <summary>"IAudioClient3" or "IAudioClient".</summary>
    public string? ClientMode { get; private set; }
    public double StreamLatencyMs { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }

    public long Underruns => _tap?.Underruns ?? 0;

    /// <summary>Average time the sound waits in the monitor FIFO (ms).</summary>
    public double FifoMs => Volatile.Read(ref _fifoMs);

    /// <summary>Average time the sound waits in the output device buffer (ms).</summary>
    public double OutputMs => Volatile.Read(ref _outputMs);

    /// <summary>
    /// Mic-in to headphone-out estimate (ms): capture side + resampling + FIFO + output buffer, all
    /// measured while running, plus the output stream latency reported by WASAPI. Converter and driver
    /// delays below WASAPI (typically 1–5 ms) are not included.
    /// </summary>
    public double EstimatedLatencyMs => Running
        ? _source.LatencyMs + (_tap?.ResamplerDelayMs ?? 0) + FifoMs + OutputMs + StreamLatencyMs
        : 0;

    /// <summary>Raised on the monitor thread when the device opens, fails or closes.</summary>
    public event Action? StatusChanged;

    public LocalMonitor(ITapSource source, string? deviceId = null)
    {
        _source = source;
        DeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        _thread = new Thread(Run) { IsBackground = true, Name = "Local monitor", Priority = ThreadPriority.Highest };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        using var mmcss = Native.EnterMmcss("Pro Audio");
        while (!_stop)
        {
            try
            {
                RenderOnce();
            }
            catch (Exception ex) when (!_stop)
            {
                Error = ex.Message;
                Running = false;
                StatusChanged?.Invoke();
                Log.Warn($"monitor: {ex.Message}; retrying");
                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
            }
        }
        Running = false;
        StatusChanged?.Invoke();
    }

    private unsafe void RenderOnce()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        SharedStream? stream = null;
        IAudioRenderClient? render = null;
        AudioTap? tap = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            device = AudioEndpoints.Open(enumerator, EDataFlow.Render, DeviceId)
                     ?? throw new InvalidOperationException(DeviceId == null ? L.T("没有可用的输出设备") : L.T("找不到所选的监听设备"));
            device.GetId(out var openedId);
            DeviceName = CoreAudio.FriendlyName(device);
            stream = SharedStream.Open(device, ready.SafeWaitHandle.DangerousGetHandle());
            var fmt = stream.Format;
            CoreAudio.Check(stream.Client.GetService(ref CoreAudio.IidAudioRenderClient, out var svc), "render client");
            render = (IAudioRenderClient)svc;

            int rate = fmt.SampleRate;
            DeviceRate = rate;
            PeriodFrames = stream.PeriodFrames;
            PeriodMs = stream.PeriodMs;
            BufferFrames = stream.BufferFrames;
            LowLatency = stream.LowLatency;
            ClientMode = stream.Mode;
            StreamLatencyMs = stream.StreamLatencyMs;

            // Keep two periods queued in the device: one playing, one ready.
            int fill = (int)Math.Min(stream.BufferFrames, (uint)Math.Max(2 * stream.PeriodFrames, rate / 500));
            // The FIFO's average depth before a read must cover the read (one period), half a source packet
            // (packets arrive in bursts) and some scheduling jitter.
            double targetMs = PeriodMs + _source.ChunkMs / 2 + 2;
            tap = new AudioTap(_source.Rate, rate, 0, 0)
            {
                TargetFrames = (int)(targetMs * rate / 1000),
                CapFrames = (int)((targetMs + 2 * _source.ChunkMs + MaxExtraMs + 10) * rate / 1000),
            };
            int baseTarget = tap.TargetFrames;
            _tap = tap;
            _source.Attach(tap);
            var pcm = new float[stream.BufferFrames * 2];
            Volatile.Write(ref _fifoMs, targetMs);
            Volatile.Write(ref _outputMs, fill * 1000.0 / rate);
            Log.Info($"monitor \"{DeviceName}\" {rate} Hz {fmt.Channels} ch, {stream.Mode} period {PeriodFrames} frames " +
                     $"({PeriodMs:F2} ms), buffer {BufferFrames}, fill {fill}, fifo target {targetMs:F1} ms, " +
                     $"stream latency {StreamLatencyMs:F1} ms");

            CoreAudio.Check(render.GetBuffer((uint)fill, out _), "prefill");
            render.ReleaseBuffer((uint)fill, CoreAudio.BufferFlagsSilent);
            CoreAudio.Check(stream.Client.Start(), "start");
            Running = true;
            Error = null;
            StatusChanged?.Invoke();

            long started = Stopwatch.GetTimestamp();
            long nextDeviceCheck = started + DeviceCheckInterval;
            long seenUnderruns = 0;
            while (!_stop)
            {
                ready.WaitOne(100);
                int hr = stream.Client.GetCurrentPadding(out uint padding);
                if (hr == CoreAudio.DeviceInvalidated) return;
                CoreAudio.Check(hr, "padding");
                int want = fill - (int)padding;
                if (want > 0)
                {
                    var span = pcm.AsSpan(0, want * 2);
                    tap.Read(span);
                    hr = render.GetBuffer((uint)want, out var data);
                    if (hr == CoreAudio.DeviceInvalidated) return;
                    CoreAudio.Check(hr, "get buffer");
                    WriteFrames(data, span, want, fmt);
                    render.ReleaseBuffer((uint)want, 0);

                    // Time-averaged waits: FIFO depth before this read minus half a read, and the device
                    // queue after this write minus half a period.
                    double fifo = (tap.AverageDepth - want / 2.0) * 1000 / rate;
                    double output = ((int)padding + want - PeriodFrames / 2.0) * 1000 / rate;
                    Volatile.Write(ref _fifoMs, _fifoMs + 0.02 * (Math.Max(0, fifo) - _fifoMs));
                    Volatile.Write(ref _outputMs, _outputMs + 0.02 * (output - _outputMs));
                }

                if (tap.Underruns != seenUnderruns)
                {
                    seenUnderruns = tap.Underruns;
                    // After the start-up, every dropout buys 1 ms more FIFO (up to +20 ms).
                    if (Stopwatch.GetTimestamp() - started > Stopwatch.Frequency / 2 &&
                        tap.TargetFrames < baseTarget + MaxExtraMs * rate / 1000)
                        tap.TargetFrames += rate / 1000;
                }

                if (DeviceId == null && Stopwatch.GetTimestamp() >= nextDeviceCheck)
                {
                    nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
                    if (AudioEndpoints.DefaultId(enumerator, EDataFlow.Render) is { } current && current != openedId)
                    {
                        Log.Info("default output changed, monitor switching");
                        return;
                    }
                }
            }
        }
        finally
        {
            Running = false;
            if (tap != null) _source.Detach(tap);
            if (render != null) Marshal.ReleaseComObject(render);
            stream?.Dispose();
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Stereo float → device format; extra channels silent, mono devices get (L+R)/2. Hard-clipped at ±1.</summary>
    private static unsafe void WriteFrames(IntPtr data, ReadOnlySpan<float> stereo, int frames, WaveFormat fmt)
    {
        int bytes = fmt.BitsPerSample / 8;
        for (int i = 0; i < frames; i++)
        {
            byte* f = (byte*)data + (long)i * fmt.BlockAlign;
            float l = Math.Clamp(stereo[i * 2], -1f, 1f), r = Math.Clamp(stereo[i * 2 + 1], -1f, 1f);
            for (int c = 0; c < fmt.Channels; c++)
            {
                float v = fmt.Channels == 1 ? (l + r) * 0.5f : c == 0 ? l : c == 1 ? r : 0f;
                byte* s = f + c * bytes;
                if (fmt.IsFloat) *(float*)s = v;
                else if (bytes == 2) *(short*)s = (short)(v * 32767);
                else if (bytes == 4) *(int*)s = (int)(v * 2147483647.0);
                else if (bytes == 3)
                {
                    int x = (int)(v * 8388607);
                    s[0] = (byte)x; s[1] = (byte)(x >> 8); s[2] = (byte)(x >> 16);
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(2000);
    }
}
