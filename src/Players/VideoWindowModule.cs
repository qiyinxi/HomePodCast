using System.Diagnostics;

namespace HomePodCast.Players;

/// <summary>
/// The optional synchronized viewing window (modules/VideoWindow): a separately downloaded zip that HomePodCast starts
/// from the folder the user chose. Experimental since 1.1.
/// </summary>
internal static class VideoWindowModule
{
    internal const string Executable = "HomePodCast.VideoWindow.exe";
    internal const string Assembly = "HomePodCast.VideoWindow.dll";

    /// <summary>
    /// Not in the Microsoft Store version: starting an exe downloaded apart from the package is what Store policy
    /// 10.2.2 forbids (the review of PR #23, 2026-10-13), so its A/V sync section is omitted there.
    /// </summary>
    public static bool Offered => OfferedIn(AppEnvironment.IsPackaged);

    internal static bool OfferedIn(bool packaged) => !packaged;

    public static bool Exists(string? directory) => !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, Executable)) &&
        File.Exists(Path.Combine(directory, Assembly)) &&
        File.Exists(Path.Combine(directory, "HomePodCast.VideoWindow.runtimeconfig.json"));

    /// <summary>The module's version (X.Y.Z), null when unreadable; the module itself warns when it differs from the app's.</summary>
    public static string? Version(string directory)
    {
        try { return System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(directory, Assembly)).Version?.ToString(3); }
        catch (Exception) { return null; }
    }

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
