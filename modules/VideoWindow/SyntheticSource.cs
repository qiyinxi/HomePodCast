namespace HomePodCast.VideoWindow;

/// <summary>
/// Generated 3840 × 2160 pictures at 30 fps through the same scaler and queue as a capture (a white bar that moves over
/// four colours): the CI smoke test (gui-smoke) and the benchmark use it instead of a captured window, so neither needs
/// a picker, a desktop or anyone's windows.
/// </summary>
internal sealed unsafe class SyntheticSource : IFrameSource
{
    public const int Width = 3840, Height = 2160;
    private static readonly uint[] Colours = [0xff1e5aa8, 0xff2f8f4e, 0xffa83a1e, 0xff6b3fa0];

    private readonly FrameScaler _scaler;
    private readonly DelayQueue<GpuFrame> _queue;
    private readonly nint[] _pictures;
    private readonly System.Threading.Timer _timer;
    private int _delayMs, _next, _busy;
    public event Action<string>? Failed;
#pragma warning disable CS0067 // a generated picture never closes
    public event Action? Closed;
#pragma warning restore CS0067
    public int DelayMs { set => Volatile.Write(ref _delayMs, value); }

    public SyntheticSource(Gpu gpu, FrameScaler scaler, DelayQueue<GpuFrame> queue)
    {
        _scaler = scaler; _queue = queue;
        _pictures = Pictures(gpu, Width, Height);
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, 33);
    }

    /// <summary>Four pictures, each a colour with the white bar a quarter further across.</summary>
    public static nint[] Pictures(Gpu gpu, int width, int height)
    {
        var pixels = new uint[width * height];
        var pictures = new nint[Colours.Length];
        for (int i = 0; i < pictures.Length; i++)
        {
            Array.Fill(pixels, Colours[i]);
            int bar = i * width / pictures.Length, barWidth = width / 16;
            for (int y = 0; y < height; y++) Array.Fill(pixels, 0xffffffff, y * width + bar, barWidth);
            fixed (uint* p = pixels) pictures[i] = gpu.CreateTexture(width, height, data: p, pitch: (uint)width * 4);
        }
        return pictures;
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var frame = _scaler.Scale(_pictures[_next++ % _pictures.Length], Width, Height);
            _queue.Add(Clock.Now, frame.Bytes, frame);
        }
        catch (Exception ex) { Failed?.Invoke(Failure.Capture(ex)); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        using (var done = new ManualResetEvent(false))
            if (_timer.Dispose(done)) done.WaitOne(2000);
        for (int i = 0; i < _pictures.Length; i++) Gpu.Release(ref _pictures[i]);
    }
}
