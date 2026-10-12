using System.Diagnostics;

namespace HomePodCast.Players;

internal static class VideoWindowModule
{
    internal const string Executable = "HomePodCast.VideoWindow.exe";
    public static bool Exists(string? directory) => !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, Executable)) &&
        File.Exists(Path.Combine(directory, "HomePodCast.VideoWindow.dll")) &&
        File.Exists(Path.Combine(directory, "HomePodCast.VideoWindow.runtimeconfig.json"));

    internal static ProcessStartInfo StartInfo(string directory, int port, int delayMs, string language)
    {
        var start = new ProcessStartInfo(Path.Combine(Path.GetFullPath(directory), Executable))
        { UseShellExecute = false, WorkingDirectory = Path.GetFullPath(directory) };
        foreach (string value in new[] { "--status-port", Math.Clamp(port, 0, 65535).ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--delay-ms", Math.Clamp(delayMs, 0, 2000).ToString(System.Globalization.CultureInfo.InvariantCulture), "--language", language })
            start.ArgumentList.Add(value);
        return start;
    }
}
