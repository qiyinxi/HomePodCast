using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Event-driven WASAPI loopback capture of the default render device, resampled to the stream rate
/// and written into the FIFO. Follows default-device changes and steers the resampler ratio from the
/// FIFO depth so the sound-card clock never drifts against the network timeline.
/// </summary>
public sealed class LoopbackCapture : ICaptureSource
{
    private const long BufferDuration = 200_000; // 20 ms in 100 ns units
    private static readonly long DeviceCheckInterval = Stopwatch.Frequency; // 1 s

    private readonly AudioFifo _fifo;
    private readonly int _outRate;
    private readonly Thread _thread;
    private volatile bool _stop;
    private double _avgDepth;

    public string? DeviceName { get; private set; }
    public int DeviceRate { get; private set; }
    public int DeviceChannels { get; private set; }
    public double DriftPpm { get; private set; }
    public float Peak { get; private set; }
    public double ProportionalGain { get; set; } = 0.02;
    public int ExtraLatencyMs => 0;

    public event Action<string>? DeviceChanged;

    public LoopbackCapture(AudioFifo fifo, int outRate)
    {
        _fifo = fifo;
        _outRate = outRate;
        _avgDepth = fifo.TargetFrames;
        _thread = new Thread(Run) { IsBackground = true, Name = "Loopback capture", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        using var mmcss = Native.EnterMmcss("Audio");
        while (!_stop)
        {
            try
            {
                CaptureDefaultDevice();
            }
            catch (Exception ex) when (!_stop)
            {
                Log.Warn($"capture: {ex.Message}; retrying");
                Thread.Sleep(1000);
            }
            catch (Exception)
            {
                break; // failed while being stopped: an unhandled one would end the app
            }
        }
    }

    /// <summary>Capture until stopped or the default device changes / disappears.</summary>
    private void CaptureDefaultDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr mix = IntPtr.Zero;
        using var ready = new AutoResetEvent(false);
        try
        {
            CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device), "default device");
            device.GetId(out var deviceId);
            DeviceName = CoreAudio.FriendlyName(device);

            CoreAudio.Check(device.Activate(ref CoreAudio.IidAudioClient, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "activate");
            client = (IAudioClient)obj;
            CoreAudio.Check(client.GetMixFormat(out mix), "mix format");
            var fmt = WaveFormat.FromPointer(mix);
            DeviceRate = fmt.SampleRate;
            DeviceChannels = fmt.Channels;

            CoreAudio.Check(client.Initialize(0,
                CoreAudio.StreamFlagsLoopback | CoreAudio.StreamFlagsEventCallback | CoreAudio.StreamFlagsNoPersist,
                BufferDuration, 0, mix, IntPtr.Zero), "initialize");
            CoreAudio.Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "event");
            CoreAudio.Check(client.GetService(ref CoreAudio.IidAudioCaptureClient, out var svc), "capture client");
            capture = (IAudioCaptureClient)svc;

            var resampler = new Resampler(fmt.SampleRate, _outRate);
            var stereo = new float[fmt.SampleRate / 10 * 2];
            var resampled = new List<float>(_outRate / 10 * 2);
            Log.Info($"capturing \"{DeviceName}\" {fmt.SampleRate} Hz {fmt.Channels} ch " +
                     $"{(fmt.IsFloat ? "float" : "pcm")}{fmt.BitsPerSample}");
            DeviceChanged?.Invoke(DeviceName);

            CoreAudio.Check(client.Start(), "start");
            long nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
            long lastData = Stopwatch.GetTimestamp();

