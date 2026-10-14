namespace HomePodCast.Update;

/// <summary>What 「更新」 does with the release it was offered.</summary>
internal enum InstallRoute
{
    /// <summary>An MSI install and GitHub lists the MSI's SHA-256: download, check, install through the helper.</summary>
    Install,
    /// <summary>A portable copy (or a test profile): open the release page, never install.</summary>
    ReleasePage,
    /// <summary>No MSI or no SHA-256 for it: nothing to check the download against, so nothing is installed.</summary>
    NoChecksum,
}

/// <summary>A downloaded MSI that matched its SHA-256, in its own update folder (<see cref="UpdatePackage.CreateFolder"/>).</summary>
internal sealed record DownloadedUpdate(string Folder, string Msi);

/// <summary>
/// The steps of 设置 → 软件更新 without any UI, shared by the buttons (TrayApp.Update.cs: 「检查更新」 and 「更新」) and
/// the update-selftest command (<see cref="UpdateSelfTest"/>, which CI runs from an installed older MSI): which kind of
/// install this is, the check, what 「更新」 does with the release found, the download with its SHA-256 check, and the
/// start of the installer helper. The UI adds the confirmation, the status line and quitting.
/// </summary>
internal static class UpdateFlow
{
    /// <summary>An MSI install that 「更新」 replaces; a portable copy opens the release page. A test copy never installs over the real one.</summary>
    public static bool IsMsiInstall() => AppConfig.Profile == null && UpdateInstaller.IsMsiInstall(Environment.ProcessPath ?? "");

    /// <summary>One GET of <paramref name="feed"/>'s release list (off the calling thread), parsed. Throws for an HTTP error.</summary>
    public static async Task<List<Release>> FetchReleasesAsync(ReleaseFeed feed, CancellationToken ct)
    {
        var json = await Task.Run(async () =>
        {
            using var http = GitHubReleases.CreateClient(ReleaseVersion.CurrentText, GitHubReleases.Timeout, feed.FollowRedirects);
            return await GitHubReleases.FetchAsync(http, feed, ct);
        });
        return GitHubReleases.Parse(json, feed);
    }

    /// <summary>
    /// An exception for the log: each level's type and message, outermost first ("HttpRequestException: … ←
    /// SocketException: …"). 2026-10-14: a check failed 2 ms after it started and the log said only "check failed: ",
    /// an empty message with no hint where it came from.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 5; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {(string.IsNullOrWhiteSpace(e.Message) ? "(no message)" : e.Message)}");
        return string.Join(" ← ", parts);
    }

    /// <summary>What 「更新」 does with <paramref name="release"/> in an MSI install or a portable copy.</summary>
    public static InstallRoute Route(Release release, bool msiInstall) =>
        !msiInstall ? InstallRoute.ReleasePage : release.Msi is { Sha256: not null } ? InstallRoute.Install : InstallRoute.NoChecksum;

    /// <summary>
    /// Downloads the release's MSI into a new update folder and checks it against the SHA-256 GitHub lists for it
    /// (<see cref="UpdatePackage.DownloadAsync"/>). On any failure, a mismatch (<see cref="UpdateVerificationException"/>)
    /// or cancellation the folder is removed again and the exception goes on to the caller.
    /// </summary>
    public static async Task<DownloadedUpdate> DownloadAsync(ReleaseFeed feed, Release release, IProgress<double>? progress, CancellationToken ct)
    {
        var asset = release.Msi ?? throw new UpdateCheckException("the release has no MSI");
        string? folder = null;
        try
        {
            folder = UpdatePackage.CreateFolder();
            Log.Info($"update: downloading {asset.Url} ({asset.Size} bytes) to {folder}");
            var msi = await Task.Run(async () =>
            {
                using var http = GitHubReleases.CreateClient(ReleaseVersion.CurrentText, TimeSpan.FromMinutes(10), feed.FollowRedirects);
                return await UpdatePackage.DownloadAsync(http, release, folder, progress, ct);
            }, ct);
            Log.Info($"update: {msi} matches GitHub's SHA-256 {asset.Sha256}");
            return new DownloadedUpdate(folder, msi);
        }
        catch
        {
            UpdatePackage.TryDelete(folder);
            throw;
        }
    }

    /// <summary>
    /// Starts the installer helper for <paramref name="update"/> (<see cref="UpdateInstaller.StartHelper"/>): once this
    /// process has exited it installs the MSI through UAC into the folder this exe runs from, then starts this exe again
    /// with --updated-from=&lt;this version&gt; (and --tray when <paramref name="tray"/>). The caller quits right after.
    /// </summary>
    public static void StartInstaller(DownloadedUpdate update, bool tray) =>
        UpdateInstaller.StartHelper(new UpdateInstaller.Helper(Environment.ProcessId, update.Msi, Path.Combine(update.Folder, UpdatePackage.MsiLogFile),
            Path.Combine(update.Folder, UpdatePackage.ResultFile), Environment.ProcessPath!, ReleaseVersion.CurrentText, tray));
}
