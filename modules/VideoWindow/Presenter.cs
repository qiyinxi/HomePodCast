using System.Runtime.InteropServices;

namespace HomePodCast.VideoWindow;

/// <summary>
/// Shows the delay queue in a window through a flip-model DXGI swap chain. A thread of its own wakes at every refresh
/// (the swap chain's frame-latency waitable object, so the pace is the screen's, not a 15 ms WinForms timer), takes
/// the newest frame due by the time this present reaches the screen (now + <see cref="DisplayLatency"/>), copies it
/// into the back buffer and presents. The swap chain has the frame's size and DXGI stretches it to the window, which
/// the viewer keeps at the picture's aspect ratio: no CPU touches a pixel.
/// </summary>
internal sealed unsafe class Presenter : IDisposable
{
    private const int FrameLatencyWaitable = 64; // DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid Factory2Id = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid SwapChain2Id = new("a8be2ac4-199f-4946-b331-79599fb98de7");
    private static readonly Guid TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly Gpu _gpu;
    private readonly DelayQueue<GpuFrame> _queue;
    private readonly DisplayLatency _latency = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private nint _swapChain, _backBuffer, _target, _waitable;
    private int _width = 16, _height = 16;
    private GpuFrame? _current;
    private volatile bool _stop, _clear, _active;
    private long _delayTicks, _manualLead = -1, _lead;
    private int _shown, _presents;

    /// <summary>The frame size changed (on the presenter thread): the window's aspect ratio follows.</summary>
    public event Action<Size>? FrameSizeChanged;

    /// <summary>Presenting failed (the HRESULT; on the presenter thread, which then stops).</summary>
    public event Action<int>? Failed;