            while (!_stop)
            {
                ready.WaitOne(20); // loopback signals nothing while the endpoint is silent

                while (true)
                {
                    int hr = capture.GetNextPacketSize(out uint pending);
                    if (hr == CoreAudio.DeviceInvalidated) return;
                    CoreAudio.Check(hr, "packet size");
                    if (pending == 0) break;

                    hr = capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _);
                    if (hr == CoreAudio.DeviceInvalidated) return;
                    CoreAudio.Check(hr, "get buffer");

                    long now = Stopwatch.GetTimestamp();
                    if (now - lastData > Stopwatch.Frequency / 5) resampler.Reset(); // resume after silence
                    else NoteGap(now - lastData);
                    lastData = now;

                    if (stereo.Length < frames * 2) stereo = new float[frames * 2];
                    var span = stereo.AsSpan(0, (int)frames * 2);
                    if ((flags & CoreAudio.BufferFlagsSilent) != 0) span.Clear();
                    else ToStereo(data, (int)frames, fmt, span);
                    capture.ReleaseBuffer(frames);

                    resampled.Clear();
                    resampler.Process(span, resampled);
                    _fifo.Write(CollectionsMarshal.AsSpan(resampled));
                    TrackPeak(CollectionsMarshal.AsSpan(resampled));
                    SteerDrift(resampler);
                }

                if (Stopwatch.GetTimestamp() >= nextDeviceCheck)
                {
                    nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
                    if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var current) >= 0)
                    {
                        current.GetId(out var currentId);
                        Marshal.ReleaseComObject(current);
                        if (currentId != deviceId)
                        {
                            Log.Info("default output device changed, switching");
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            try { client?.Stop(); } catch { }
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private long _maxGapTicks;

    /// <summary>Longest wait between two packets while audio was flowing (quiet spells excluded).</summary>
    private void NoteGap(long ticks)
    {
        if (ticks > Volatile.Read(ref _maxGapTicks)) Volatile.Write(ref _maxGapTicks, ticks);
    }

    /// <summary>The longest gap between capture packets since the last call, in ms (for the minute stats).</summary>
    public double TakeMaxGapMs() => Interlocked.Exchange(ref _maxGapTicks, 0) * 1000.0 / Stopwatch.Frequency;

    private void SteerDrift(Resampler resampler)
    {
        _avgDepth += 0.02 * (_fifo.Depth - _avgDepth);
        double errorSeconds = (_avgDepth - _fifo.TargetFrames) / _outRate;
        resampler.Adjust = Math.Clamp(errorSeconds * ProportionalGain, -500e-6, 500e-6);
        DriftPpm = resampler.Adjust * 1e6;
    }

    private void TrackPeak(ReadOnlySpan<float> samples)
    {
        float peak = Peak * 0.95f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        Peak = peak;
    }

    internal static unsafe void ToStereo(IntPtr data, int frames, WaveFormat fmt, Span<float> dst)
    {
        int ch = fmt.Channels;
        byte* p = (byte*)data;
        Span<float> frame = stackalloc float[Math.Max(ch, 2)];
        for (int i = 0; i < frames; i++)
        {
            byte* f = p + (long)i * fmt.BlockAlign;
            for (int c = 0; c < ch; c++) frame[c] = ReadSample(f, c, fmt);

            float l, r;
            if (ch == 1) l = r = frame[0];
            else
            {
                l = frame[0];
                r = frame[1];
                if (ch >= 3) { l += 0.707f * frame[2]; r += 0.707f * frame[2]; }       // centre
                if (ch >= 6) { l += 0.5f * frame[4]; r += 0.5f * frame[5]; }            // back/side
                if (ch >= 8) { l += 0.5f * frame[6]; r += 0.5f * frame[7]; }            // side
            }
            dst[i * 2] = l;
            dst[i * 2 + 1] = r;
        }
    }

    private static unsafe float ReadSample(byte* frame, int channel, WaveFormat fmt)
    {
        int bytes = fmt.BitsPerSample / 8;
        byte* s = frame + channel * bytes;
        if (fmt.IsFloat) return *(float*)s;
        return bytes switch
        {
            2 => *(short*)s / 32768f,
            3 => (s[0] << 8 | s[1] << 16 | s[2] << 24) / 2147483648f,
            4 => *(int*)s / 2147483648f,
            _ => 0f,
        };
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1000);
    }
}
