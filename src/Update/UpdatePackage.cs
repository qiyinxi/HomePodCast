using System.Security.Cryptography;

namespace HomePodCast.Update;

/// <summary>
/// The downloaded MSI: a fresh folder under %TEMP% per download, the file checked against the SHA-256 GitHub lists
/// for it, and the folder removed again on the next start (with the installer helper's result read first).
/// </summary>
internal static class UpdatePackage
{
    /// <summary>Folders are %TEMP%\HomePodCast-update-&lt;random&gt;.</summary>
    public const string FolderPrefix = "HomePodCast-update-";

    /// <summary>What the installer helper writes (msiexec's exit code, see <see cref="UpdateInstaller"/>) and its msiexec log.</summary>
    public const string ResultFile = "result.txt", MsiLogFile = "msiexec.log";

    /// <summary>The release files are about 2 MB; anything far larger is not ours.</summary>
    private const long MaxBytes = 200L << 20;

    /// <summary>"sha256:&lt;64 hex digits&gt;" (the API's "digest") → the lower-case hex digits; null for anything else.</summary>
    public static string? Sha256FromDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>A file's SHA-256 matches <paramref name="sha256"/> (lower-case hex).</summary>
    public static bool Matches(string file, string sha256)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == sha256;
    }

    public static string CreateFolder() => Directory.CreateTempSubdirectory(FolderPrefix).FullName;

    /// <summary>
    /// Downloads the MSI into <paramref name="folder"/> as HomePodCast-&lt;version&gt;-x64.msi and checks its SHA-256.
    /// The file is deleted again when it does not match, is too large, or the download fails or is cancelled.
    /// </summary>
    /// <param name="progress">Fraction done, 0 to 1 (when the size is known).</param>
    public static async Task<string> DownloadAsync(HttpClient http, Release release, string folder, IProgress<double>? progress,
        CancellationToken ct)
    {
        var asset = release.Msi ?? throw new UpdateCheckException("the release has no MSI");
        var sha256 = asset.Sha256 ?? throw new UpdateCheckException("GitHub lists no SHA-256 for the MSI");
        var path = Path.Combine(folder, $"HomePodCast-{release.Version}-x64.msi");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateCheckException($"download: HTTP {(int)response.StatusCode}");
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            if (total > MaxBytes) throw new UpdateCheckException($"download: {total} bytes is too large");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    done += n;
                    if (done > MaxBytes) throw new UpdateCheckException("download: too large");
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != sha256) throw new UpdateVerificationException($"SHA-256 {actual} does not match GitHub's {sha256}");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Removes every update folder under <paramref name="tempRoot"/> and returns the newest installer result found in
    /// them (null if none): what happened to the update this start may be the result of. msiexec's log of a failed
    /// install is copied to <paramref name="keepDir"/> first. A folder still in use stays for the next start.
    /// </summary>
    public static InstallResult? CleanUp(string tempRoot, string keepDir)
    {
        InstallResult? newest = null;
        DateTime newestTime = DateTime.MinValue;
        IEnumerable<DirectoryInfo> folders;
        try { folders = new DirectoryInfo(tempRoot).EnumerateDirectories(FolderPrefix + "*").ToList(); }
        catch (Exception ex)
        {
            Log.Warn($"update: listing {tempRoot}: {ex.Message}");
            return null;
        }
        foreach (var dir in folders)
        {
            var result = Path.Combine(dir.FullName, ResultFile);
            try
            {
                if (File.Exists(result) && File.GetLastWriteTimeUtc(result) is var time && time > newestTime)
                {
                    newestTime = time;
                    newest = InstallResult.Parse(File.ReadAllText(result), Path.Combine(dir.FullName, MsiLogFile));
                    if (newest is { Succeeded: false } failed) newest = failed with { MsiLog = KeepLog(failed.MsiLog, keepDir) };
                }
                dir.Delete(recursive: true);
                Log.Info($"update: removed {dir.FullName}");
            }
            catch (Exception ex)
            {
                Log.Warn($"update: could not remove {dir.FullName} yet: {ex.Message}");
            }
        }
        return newest;
    }

    /// <summary>msiexec's log of a failed install, copied next to our own log before its folder goes.</summary>
    private static string? KeepLog(string? log, string keepDir)
    {
        if (log == null || !File.Exists(log)) return null;
        try
        {
            Directory.CreateDirectory(keepDir);
            var keep = Path.Combine(keepDir, "update-msiexec.log");
            File.Copy(log, keep, overwrite: true);
            return keep;
        }
        catch (Exception ex)
        {
            Log.Warn($"update: keeping {log}: {ex.Message}");
            return null;
        }
    }

    public static void TryDelete(string? path)
    {
        if (path == null) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"update: delete {path}: {ex.Message}");
        }
    }
}

/// <summary>The downloaded file is not the one GitHub listed (SHA-256 mismatch): deleted, never installed.</summary>
internal sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>What the installer helper wrote: msiexec's exit code, or why it never ran.</summary>
/// <param name="Code">msiexec's exit code; <see cref="Cancelled"/> when UAC was declined; null when it never ran.</param>
/// <param name="Text">The raw result ("0", "1223", "timeout", …).</param>
/// <param name="MsiLog">msiexec's verbose log, if kept.</param>
internal sealed record InstallResult(int? Code, string Text, string? MsiLog)
{
    /// <summary>ERROR_CANCELLED: UAC declined.</summary>
    public const int Cancelled = 1223;

    /// <summary>ERROR_INSTALL_USEREXIT: cancelled in msiexec's progress window.</summary>
    public const int UserExit = 1602;

    /// <summary>0, or 3010 / 1641 (done, a restart is needed / was started).</summary>
    public bool Succeeded => Code is 0 or 3010 or 1641;

    public bool WasCancelled => Code is Cancelled or UserExit;

    public static InstallResult Parse(string text, string? msiLog)
    {
        var t = text.Trim().TrimStart('﻿').Trim(); // Windows PowerShell writes UTF-8 with a BOM
        return new InstallResult(int.TryParse(t, out var code) ? code : null, t, msiLog);
    }
}
