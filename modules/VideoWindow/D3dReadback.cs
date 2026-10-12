using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace HomePodCast.VideoWindow;

/// <summary>WGC BGRA surface → bounded SDR preview. Native resources are confined to the capture lock.</summary>
internal sealed unsafe class D3dReadback : IDisposable
{
    private nint _device, _context, _staging;
    private TextureDesc _stagingDesc;
    public IDirect3DDevice Device { get; }
    private static readonly Guid TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid SurfaceAccessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");

    public D3dReadback(bool warp = false)
    {
        nint device = 0, context = 0; int level;
        int hr = D3D11CreateDevice(0, warp ? 5 : 1, 0, 0x20, 0, 0, 7, out device, out level, out context);
        if (hr < 0 && !warp) hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out device, out level, out context);
        Marshal.ThrowExceptionForHR(hr); _device = device; _context = context;
        nint dxgi = 0, projected = 0;
        try
        {
            var id = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device, in id, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            Device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        catch { Dispose(); throw; }
        finally { Release(ref dxgi); Release(ref projected); }
    }

    public Bitmap Read(IDirect3DSurface surface)
    {
        nint abi = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface), access = 0, texture = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in SurfaceAccessId, out access));
            var get = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(access, 3);
            var id = TextureId; Marshal.ThrowExceptionForHR(get(access, &id, &texture));
            return ReadTexture(texture);
        }
        finally { Release(ref texture); Release(ref access); Release(ref abi); }
    }

    private Bitmap ReadTexture(nint texture)
    {
        TextureDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Method(texture, 10))(texture, &desc);
        // Do not copy arbitrarily large desktop surfaces into managed memory.
        if (desc.Width == 0 || desc.Height == 0 || (long)desc.Width * desc.Height > 8_847_360 || desc.Format != 87)
            throw new InvalidDataException("Capture requires a BGRA SDR surface no larger than 4096 × 2160.");
        if (_staging == 0 || desc.Width != _stagingDesc.Width || desc.Height != _stagingDesc.Height)
        {
            Release(ref _staging);
            desc.Usage = 3; desc.BindFlags = 0; desc.CpuAccessFlags = 0x20000; desc.MiscFlags = 0;
            nint created = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(_device, 5))
                (_device, &desc, 0, &created));
            _staging = created; _stagingDesc = desc;
        }
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(_context, 47))(_context, _staging, texture);
        Mapped mapped;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Method(_context, 14))
            (_context, _staging, 0, 1, 0, &mapped));
        Bitmap? raw = null;
        try
        {
            raw = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppArgb);
            var data = raw.LockBits(new Rectangle(0, 0, raw.Width, raw.Height), ImageLockMode.WriteOnly, raw.PixelFormat);
            try
            {
                for (int y = 0; y < raw.Height; y++)
                    Buffer.MemoryCopy((byte*)mapped.Data + y * mapped.RowPitch, (byte*)data.Scan0 + y * data.Stride,
                        Math.Abs(data.Stride), raw.Width * 4);
            }
            finally { raw.UnlockBits(data); }
            double scale = Math.Min(1, Math.Min(1280.0 / raw.Width, 720.0 / raw.Height));
            if (scale == 1) { var result = raw; raw = null; return result; }
            var small = new Bitmap(Math.Max(1, (int)(raw.Width * scale)), Math.Max(1, (int)(raw.Height * scale)), raw.PixelFormat);
            try { using var graphics = Graphics.FromImage(small); graphics.DrawImage(raw, 0, 0, small.Width, small.Height); return small; }
            catch { small.Dispose(); throw; }
        }
        finally
        {
            raw?.Dispose();
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(_context, 15))(_context, _staging, 0);
        }
    }

    // Headless native readback check: synthetic texture, no picker, window, sound, or capture session.
    public void VerifyReadback()
    {
        TextureDesc desc = new() { Width = 32, Height = 16, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1 };
        var pixels = new uint[32 * 16]; Array.Fill(pixels, 0xff123456);
        nint texture = 0, dxgiSurface = 0, abi = 0;
        try
        {
            fixed (uint* p = pixels)
            {
                Subresource data = new() { Data = (nint)p, Pitch = 128 };
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, Subresource*, nint*, int>)Method(_device, 5))
                    (_device, &desc, &data, &texture));
            }
            var id = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in id, out dxgiSurface));
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out abi));
            var surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(abi);
            using var surfaceLifetime = surface as IDisposable;
            using var image = Read(surface);
            if (image.GetPixel(17, 9).ToArgb() != unchecked((int)0xff123456)) throw new InvalidDataException("D3D readback pixel mismatch.");
        }
        finally { Release(ref abi); Release(ref dxgiSurface); Release(ref texture); }
    }

    private static nint Method(nint obj, int slot) => (*(nint**)obj)[slot];
    private static void Release(ref nint obj) { if (obj != 0) { Marshal.Release(obj); obj = 0; } }
    public void Dispose()
    {
        (Device as IDisposable)?.Dispose(); Release(ref _staging); Release(ref _context); Release(ref _device);
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc
    { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct Subresource { public nint Data; public uint Pitch, SlicePitch; }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software,
        uint flags, nint featureLevels, uint count, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint surface);
}
