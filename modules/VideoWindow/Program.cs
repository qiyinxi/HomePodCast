using System.Diagnostics;

namespace HomePodCast;

// L.cs shares embedded translations with the main app; this companion never writes its profile or log.
internal static class Log { public static void Warn(string text) => Trace.TraceWarning(text); }

internal static class VideoProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool headless = args.Contains("--self-test") || args.Contains("--benchmark");
        try
        {
            // Headless checks: no window, picker or capture session.
            if (args.Contains("--self-test")) return VideoWindow.SelfTest.Run(warp: !args.Contains("--hardware"));
            if (args.Contains("--benchmark"))
                return VideoWindow.Benchmark.Run(args.Contains("--warp"), int.TryParse(Arg("--frames"), out int frames) ? Math.Max(30, frames) : 150);

            int.TryParse(Arg("--status-port"), out int port);
            int.TryParse(Arg("--delay-ms"), out int delay);
            L.Use(Arg("--language"));
            ApplicationConfiguration.Initialize();
            Application.Run(new VideoWindow.ViewerForm(port, Math.Clamp(delay, 0, 2000),
                smoke: args.Contains("--smoke-source"), smokeResult: Arg("--smoke-result")));
            return 0;
        }
        catch (Exception ex)
        {
            if (headless) { Console.Error.WriteLine(ex); return 1; }
            MessageBox.Show(L.F("同步窗口无法启动：{0}", VideoWindow.Failure.Code(ex.HResult)), "HomePodCast"); return 1;
        }
    }
}
