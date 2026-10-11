using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HomePodCast.Update;

/// <summary>
/// Installing a downloaded MSI over the running app. Only for MSI installs: the exe runs from the install folder of a
/// product with our UpgradeCode (installer\HomePodCast.wxs), as Windows Installer reports it. A portable copy is
/// never replaced; its 更新 opens the release page.
///
/// The app cannot replace its own exe while running, and must not stay elevated, so a non-elevated PowerShell helper
/// does it after the app has quit: it waits for our process to exit, runs msiexec /passive through UAC (the one
/// prompt the user approves), writes msiexec's exit code to a result file, and starts the app again as the user, with
/// --updated-from=&lt;old version&gt;. Declined, cancelled or failed: the old app starts again and reads the result.
/// </summary>
internal static partial class UpdateInstaller
{
    /// <summary>The MSI's UpgradeCode: the same for every version (never changes).</summary>
    public const string UpgradeCode = "{D9D4B1B3-FBF5-48A0-9B6D-2037DF5764DE}";

    public const string UpdatedFromArgument = "--updated-from=";

    /// <summary>How long the helper waits for the app to finish quitting before it gives up (and installs nothing).</summary>
    private const int QuitTimeoutMs = 120_000;

    /// <summary>The running exe was installed by our MSI (any version), from the folder Windows Installer has for it.</summary>
    public static bool IsMsiInstall(string exePath)
    {
        try
        {
            var locations = InstallLocations().ToList();
            bool msi = locations.Any(l => IsInFolder(exePath, l));
            Log.Info($"update: {(msi ? "MSI install" : "portable copy")} ({exePath}; installed: " +
                     $"{(locations.Count == 0 ? "none" : string.Join(", ", locations.Select(l => l.Length == 0 ? "?" : l)))})");
            return msi;
        }
        catch (Exception ex)
        {
            Log.Warn($"update: Windows Installer query failed ({ex.Message}): treated as a portable copy");
            return false;
        }
    }

