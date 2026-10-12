using System.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace HomePodCast.VideoWindow;

internal sealed class CaptureEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly DelayQueue<Bitmap> _queue;
    private D3dReadback? _reader;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private SizeInt32 _size;
    private long _lastFrame;
    private int _delayMs;
    public event Action<string>? Failed;
    public event Action? Closed;
    public int DelayMs { set => Volatile.Write(ref _delayMs, Math.Clamp(value, 0, 2000)); }
    public static long Now => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    public CaptureEngine(GraphicsCaptureItem item, DelayQueue<Bitmap> queue)
    {
        _queue = queue; _item = item; _size = item.Size;
        try
        {
            _reader = new D3dReadback();
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_reader.Device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += FrameArrived;
            item.Closed += ItemClosed;
            _session = _pool.CreateCaptureSession(item);
            _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_pool != sender || _reader == null) return;
            try
            {
                SizeInt32 resize;
                using (var frame = sender.TryGetNextFrame())
                {
                    if (frame == null) return;
                    resize = frame.ContentSize;
                    if (resize.Width <= 0 || resize.Height <= 0) return;
                    if (resize.Width == _size.Width && resize.Height == _size.Height)
                    {
                        long time = frame.SystemRelativeTime.Ticks;
                        // 30 fps normally. Long delays reduce capture cadence to fit ~30 preview frames in 128 MiB.
                        long interval = Math.Max(333_333, (Volatile.Read(ref _delayMs) + 100L) * 10_000 / 30);
                        if (time - _lastFrame < interval) return;
                        _lastFrame = time;
                        var bitmap = _reader.Read(frame.Surface);
                        _queue.Add(time, (long)bitmap.Width * bitmap.Height * 4, bitmap);
                    }
                }
                // Release the outstanding frame before recreating the native pool.
                if (resize.Width != _size.Width || resize.Height != _size.Height)
                {
                    _queue.Clear(); _size = resize; _lastFrame = 0;
                    sender.Recreate(_reader.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, resize);
                }
            }
            catch (Exception ex) { Failed?.Invoke(ex.Message); }
        }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => Closed?.Invoke();
    public void Dispose()
    {
        Direct3D11CaptureFramePool? pool; GraphicsCaptureSession? session; D3dReadback? reader;
        lock (_gate)
        {
            pool = _pool; session = _session; reader = _reader;
            _pool = null; _session = null; _reader = null;
            if (_item != null) { _item.Closed -= ItemClosed; _item = null; }
            if (pool != null) pool.FrameArrived -= FrameArrived;
        }
        // Never wait for WinRT callbacks while holding their capture lock.
        session?.Dispose(); pool?.Dispose(); reader?.Dispose();
    }
}
