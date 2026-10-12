using System.Diagnostics;

namespace HomePodCast;

// L.cs shares embedded translations with the main app; this companion never writes its profile or log.
internal static class Log { public static void Warn(string text) => Trace.TraceWarning(text); }

internal static class VideoProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test"))
            {
                using var native = new VideoWindow.D3dReadback(warp: true);
                native.VerifyReadback();
                return 0;
            }
            string? Arg(string name)
            {
                int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            int.TryParse(Arg("--status-port"), out int port);
            int.TryParse(Arg("--delay-ms"), out int delay);
            L.Use(Arg("--language"));
            ApplicationConfiguration.Initialize();
            Application.Run(new VideoWindow.ViewerForm(port, Math.Clamp(delay, 0, 2000)));
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Contains("--self-test")) { Console.Error.WriteLine(ex); return 1; }
            MessageBox.Show(L.F("同步窗口无法启动：{0}", ex.Message), "HomePodCast"); return 1;
        }
    }
}
