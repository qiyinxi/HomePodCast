namespace HomePodCast.Update;

/// <summary>
/// Where the update check reads the release list, and the only place an MSI from it is downloaded from. The app always
/// uses <see cref="GitHub"/>. <see cref="Loopback"/> is a fake release list on this PC, for the update-selftest command
/// only (CI: tools/ci/update-e2e.ps1): there is no setting, environment variable or config key for another feed, and
/// the command line takes nothing but http://127.0.0.1 or http://localhost.
/// </summary>
internal sealed class ReleaseFeed
{
    private ReleaseFeed(string apiUrl, string downloadPrefix, bool followRedirects)
    {
        ApiUrl = apiUrl;
        DownloadPrefix = downloadPrefix;
        FollowRedirects = followRedirects;
    }

    /// <summary>The release list (one GET).</summary>
    public string ApiUrl { get; }

    /// <summary>An MSI is downloaded only from a URL that starts with this, whatever the list says.</summary>
    public string DownloadPrefix { get; }

    /// <summary>GitHub answers a download with a redirect to its file host; a loopback feed's files never leave the PC.</summary>
    public bool FollowRedirects { get; }

    /// <summary>The repository's releases on GitHub: what the app checks.</summary>
    public static ReleaseFeed GitHub { get; } = new(GitHubReleases.ApiUrl, GitHubReleases.DownloadPrefix, followRedirects: true);

    /// <summary>Only CI can substitute a loopback list for GUI smoke. No persistent feed setting.</summary>
    internal static ReleaseFeed ForGuiSmoke(string[] args, bool ci)
    {
        var option = args.SingleOrDefault(a => a.StartsWith("--gui-update-feed=", StringComparison.Ordinal));
        if (option == null) return GitHub;
        if (!ci) throw new ArgumentException("--gui-update-feed is only for GitHub Actions GUI smoke tests");
        return Loopback(option["--gui-update-feed=".Length..]);
    }

    /// <summary>
    /// A release list served on this PC: <paramref name="url"/> must be http://127.0.0.1[:port]/… or
    /// http://localhost[:port]/… (no user name, no https, no other host). Its MSIs must be on the same scheme, host and
    /// port, and no redirect is followed. Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public static ReleaseFeed Loopback(string? url)
    {
        if (!IsLoopbackUrl(url, out var uri))
            throw new ArgumentException($"--feed must be an http://127.0.0.1 or http://localhost URL (a fake release list on this PC), not \"{url}\"");
        return new ReleaseFeed(uri.AbsoluteUri, uri.GetLeftPart(UriPartial.Authority) + "/", followRedirects: false);
    }

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost"];

    /// <summary>The text and the parsed URL both say http and 127.0.0.1 or localhost (so "127.0.0.1.example" or "localhost@host" do not pass).</summary>
    internal static bool IsLoopbackUrl(string? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(url) || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')) return false;
        if (!LoopbackHosts.Any(h => url.StartsWith("http://" + h, StringComparison.OrdinalIgnoreCase) &&
                                    (url.Length == 7 + h.Length || url[7 + h.Length] is ':' or '/'))) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp || parsed.UserInfo.Length != 0 || !parsed.IsLoopback ||
            !LoopbackHosts.Contains(parsed.Host, StringComparer.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }
}
