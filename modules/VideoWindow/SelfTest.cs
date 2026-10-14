namespace HomePodCast.VideoWindow;

/// <summary>
/// <c>--self-test</c> (CI, tools/build-video-window.ps1): the capture path on the GPU without a window, a picker or a
/// capture session. Known pictures of several sizes go texture → WinRT surface → texture (what a capture frame does)
/// → scaler, and the queued frame is read back: its size, and both halves' colours (a scaled picture must keep them).
/// WARP by default, so it runs the same on a runner without a GPU; <c>--hardware</c> tries the GPU.
/// </summary>
internal static class SelfTest
{
    private const uint Left = 0xff123456, Right = 0xffc8a040;

    public static unsafe int Run(bool warp)
    {
        using var gpu = new Gpu(warp);
        using var scaler = new FrameScaler(gpu);
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }
        Console.WriteLine($"device: {gpu.AdapterName()}{(gpu.Software ? " (WARP)" : "")}, textures up to {gpu.MaxTextureSize} px");

        // Above the first version's 4096 × 2160 limit too: 3840 × 2400, 5K and 8K are halved on the GPU until they fit.
        foreach (var (w, h, ew, eh) in new[] { (32, 16, 32, 16), (1920, 1080, 1920, 1080), (2560, 1600, 2560, 1600),
                     (3840, 2160, 1920, 1080), (3840, 2400, 1920, 1200), (5120, 2880, 2560, 1440), (7680, 4320, 1920, 1080) })
        {
            var pixels = new uint[w * h];
            for (int y = 0; y < h; y++)
            {
                Array.Fill(pixels, Left, y * w, w / 2);
                Array.Fill(pixels, Right, y * w + w / 2, w - w / 2);
            }
            nint texture, fromSurface = 0;
            fixed (uint* p = pixels) texture = gpu.CreateTexture(w, h, data: p, pitch: (uint)w * 4);
            try
            {
                var surface = Gpu.SurfaceOf(texture);
                using (surface as IDisposable)
                {
                    fromSurface = Gpu.TextureOf(surface);
                    using var frame = scaler.Scale(fromSurface, w, h);
                    var read = gpu.ReadPixels(frame.Texture);
                    int row = frame.Height / 2 * frame.Width;
                    Check(frame.Width == ew && frame.Height == eh && read.Length == ew * eh, $"{w} × {h} queued at {frame.Width} × {frame.Height} (expected {ew} × {eh})");
                    Check(read[row + frame.Width / 4] == Left && read[row + frame.Width * 3 / 4] == Right,
                        $"{w} × {h}: colours kept ({read[row + frame.Width / 4]:x8}, {read[row + frame.Width * 3 / 4]:x8})");
                }
            }
            finally { Gpu.Release(ref fromSurface); Gpu.Release(ref texture); }
        }

        try
        {
            using var _ = scaler.Scale(0, gpu.MaxTextureSize + 1, 1080);
            Check(false, "a source wider than the GPU's textures is refused");
        }
        catch (FrameTooLargeException ex)
        {
            Check(Failure.Capture(ex).Contains(ex.Limit.ToString(), StringComparison.Ordinal), $"a source wider than the GPU's textures is refused: {Failure.Capture(ex)}");
        }
        return failures == 0 ? 0 : 1;
    }
}
