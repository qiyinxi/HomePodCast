namespace HomePodCast.VideoWindow;

/// <summary>A picture in the delay queue: a GPU texture that goes back to its scaler's pool when disposed.</summary>
internal sealed class GpuFrame : IDisposable
{
    private readonly FrameScaler _owner;
    private nint _texture;

    internal GpuFrame(FrameScaler owner, nint texture, int width, int height)
    {
        _owner = owner; _texture = texture; Width = width; Height = height;
    }

    public nint Texture => _texture;
    public int Width { get; }
    public int Height { get; }
    public long Bytes => (long)Width * Height * 4;

    public void Dispose()
    {
        nint texture = Interlocked.Exchange(ref _texture, 0);
        if (texture != 0) _owner.Return(texture, Width, Height);
    }
}

/// <summary>The picture is larger than the GPU can take (its side above <see cref="Gpu.MaxTextureSize"/>).</summary>
internal sealed class FrameTooLargeException(int limit) : Exception($"Capture surface larger than {limit} px.")
{
    public int Limit => limit;
}

/// <summary>
/// Captured BGRA texture → queue frame, entirely on the GPU: a copy, and for sources above 2560 × 1600 a mip chain
/// (GenerateMips, a 2 × 2 box filter per level) halving them until they fit, so a 4K source is queued at 1920 × 1080
/// and a 5K or 3840 × 2400 one is accepted too (the first version read every frame back into a new Bitmap and scaled it
/// with GDI+ on the CPU, and refused anything above 4096 × 2160). Queue textures come from a pool, nothing is allocated
/// per frame; the swap chain stretches the frame to the window.
/// </summary>
internal sealed unsafe class FrameScaler : IDisposable
{
    public const int MaxWidth = 2560, MaxHeight = 1600;
    private const int PoolLimit = 8;

    private readonly Gpu _gpu;
    private readonly object _poolGate = new();
    private readonly Stack<nint> _free = new();
    private int _freeWidth, _freeHeight;
    private nint _mips, _mipsView;
    private int _mipsWidth, _mipsHeight;
    private uint _mipsLevels;
    private bool _disposed;

    public FrameScaler(Gpu gpu) => _gpu = gpu;

    /// <summary>How many times a source is halved before it is queued.</summary>
    public static int LevelFor(int width, int height)
    {
        int level = 0;
        while ((width >> level) > MaxWidth || (height >> level) > MaxHeight) level++;
        return level;
    }

    /// <summary>The size a <paramref name="width"/> × <paramref name="height"/> source is queued at.</summary>
    public static (int Width, int Height) SizeFor(int width, int height)
    {
        int level = LevelFor(width, height);
        return (Math.Max(1, width >> level), Math.Max(1, height >> level));
    }

    /// <summary>The top-left <paramref name="width"/> × <paramref name="height"/> of <paramref name="source"/> as a new queue frame.</summary>
    public GpuFrame Scale(nint source, int width, int height)
    {
        if (width > _gpu.MaxTextureSize || height > _gpu.MaxTextureSize) throw new FrameTooLargeException(_gpu.MaxTextureSize);
        int level = LevelFor(width, height);
        var (w, h) = SizeFor(width, height);
        nint target = Rent(w, h);
        try
        {
            lock (_gpu.Lock)
            {
                if (level == 0) _gpu.CopyRegion(target, 0, source, 0, w, h);
                else
                {
                    EnsureMips(width, height, (uint)level + 1);
                    _gpu.CopyRegion(_mips, 0, source, 0, width, height);
                    _gpu.GenerateMips(_mipsView);
                    _gpu.CopyRegion(target, 0, _mips, (uint)level, w, h);
                }
            }
            return new GpuFrame(this, target, w, h);
        }
        catch
        {
            Return(target, w, h);
            throw;
        }
    }

    private void EnsureMips(int width, int height, uint levels)
    {
        if (_mips != 0 && _mipsWidth == width && _mipsHeight == height && _mipsLevels == levels) return;
        Gpu.Release(ref _mipsView); Gpu.Release(ref _mips);
        _mips = _gpu.CreateTexture(width, height, levels, Gpu.BindShaderResource | Gpu.BindRenderTarget, Gpu.MiscGenerateMips);
        _mipsView = _gpu.CreateShaderResourceView(_mips);
        (_mipsWidth, _mipsHeight, _mipsLevels) = (width, height, levels);
    }

    private nint Rent(int width, int height)
    {
        lock (_poolGate)
        {
            if (_free.Count > 0 && _freeWidth == width && _freeHeight == height) return _free.Pop();
        }
        return _gpu.CreateTexture(width, height);
    }

    internal void Return(nint texture, int width, int height)
    {
        lock (_poolGate)
        {
            if (!_disposed)
            {
                if (_freeWidth != width || _freeHeight != height)
                {
                    while (_free.Count > 0) { nint old = _free.Pop(); Gpu.Release(ref old); }
                    (_freeWidth, _freeHeight) = (width, height);
                }
                if (_free.Count < PoolLimit) { _free.Push(texture); return; }
            }
        }
        Gpu.Release(ref texture);
    }

    public void Dispose()
    {
        lock (_poolGate)
        {
            _disposed = true;
            while (_free.Count > 0) { nint old = _free.Pop(); Gpu.Release(ref old); }
        }
        lock (_gpu.Lock) { Gpu.Release(ref _mipsView); Gpu.Release(ref _mips); }
    }
}
