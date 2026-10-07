using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Plays a short click once per second through the normal Windows output — exactly the path a game's
/// audio takes — and reports when each click reaches the audio engine, so a visual flash can be shown
/// at the same moment. Used for the audio/video sync test.
/// </summary>
public sealed class ClickRenderer : IDisposable
{
    private const long BufferDuration = 100_000; // 10 ms
    private readonly Thread _thread;
    private volatile bool _stop;

    /// <summary>Raised from the render thread with the QPC time at which a click starts playing.</summary>
    public event Action<long>? ClickScheduled;

    public ClickRenderer()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Click renderer", Priority = ThreadPriority.Highest };
    }

    public void Start() => _thread.Start();

    private unsafe void Run()
    {
        using var mmcss = Native.EnterMmcss("Pro Audio");
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        IntPtr mix = IntPtr.Zero;
        using var ready = new AutoResetEvent(false);
        try
        {
            CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device), "device");
            CoreAudio.Check(device.Activate(ref CoreAudio.IidAudioClient, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "activate");
            client = (IAudioClient)obj;
            CoreAudio.Check(client.GetMixFormat(out mix), "mix");
            var fmt = WaveFormat.FromPointer(mix);
            CoreAudio.Check(client.Initialize(0, CoreAudio.StreamFlagsEventCallback | CoreAudio.StreamFlagsNoPersist,
                BufferDuration, 0, mix, IntPtr.Zero), "init");
            CoreAudio.Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "event");
            CoreAudio.Check(client.GetBufferSize(out uint bufferFrames), "size");
            CoreAudio.Check(client.GetService(ref CoreAudio.IidAudioRenderClient, out var svc), "render");
            render = (IAudioRenderClient)svc;

            int rate = fmt.SampleRate;
            int clickLen = rate * 25 / 1000;  // 25 ms 1 kHz burst with a short fade
            long written = 0;                  // frames handed to the engine
            long nextClick = rate / 2;
            CoreAudio.Check(client.Start(), "start");

            while (!_stop)
            {
                ready.WaitOne(50);
                CoreAudio.Check(client.GetCurrentPadding(out uint padding), "padding");
                uint frames = bufferFrames - padding;
                if (frames == 0) continue;
                CoreAudio.Check(render.GetBuffer(frames, out var data), "buffer");

                long now = Stopwatch.GetTimestamp();
                for (uint i = 0; i < frames; i++)
                {
                    long pos = written + i;
                    if (pos == nextClick)
                    {
                        // This frame starts playing once everything queued before it has played.
                        long when = now + (long)((padding + i) * (double)Stopwatch.Frequency / rate);
                        ClickScheduled?.Invoke(when);
                    }
                    long into = pos - nextClick;
                    float s = 0;
                    if (into >= 0 && into < clickLen)
                    {
                        float env = Math.Min(1f, Math.Min(into, clickLen - into) / (rate * 0.002f));
                        s = 0.5f * env * MathF.Sin(2 * MathF.PI * 1000 * into / rate);
                    }
                    if (into == clickLen) nextClick += rate;
                    WriteFrame(data, (int)i, fmt, s);
                }
                render.ReleaseBuffer(frames, 0);
                written += frames;
            }
            client.Stop();
        }
        catch (Exception ex) when (!_stop)
        {
            Log.Warn($"click renderer: {ex.Message}");
        }
        finally
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            if (render != null) Marshal.ReleaseComObject(render);
            if (client != null) Marshal.ReleaseComObject(client);
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static unsafe void WriteFrame(IntPtr data, int frame, WaveFormat fmt, float value)
    {
        byte* f = (byte*)data + (long)frame * fmt.BlockAlign;
        int bytes = fmt.BitsPerSample / 8;
        for (int c = 0; c < fmt.Channels; c++)
        {
            byte* s = f + c * bytes;
            if (fmt.IsFloat) *(float*)s = value;
            else if (bytes == 2) *(short*)s = (short)(value * 32767);
            else if (bytes == 4) *(int*)s = (int)(value * int.MaxValue);
            else if (bytes == 3)
            {
                int v = (int)(value * 8388607);
                s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1000);
    }
}
