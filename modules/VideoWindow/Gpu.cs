using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace HomePodCast.VideoWindow;

/// <summary>
/// The D3D11 device that capture, scaling and presentation share, called through its COM vtables (slot numbers from
/// the Windows SDK's d3d11.h and dxgi*.h). The immediate context is not thread-safe: the capture callback and the
/// presenter thread use it only inside <see cref="Lock"/>, and multithread protection is on for what
/// Windows.Graphics.Capture does with the device itself.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    public const uint Bgra = 87; // DXGI_FORMAT_B8G8R8A8_UNORM, what the capture frame pool delivers
    public const uint BindShaderResource = 0x8, BindRenderTarget = 0x20;
    public const uint MiscGenerateMips = 0x1;

    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid MultithreadId = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid DxgiSurfaceId = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    private static readonly Guid SurfaceAccessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");

    private nint _device, _context;

    /// <summary>The immediate context's lock.</summary>
    public readonly object Lock = new();

    public nint Device => _device;
    public IDirect3DDevice WinRtDevice { get; }
    public bool Software { get; }

    /// <summary>The largest texture side the device takes (16384 from feature level 11_0, 8192 below).</summary>
    public int MaxTextureSize { get; }

    public Gpu(bool warp = false)
    {
        const uint BgraSupport = 0x20;
        nint device, context; int level;
        int hr = D3D11CreateDevice(0, warp ? 5 : 1, 0, BgraSupport, 0, 0, 7, out device, out level, out context);
        if (hr < 0 && !warp) { warp = true; hr = D3D11CreateDevice(0, 5, 0, BgraSupport, 0, 0, 7, out device, out level, out context); }
        Marshal.ThrowExceptionForHR(hr);
        _device = device; _context = context; Software = warp;
        MaxTextureSize = level >= 0xb000 ? 16384 : 8192;
        nint dxgi = 0, projected = 0, multithread = 0;
        try
        {
            if (Marshal.QueryInterface(_context, in MultithreadId, out multithread) >= 0)
                ((delegate* unmanaged[Stdcall]<nint, int, int>)Method(multithread, 5))(multithread, 1); // SetMultithreadProtected
            Check(Marshal.QueryInterface(_device, in DxgiDeviceId, out dxgi));
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            WinRtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        catch { Dispose(); throw; }
        finally { Release(ref dxgi); Release(ref projected); Release(ref multithread); }
    }

    // ---------------------------------------------------------------- device (free-threaded)

    public nint CreateTexture(int width, int height, uint mipLevels = 1, uint bind = BindShaderResource, uint misc = 0,
        uint usage = 0, uint cpuAccess = 0, void* data = null, uint pitch = 0)
    {
        var desc = new TextureDesc
        {
            Width = (uint)width, Height = (uint)height, MipLevels = mipLevels, ArraySize = 1, Format = Bgra, SampleCount = 1,
            Usage = usage, BindFlags = bind, CpuAccessFlags = cpuAccess, MiscFlags = misc,
        };
        var init = new Subresource { Data = (nint)data, Pitch = pitch };
        nint texture;
        Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, Subresource*, nint*, int>)Method(_device, 5))
            (_device, &desc, data == null ? null : &init, &texture));
        return texture;
    }

    public nint CreateShaderResourceView(nint resource)
    {
        nint view;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)Method(_device, 7))(_device, resource, null, &view));
        return view;
    }

    public nint CreateRenderTargetView(nint resource)
    {
        nint view;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)Method(_device, 9))(_device, resource, null, &view));
        return view;
    }

    /// <summary>The adapter's name (DXGI_ADAPTER_DESC.Description), for the benchmark's and self-test's output.</summary>
    public string AdapterName()
    {
        nint dxgi = 0, adapter = 0;
        try
        {
            Check(Marshal.QueryInterface(_device, in DxgiDeviceId, out dxgi));
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(dxgi, 7))(dxgi, &adapter)); // GetAdapter
            var desc = stackalloc byte[512];
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Method(adapter, 8))(adapter, desc)); // GetDesc: WCHAR Description[128] first
            return new string((char*)desc).Trim();
        }
        catch (Exception) { return "?"; }
        finally { Release(ref adapter); Release(ref dxgi); }
    }

    public static TextureDesc Describe(nint texture)
    {
        TextureDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Method(texture, 10))(texture, &desc);
        return desc;
    }

    /// <summary>The ID3D11Texture2D behind a capture frame's surface (a new reference: release it).</summary>
    public static nint TextureOf(IDirect3DSurface surface)
    {
        nint abi = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface), access = 0, texture = 0;
        try
        {
            Check(Marshal.QueryInterface(abi, in SurfaceAccessId, out access));
            var id = TextureId;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(access, 3))(access, &id, &texture));
            return texture;
        }
        finally { Release(ref access); Release(ref abi); }
    }

    /// <summary>A WinRT surface for a texture, as capture frames carry them (self-test of <see cref="TextureOf"/>).</summary>
    public static IDirect3DSurface SurfaceOf(nint texture)
    {
        nint dxgiSurface = 0, abi = 0;
        try
        {
            Check(Marshal.QueryInterface(texture, in DxgiSurfaceId, out dxgiSurface));
            Check(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out abi));
            return WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(abi);
        }
        finally { Release(ref abi); Release(ref dxgiSurface); }
    }

    // ---------------------------------------------------------------- immediate context (hold Lock)

    /// <summary>Copies the top-left <paramref name="width"/> × <paramref name="height"/> of a subresource.</summary>
    public void CopyRegion(nint dst, uint dstSub, nint src, uint srcSub, int width, int height)
    {
        var box = new Box { Right = (uint)width, Bottom = (uint)height, Back = 1 };
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Method(_context, 46))
            (_context, dst, dstSub, 0, 0, 0, src, srcSub, &box);
    }

    public void CopyResource(nint dst, nint src) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(_context, 47))(_context, dst, src);

    public void GenerateMips(nint view) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Method(_context, 54))(_context, view);

    public void Clear(nint target)
    {
        float* black = stackalloc float[4] { 0, 0, 0, 1 };
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Method(_context, 50))(_context, target, black);
    }

    /// <summary>Submits the queued commands; also lets released views and textures actually go (deferred destruction).</summary>
    public void Flush() => ((delegate* unmanaged[Stdcall]<nint, void>)Method(_context, 111))(_context);

    /// <summary>Waits until the GPU has done everything submitted so far (an event query).</summary>
    public void Finish()
    {
        var desc = new QueryDesc();
        nint query;
        Check(((delegate* unmanaged[Stdcall]<nint, QueryDesc*, nint*, int>)Method(_device, 24))(_device, &desc, &query));
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Method(_context, 28))(_context, query); // End
            int done = 0, hr;
            while ((hr = ((delegate* unmanaged[Stdcall]<nint, nint, int*, uint, uint, int>)Method(_context, 29))(_context, query, &done, 4, 0)) == 1)
                Thread.Yield();
            Check(hr);
        }
        finally { Release(ref query); }
    }

    /// <summary>The pixels of a single-level texture (BGRA words, row by row), through a staging copy.</summary>
    public uint[] ReadPixels(nint texture)
    {
        var desc = Describe(texture);
        nint staging = CreateTexture((int)desc.Width, (int)desc.Height, bind: 0, usage: 3 /* STAGING */, cpuAccess: 0x20000 /* READ */);
        try
        {
            var pixels = new uint[desc.Width * desc.Height];
            lock (Lock)
            {
                CopyResource(staging, texture);
                Mapped mapped;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Method(_context, 14))
                    (_context, staging, 0, 1 /* READ */, 0, &mapped));
                try
                {
                    fixed (uint* p = pixels)
                        for (uint y = 0; y < desc.Height; y++)
                            Buffer.MemoryCopy((byte*)mapped.Data + y * mapped.RowPitch, p + y * desc.Width, desc.Width * 4L, desc.Width * 4L);
                }
                finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(_context, 15))(_context, staging, 0); }
            }
            return pixels;
        }
        finally { Release(ref staging); }
    }

    // ---------------------------------------------------------------- COM

    internal static nint Method(nint obj, int slot) => (*(nint**)obj)[slot];

    public static void Release(ref nint obj)
    {
        if (obj != 0) { Marshal.Release(obj); obj = 0; }
    }

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        Release(ref _context); Release(ref _device);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextureDesc
    { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct Subresource { public nint Data; public uint Pitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryDesc { public uint Query, MiscFlags; } // D3D11_QUERY_EVENT = 0

    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software,
        uint flags, nint featureLevels, uint count, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint surface);
}