    public Presenter(Gpu gpu, DelayQueue<GpuFrame> queue, nint hwnd)
    {
        _gpu = gpu; _queue = queue;
        try { CreateSwapChain(hwnd); }
        catch { ReleaseSwapChain(); throw; }
        _thread = new Thread(Run) { IsBackground = true, Name = "presenter", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Frames are coming: take them from the queue at every refresh. Off: show black and sleep.</summary>
    public bool Active { set { _active = value; _wake.Set(); } }

    /// <summary>The picture delay (the HomePod's sound lag plus the fine adjustment).</summary>
    public int DelayMs { set => Volatile.Write(ref _delayTicks, Math.Max(0, value) * Clock.TicksPerMs); }

    /// <summary>The display latency to subtract: null = the measured one, else this many ms.</summary>
    public int? ManualLeadMs { set => Volatile.Write(ref _manualLead, value is { } ms ? Math.Max(0, ms) * Clock.TicksPerMs : -1); }

    /// <summary>The display latency subtracted now, ms.</summary>
    public int LeadMs => (int)(Volatile.Read(ref _lead) / Clock.TicksPerMs);

    /// <summary>Whether <see cref="LeadMs"/> comes from DXGI frame statistics (else one refresh period).</summary>
    public bool LeadMeasured { get; private set; }

    /// <summary>Frames shown and presents made so far (each refresh presents, a frame lasts several).</summary>
    public (int Shown, int Presents) Counts => (Volatile.Read(ref _shown), Volatile.Read(ref _presents));

    /// <summary>Drops the frame on screen (capture stopped or changed source): black until the next one.</summary>
    public void Clear() { _clear = true; _wake.Set(); }

    private void Run()
    {
        bool black = false;
        try
        {
            while (!_stop)
            {
                if (!_active && black && !_clear) { _wake.WaitOne(500); continue; }
                WaitForSingleObjectEx(_waitable, 100, true);
                if (_stop) break;
                long now = Clock.Now;
                _latency.Woke(now);
                long lead = Volatile.Read(ref _manualLead) is var manual and >= 0 ? manual : _latency.Ticks;
                Volatile.Write(ref _lead, lead);
                LeadMeasured = _latency.Measured;
                Size? resized = null;
                int hr;
                lock (_gpu.Lock)
                {
                    if (_clear) { _clear = false; _current?.Dispose(); _current = null; }
                    var frame = _active ? _queue.TakeReady(now + lead, Volatile.Read(ref _delayTicks)) : null;
                    if (frame != null)
                    {
                        _current?.Dispose();
                        _current = frame;
                        Interlocked.Increment(ref _shown);
                    }
                    if (_current is { } c && (c.Width != _width || c.Height != _height))
                    {
                        Resize(c.Width, c.Height);
                        resized = new Size(c.Width, c.Height);
                    }
                    if (_current != null) _gpu.CopyResource(_backBuffer, _current.Texture);
                    else _gpu.Clear(_target);
                    hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Gpu.Method(_swapChain, 8))(_swapChain, 1, 0); // Present
                    if (hr >= 0) Measure(now);
                }
                if (resized is { } size) FrameSizeChanged?.Invoke(size);
                if (hr < 0) { Failed?.Invoke(hr); return; }
                black = _current == null;
                Interlocked.Increment(ref _presents);
            }
        }
        catch (Exception ex) { Failed?.Invoke(ex.HResult); }
    }

    /// <summary>When the presents reached the screen: the latency from choosing a frame to seeing it.</summary>
    private void Measure(long chosenAt)
    {
        uint count;
        if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Gpu.Method(_swapChain, 17))(_swapChain, &count) >= 0) // GetLastPresentCount
            _latency.Presented(count, chosenAt);
        FrameStatistics stats;
        if (((delegate* unmanaged[Stdcall]<nint, FrameStatistics*, int>)Gpu.Method(_swapChain, 16))(_swapChain, &stats) >= 0 // GetFrameStatistics
            && stats.SyncQpcTime != 0)
            _latency.Displayed(stats.PresentCount, Clock.FromQpc(stats.SyncQpcTime));
    }

    private void CreateSwapChain(nint hwnd)
    {
        nint dxgiDevice = 0, adapter = 0, factory = 0, chain = 0;
        try
        {
            Gpu.Check(Marshal.QueryInterface(_gpu.Device, in DxgiDeviceId, out dxgiDevice));
            Gpu.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Gpu.Method(dxgiDevice, 7))(dxgiDevice, &adapter)); // GetAdapter
            var factoryId = Factory2Id;
            Gpu.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Gpu.Method(adapter, 6))(adapter, &factoryId, &factory)); // GetParent
            var desc = new SwapChainDesc1
            {
                Width = (uint)_width, Height = (uint)_height, Format = Gpu.Bgra, SampleCount = 1,
                BufferUsage = 0x20 /* RENDER_TARGET_OUTPUT */, BufferCount = 2,
                Scaling = 0 /* STRETCH */, SwapEffect = 4 /* FLIP_DISCARD */, AlphaMode = 0, Flags = FrameLatencyWaitable,
            };
            Gpu.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, SwapChainDesc1*, void*, nint, nint*, int>)Gpu.Method(factory, 15)) // CreateSwapChainForHwnd
                (factory, _gpu.Device, hwnd, &desc, null, 0, &chain));
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Gpu.Method(factory, 8))(factory, hwnd, 2); // MakeWindowAssociation: no Alt+Enter
            Gpu.Check(Marshal.QueryInterface(chain, in SwapChain2Id, out _swapChain));
            Gpu.Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Gpu.Method(_swapChain, 31))(_swapChain, 1)); // SetMaximumFrameLatency
            _waitable = ((delegate* unmanaged[Stdcall]<nint, nint>)Gpu.Method(_swapChain, 33))(_swapChain); // GetFrameLatencyWaitableObject
            GetBuffer();
        }
        finally { Gpu.Release(ref chain); Gpu.Release(ref factory); Gpu.Release(ref adapter); Gpu.Release(ref dxgiDevice); }
    }

    private void GetBuffer()
    {
        var id = TextureId;
        nint buffer;
        Gpu.Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Gpu.Method(_swapChain, 9))(_swapChain, 0, &id, &buffer));
        _backBuffer = buffer;
        _target = _gpu.CreateRenderTargetView(_backBuffer);
    }

    /// <summary>The back buffers at the frame's size (inside the GPU lock: nothing may hold them meanwhile).</summary>
    private void Resize(int width, int height)
    {
        Gpu.Release(ref _target); Gpu.Release(ref _backBuffer);
        _gpu.Flush(); // ResizeBuffers fails while a released view of a buffer still waits for deferred destruction
        Gpu.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)Gpu.Method(_swapChain, 13)) // ResizeBuffers
            (_swapChain, 0, (uint)width, (uint)height, 0, FrameLatencyWaitable));
        (_width, _height) = (width, height);
        GetBuffer();
    }

    private void ReleaseSwapChain()
    {
        Gpu.Release(ref _target); Gpu.Release(ref _backBuffer); Gpu.Release(ref _swapChain);
        if (_waitable != 0) { CloseHandle(_waitable); _waitable = 0; }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        lock (_gpu.Lock)
        {
            _current?.Dispose();
            _current = null;
            ReleaseSwapChain();
        }
        _wake.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDesc1
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameStatistics
    {
        public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
        public long SyncQpcTime, SyncGpuTime;
    }

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObjectEx(nint handle, uint milliseconds, bool alertable);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