    /// <summary><paramref name="exePath"/> is directly in <paramref name="folder"/> (case-insensitive, either may end in a backslash).</summary>
    internal static bool IsInFolder(string exePath, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            var installed = Path.GetFullPath(folder.Trim());
            return dir != null && string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(installed),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false; // not a path
        }
    }

    /// <summary>Every installed product with our UpgradeCode: its InstallLocation ("" when it has none). Read-only.</summary>
    internal static IEnumerable<string> InstallLocations()
    {
        var product = new char[39];
        for (uint i = 0; ; i++)
        {
            uint rc = MsiEnumRelatedProducts(UpgradeCode, 0, i, product);
            if (rc == ErrorNoMoreItems) yield break;
            if (rc != 0) throw new InvalidOperationException($"MsiEnumRelatedProducts: error {rc}");
            var code = new string(product, 0, Array.IndexOf(product, '\0') is var end and >= 0 ? end : product.Length);
            yield return ProductInfo(code, "InstallLocation") ?? "";
        }
    }

    private static string? ProductInfo(string product, string property)
    {
        uint length = 260;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new char[length + 1];
            uint size = (uint)buffer.Length;
            uint rc = MsiGetProductInfo(product, property, buffer, ref size);
            if (rc == 0) return new string(buffer, 0, (int)size);
            if (rc != ErrorMoreData) return null;
            length = size + 1;
        }
        return null;
    }

    private const uint ErrorNoMoreItems = 259, ErrorMoreData = 234;

    [LibraryImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, [Out] char[] productCode);

    [LibraryImport("msi.dll", EntryPoint = "MsiGetProductInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetProductInfo(string product, string property, [Out] char[] value, ref uint length);

    // ---------------------------------------------------------------- the helper

    /// <summary>What the helper works with; every path is a single-quoted literal in its script.</summary>
    /// <param name="Exe">The installed exe: started again afterwards; its folder is the install folder the MSI keeps.</param>
    /// <param name="UpdatedFrom">The running version, passed back as --updated-from=.</param>
    /// <param name="Tray">Start again into the tray (--tray), as the window was hidden.</param>
    internal sealed record Helper(int ProcessId, string Msi, string MsiLog, string Result, string Exe, string UpdatedFrom, bool Tray)
    {
        /// <summary>The install folder, without a trailing backslash (msiexec reads <c>\"</c> as an escaped quote).</summary>
        public string InstallDir => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Exe) ?? "");
    }

    /// <summary>Starts the helper (non-elevated, no window). It does nothing until this process has exited.</summary>
    public static void StartHelper(Helper helper)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var p = Process.Start(new ProcessStartInfo(powershell, HelperArguments(helper))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(), // not the update folder: it is removed on the next start
        }) ?? throw new InvalidOperationException("PowerShell did not start");
        Log.Info($"update: helper started (PowerShell, pid {p.Id}); once pid {helper.ProcessId} has exited it runs " +
                 $"msiexec /i \"{helper.Msi}\" INSTALLFOLDER=\"{helper.InstallDir}\" through UAC, then starts {helper.Exe}");
    }

    /// <summary>powershell.exe arguments: the script as -EncodedCommand, so no command line parses a path (as in Firewall).</summary>
    internal static string HelperArguments(Helper helper) =>
        "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(HelperScript(helper)));

    /// <summary>The paths and values first (one assignment per line), then the steps, which only use the variables.</summary>
    internal static string HelperVariables(Helper h)
    {
        // msiexec gets these in double quotes; no Windows path has one, so one here is not a path.
        foreach (var path in new[] { h.Msi, h.MsiLog, h.InstallDir })
            if (path.Length == 0 || path.Contains('"')) throw new ArgumentException($"not a usable path: {path}");
        var args = new List<string> { UpdatedFromArgument + SafeVersion(h.UpdatedFrom) };
        if (h.Tray) args.Add("--tray");
        return $"""
            $appPid = {h.ProcessId}
            $msi = {Firewall.PowerShellLiteral(h.Msi)}
            $msiLog = {Firewall.PowerShellLiteral(h.MsiLog)}
            $installDir = {Firewall.PowerShellLiteral(h.InstallDir)}
            $result = {Firewall.PowerShellLiteral(h.Result)}
            $exe = {Firewall.PowerShellLiteral(h.Exe)}
            $appArgs = @({string.Join(", ", args.Select(Firewall.PowerShellLiteral))})
            """.ReplaceLineEndings("\n"); // the same script whatever line endings the checkout gave this file (CI: CRLF)
    }

    /// <summary>
    /// The helper. Waits for the app (pid and name, so a reused pid does not count) for up to two minutes, then runs
    /// msiexec elevated (UAC) with a progress bar and no reboot, into the folder the app runs from (a major upgrade
    /// would otherwise go to the default folder), with a verbose log. msiexec's exit code, or 1223 when UAC was
    /// declined, or "timeout" when the app never quit, goes to the result file; then the app starts again from this
    /// non-elevated PowerShell, so it runs as the user.
    /// </summary>
    internal static string HelperScript(Helper h) => HelperVariables(h) + "\n" + HelperSteps;

    private static readonly string HelperSteps = $$"""
        $ErrorActionPreference = 'Stop'
        $app = Get-Process -Id $appPid -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'HomePodCast' }
        if ($app -and -not $app.WaitForExit({{QuitTimeoutMs}})) {
            Set-Content -LiteralPath $result -Value 'timeout' -Encoding Ascii
            exit 1
        }
        $code = 'unknown'
        try {
            $info = New-Object System.Diagnostics.ProcessStartInfo
            $info.FileName = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
            $info.Arguments = '/i "' + $msi + '" /passive /norestart /l*v "' + $msiLog + '" INSTALLFOLDER="' + $installDir + '"'
            $info.Verb = 'runas'
            $info.UseShellExecute = $true
            $installer = [System.Diagnostics.Process]::Start($info)
            $installer.WaitForExit()
            $code = $installer.ExitCode
        } catch {
            $e = $_.Exception
            while ($e.InnerException) { $e = $e.InnerException }
            if ($e -is [System.ComponentModel.Win32Exception]) { $code = $e.NativeErrorCode } else { $code = 'error: ' + $e.Message }
        }
        Set-Content -LiteralPath $result -Value $code -Encoding UTF8
        Start-Process -FilePath $exe -ArgumentList $appArgs -WorkingDirectory $installDir
        """.ReplaceLineEndings("\n");

    /// <summary>A version as it goes on the command line: only the characters a version has.</summary>
    internal static string SafeVersion(string version) =>
        new(version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+').ToArray());

    /// <summary>The version in --updated-from=…, if the app was started with it.</summary>
    public static string? UpdatedFrom(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(UpdatedFromArgument, StringComparison.Ordinal))?[UpdatedFromArgument.Length..];
}
