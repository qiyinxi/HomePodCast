using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace HomePodCast.VideoWindow;

/// <summary>Where the delay queue's frames come from: a captured window, or the CI smoke test's generated pictures.</summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>The picture delay, which sets how many frames per second the queue can hold.</summary>
    int DelayMs { set; }

    /// <summary>The source failed, with a translated reason (on a capture thread).</summary>
    event Action<string>? Failed;

    /// <summary>The captured window closed (on a capture thread).</summary>
    event Action? Closed;
}

/// <summary>The texts the viewer shows for a failure: translated, with the HRESULT for anything unexpected.</summary>
internal static class Failure
{
    public static string Capture(Exception ex) => ex is FrameTooLargeException large
        ? L.F("画面太大：最多支持 {0} × {1} 像素。", large.Limit, large.Limit)
        : L.F("窗口采集失败，错误代码 {0}。请重新选择窗口。", Code(ex.HResult));

    public static string Code(int hr) => $"0x{hr:X8}";
}

/// <summary>
/// Windows.Graphics.Capture of the window or screen picked in the system picker (which keeps its yellow border), into
/// the delay queue: every frame stays on the GPU (<see cref="FrameScaler"/>) and keeps its capture timestamp.
/// </summary>
internal sealed class CaptureEngine : IFrameSource
{
    private readonly object _gate = new();
    private readonly Gpu _gpu;
    private readonly FrameScaler _scaler;
    private readonly DelayQueue<GpuFrame> _queue;
    private readonly FrameCadence _cadence = new();
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private SizeInt32 _size;
    private int _delayMs;
    public event Action<string>? Failed;
    public event Action? Closed;
    public int DelayMs { set => Volatile.Write(ref _delayMs, Math.Clamp(value, 0, 2000)); }

    public CaptureEngine(Gpu gpu, FrameScaler scaler, DelayQueue<GpuFrame> queue, GraphicsCaptureItem item)
    {
        _gpu = gpu; _scaler = scaler; _queue = queue; _item = item; _size = item.Size;
        try
        {
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += FrameArrived;
            item.Closed += ItemClosed;
            _session = _pool.CreateCaptureSession(item);
            // The picture is for watching: a delayed copy of the mouse pointer would be a second, late pointer.
            try { _session.IsCursorCaptureEnabled = false; }
            catch (Exception ex) when (ex is InvalidCastException or COMException or NotImplementedException) { } // before Windows 10 2004
            _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_pool != sender) return;
            try
            {
                SizeInt32 size;
                using (var frame = sender.TryGetNextFrame())
                {
                    if (frame == null) return;
                    size = frame.ContentSize;
                    if (size.Width <= 0 || size.Height <= 0) return;
                    if (size.Width == _size.Width && size.Height == _size.Height)
                    {
                        long time = frame.SystemRelativeTime.Ticks;
                        var (w, h) = FrameScaler.SizeFor(size.Width, size.Height);
                        long interval = FrameCadence.IntervalFor(Volatile.Read(ref _delayMs), (long)w * h * 4, _queue.ByteLimit, _queue.FrameLimit);
                        if (!_cadence.Accept(time, interval)) return;
                        nint texture = Gpu.TextureOf(frame.Surface);
                        try
                        {
                            var queued = _scaler.Scale(texture, size.Width, size.Height);
                            _queue.Add(time, queued.Bytes, queued);
                        }
                        finally { Gpu.Release(ref texture); }
                        return;
                    }
                }
                // Resized: release the frame above first, then recreate the pool at the new size; older pictures go.
                _queue.Clear(); _cadence.Reset(); _size = size;
                sender.Recreate(_gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
            catch (Exception ex) { Failed?.Invoke(Failure.Capture(ex)); }
        }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => Closed?.Invoke();

    public void Dispose()
    {
        Direct3D11CaptureFramePool? pool; GraphicsCaptureSession? session;
        lock (_gate)
        {
            pool = _pool; session = _session;
            _pool = null; _session = null;
            if (_item != null) { _item.Closed -= ItemClosed; _item = null; }
            if (pool != null) pool.FrameArrived -= FrameArrived;
        }
        // Never wait for WinRT callbacks while holding their capture lock.
        session?.Dispose(); pool?.Dispose();
    }
}
