using System.Diagnostics;

namespace HomePodCast.VideoWindow;

/// <summary>
/// <c>--benchmark [--warp] [--frames N]</c>: what one frame costs on the capture-to-screen path, without a window or a
/// capture session. For a 1080p and a 4K source, N frames at 30 fps go through the scaler into a queue frame, which is
/// copied twice into a back-buffer-sized texture (a 60 Hz presenter shows a 30 fps frame twice). Prints the process's
/// CPU time per frame (all threads, the driver's included; on WARP that is also the "GPU" work) and the time until the
/// GPU has finished one frame's work.
/// </summary>
internal static unsafe class Benchmark
{
    public static int Run(bool warp, int frames)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        using var gpu = new Gpu(warp);
        using var scaler = new FrameScaler(gpu);
        Console.WriteLine($"device: {gpu.AdapterName()}{(gpu.Software ? " (WARP, software)" : "")}; {frames} frames at 30 fps per size");
        Console.WriteLine("source      queued      CPU ms/frame   GPU done ms/frame (median)");
        foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
        {
            var pictures = SyntheticSource.Pictures(gpu, w, h);
            var (fw, fh) = FrameScaler.SizeFor(w, h);
            nint backBuffer = gpu.CreateTexture(fw, fh, bind: Gpu.BindRenderTarget);
            try
            {
                void One(int i)
                {
                    using var frame = scaler.Scale(pictures[i % pictures.Length], w, h);
                    lock (gpu.Lock)
                    {
                        gpu.CopyResource(backBuffer, frame.Texture);
                        gpu.CopyResource(backBuffer, frame.Texture);
                    }
                }
                for (int i = 0; i < 10; i++) One(i);
                lock (gpu.Lock) gpu.Finish();

                // GPU completion: each frame waited for on its own.
                var done = new List<double>();
                for (int i = 0; i < 30; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    One(i);
                    lock (gpu.Lock) gpu.Finish();
                    done.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                done.Sort();

                // CPU: paced like a 30 fps source, so idle waiting costs nothing; the GPU's work is waited for at the end.
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    One(i);
                    double next = (i + 1) * 1000.0 / 30;
                    while (clock.Elapsed.TotalMilliseconds < next) Thread.Sleep(1);
                }
                lock (gpu.Lock) gpu.Finish();
                double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / frames;
                Console.WriteLine($"{w} × {h,-5} {fw} × {fh,-5} {cpuMs,8:F2}       {done[done.Count / 2],8:F2}");
            }
            finally
            {
                Gpu.Release(ref backBuffer);
                for (int i = 0; i < pictures.Length; i++) Gpu.Release(ref pictures[i]);
            }
        }
        return 0;
    }
}
