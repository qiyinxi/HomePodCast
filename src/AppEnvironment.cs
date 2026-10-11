using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HomePodCast;

/// <summary>
/// How this copy runs. Unpackaged: the MSI, the portable zip and development builds. Packaged: the Microsoft Store
/// version (packaging/msix), a full-trust desktop app with package identity. For it Windows applies the manifest (the
/// firewall rules, the startup task, the per-app microphone switch) and keeps what the app writes under %APPDATA% and
/// to HKCU in the package's own storage. Whatever differs between the two asks here; the decisions themselves take the
/// values as parameters, so tests cover both without a package.
/// </summary>
internal static partial class AppEnvironment
{
    /// <summary>The app's Application Id in packaging/msix/AppxManifest.xml: Start launches <c>&lt;family name&gt;!HomePodCast</c>.</summary>
    public const string ApplicationId = "HomePodCast";

    /// <summary>
    /// The manifest's startup task (desktop:StartupTask TaskId; its extension passes --tray), switched by 开机自动启动 in the
    /// packaged app.
    /// </summary>
    public const string StartupTaskId = "HomePodCastStartup";

    /// <summary>Running from an MSIX package: the Microsoft Store version.</summary>
    public static bool IsPackaged { get; } = HasPackageIdentity();

    /// <summary>The package family name (<c>qiyinxi.HomePodCast_21rkx5qfj0b6t</c>); null when not packaged.</summary>
    public static string? PackageFamilyName { get; } = IsPackaged ? ReadPackageFamilyName() : null;

    private static string? _packageSid;

    /// <summary>The package's SID (S-1-15-2-…), as a firewall rule names it in LocalAppPackageId; null when not packaged.</summary>
    public static string? PackageSid => PackageFamilyName == null ? null : _packageSid ??= PackageSidFor(PackageFamilyName);

    private static bool HasPackageIdentity()
    {
        try
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
        }
        catch (Exception)
        {
            return false; // no AppModel API (never on Windows 10 2004+): not packaged
        }
    }

    private static string? ReadPackageFamilyName()
    {
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
            var name = new char[length];
            if (GetCurrentPackageFamilyName(ref length, name) != 0) return null;
            return new string(name, 0, Array.IndexOf(name, '\0') is var end and >= 0 ? end : name.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The SID Windows derives from a package family name (its AppContainer SID); firewall rules can name a package by
    /// it. Null if it can't be derived.
    /// </summary>
    internal static string? PackageSidFor(string packageFamilyName)
    {
        try
        {
            if (DeriveAppContainerSidFromAppContainerName(packageFamilyName, out var sid) < 0 || sid == IntPtr.Zero) return null;
            try { return new SecurityIdentifier(sid).Value; }
            finally { FreeSid(sid); }
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- starting again

    /// <summary>
    /// What starts this app again (after a change of the UI language). Unpackaged: the exe itself. Packaged: the
    /// package's application through Explorer, so the new copy has package identity again (an exe started by path from
    /// WindowsApps would run without it: no manifest, no virtualization, a different settings folder).
    /// </summary>
    internal static (string File, string Arguments) RelaunchCommand(string? packageFamilyName, string exePath, string windowsDirectory) =>
        packageFamilyName == null
            ? (exePath, "")
            : (Path.Combine(windowsDirectory, "explorer.exe"), $@"shell:AppsFolder\{packageFamilyName}!{ApplicationId}");

    /// <summary>The command for this copy (see <see cref="RelaunchCommand(string?, string, string)"/>).</summary>
    public static (string File, string Arguments) Relaunch =>
        RelaunchCommand(PackageFamilyName, Environment.ProcessPath ?? "HomePodCast.exe", Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    // ---------------------------------------------------------------- the log folder

    /// <summary>
    /// The folder Explorer has to open to show <paramref name="fileName"/> in <paramref name="folder"/> (a folder under
    /// %APPDATA%, <paramref name="roamingAppData"/>). Unpackaged: the folder itself. Packaged, Windows writes the files
    /// the app creates there into the package's storage, <c>%LOCALAPPDATA%\Packages\&lt;family name&gt;\LocalCache\Roaming\…</c>,
    /// and only the app sees them at the usual path; Explorer runs outside the package. Files that were there before
    /// (an MSI or portable copy's) stay where they are and are changed in place. So: the package's folder when the file
    /// is there, else the usual one.
    /// </summary>
    internal static string FolderForExplorer(string folder, string fileName, string? packageFamilyName, string roamingAppData,
        string localAppData, Func<string, bool> fileExists)
    {
        if (packageFamilyName == null) return folder;
        var relative = Path.GetRelativePath(roamingAppData, folder);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return folder;
        var redirected = Path.Combine(localAppData, "Packages", packageFamilyName, "LocalCache", "Roaming", relative);
        return fileExists(Path.Combine(redirected, fileName)) ? redirected : folder;
    }

    /// <summary>The folder that holds the log (homepodcast.log) and config.json, as Explorer sees it.</summary>
    public static string LogFolder => FolderForExplorer(AppConfig.Directory, AppConfig.LogFileName, PackageFamilyName,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists);

    /// <summary>explorer.exe arguments: the folder with <paramref name="file"/> selected, or just the folder.</summary>
    internal static string ExplorerArguments(string folder, string? file) =>
        file != null ? $"/select,\"{Path.Combine(folder, file)}\"" : $"\"{folder}\"";

    /// <summary>Opens the log folder in Explorer, with the log selected when it exists. Never throws.</summary>
    public static void OpenLogFolder()
    {
        try
        {
            var folder = LogFolder;
            Directory.CreateDirectory(folder);
            var file = File.Exists(Path.Combine(folder, AppConfig.LogFileName)) ? AppConfig.LogFileName : null;
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(explorer, ExplorerArguments(folder, file))?.Dispose();
            Log.Info($"opened the log folder {folder}");
        }
        catch (Exception ex)
        {
            Log.Warn($"open the log folder: {ex.Message}");
        }
    }

    /// <summary>Opens a Settings page (ms-settings:…) or another URI through the shell. Never throws.</summary>
    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            Log.Info($"opened {uri}");
        }
        catch (Exception ex)
        {
            Log.Warn($"open {uri}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Windows

    private const int AppModelErrorNoPackage = 15700, ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint length, IntPtr packageFullName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFamilyName(ref uint length, [Out] char[]? packageFamilyName);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr sid);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr FreeSid(IntPtr sid);
}
