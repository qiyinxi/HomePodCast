using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomePodCast.Update;

/// <summary>Which releases count as updates (设置 → 更新渠道).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
public enum UpdateChannel
{
    /// <summary>正式版: stable releases only.</summary>
    Stable,
    /// <summary>测试版: prereleases and stable releases, the highest version wins.</summary>
    Beta,
}

/// <summary>A published release as the GitHub API lists it, with its MSI if it has one.</summary>
/// <param name="PageUrl">The release's page on github.com (its notes; the portable zip is there too).</param>
/// <param name="Prerelease">Marked as a prerelease on GitHub, or a prerelease version.</param>
internal sealed record Release(ReleaseVersion Version, string Tag, string PageUrl, bool Prerelease, ReleaseAsset? Msi);

/// <param name="Sha256">The SHA-256 GitHub computed when the file was uploaded (the asset's "digest"), lower-case hex; null if none.</param>
internal sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

internal enum CheckOutcome
{
    /// <summary>A newer release than the running one, not skipped.</summary>
    UpdateAvailable,
    UpToDate,
    /// <summary>The newest release is the one the user skipped.</summary>
    Skipped,
    /// <summary>正式版 channel while running a prerelease that is newer than the latest stable release (never a downgrade).</summary>
    PrereleaseAhead,
    /// <summary>No release on the channel at all.</summary>
    NoRelease,
}

/// <param name="Release">The newest release on the channel (the update, when there is one).</param>
internal sealed record CheckResult(CheckOutcome Outcome, Release? Release);

/// <summary>
/// The update check: one GET of the repository's release list from api.github.com, the only request HomePodCast
/// makes outside the local network (only when the user asks for it or turned on automatic checks). No account,
/// no token, no data about the PC or the speaker: the request carries "HomePodCast/&lt;version&gt;" as its User-Agent.
/// </summary>
internal static class GitHubReleases
{
    public const string ApiUrl = "https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20";
    public const string ReleasesPage = "https://github.com/qiyinxi/HomePodCast/releases";

    /// <summary>Only files and pages of this repository are downloaded or opened, whatever the response says.</summary>
    internal const string DownloadPrefix = ReleasesPage + "/download/";
    private const string PagePrefix = ReleasesPage + "/";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <param name="followRedirects">False only for a loopback feed (<see cref="ReleaseFeed.FollowRedirects"/>).</param>
    public static HttpClient CreateClient(string version, TimeSpan timeout, bool followRedirects = true)
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = followRedirects })
        {
            Timeout = timeout,
        };
        if (!http.DefaultRequestHeaders.UserAgent.TryParseAdd("HomePodCast/" + version)) http.DefaultRequestHeaders.UserAgent.ParseAdd("HomePodCast");
        return http;
    }

    /// <summary>The release list as JSON. Throws <see cref="UpdateCheckException"/> for an HTTP error (rate limit included).</summary>
    public static Task<string> FetchAsync(HttpClient http, CancellationToken ct) => FetchAsync(http, ReleaseFeed.GitHub, ct);

    /// <summary>The release list of <paramref name="feed"/> as JSON (the same request as GitHub's).</summary>
    public static async Task<string> FetchAsync(HttpClient http, ReleaseFeed feed, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feed.ApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string detail = $"HTTP {(int)response.StatusCode}";
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0")
            {
                detail += ", rate limited";
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var unix))
                    detail += $" until {DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime():HH:mm}";
            }
            throw new UpdateCheckException(detail);
        }
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The published releases in <paramref name="json"/> (drafts and tags that are no version left out).</summary>
    public static List<Release> Parse(string json) => Parse(json, ReleaseFeed.GitHub);

    /// <summary>The same, with the MSIs that <paramref name="feed"/> downloads from (GitHub's: this repository's release files).</summary>
    public static List<Release> Parse(string json, ReleaseFeed feed)
    {
        var list = new List<Release>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new UpdateCheckException("unexpected response (not a list)");
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft")) continue;
            var tag = Str(r, "tag_name");
            if (ReleaseVersion.Parse(tag) is not { } version) continue;
            var page = Str(r, "html_url") is { } url && url.StartsWith(PagePrefix, StringComparison.Ordinal)
                ? url
                : PagePrefix + "tag/" + Uri.EscapeDataString(tag!);
            list.Add(new Release(version, tag!, page, Bool(r, "prerelease") || version.IsPrerelease, Msi(r, feed.DownloadPrefix)));
        }
        return list;
    }

    /// <summary>The release's x64 MSI, once it is fully uploaded; one from another place is ignored.</summary>
    private static ReleaseAsset? Msi(JsonElement release, string downloadPrefix)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name");
            var url = Str(a, "browser_download_url");
            if (name == null || url == null || !name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(a, "state") is { } state && state != "uploaded") continue;
            if (!url.StartsWith(downloadPrefix, StringComparison.Ordinal)) continue;
            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new ReleaseAsset(name, url, size, UpdatePackage.Sha256FromDigest(Str(a, "digest")));
        }
        return null;
    }

    /// <summary>
    /// What the check found for the running version: the highest release on the channel (正式版: stable releases only;
    /// 测试版: both), counted only with an MSI when this is an MSI install (a release whose files are still being
    /// uploaded is not ready yet). Never a downgrade: a release is an update only when it is newer, so a prerelease
    /// stays until a stable release of at least its X.Y.Z appears (0.2.2 is newer than 0.2.2-beta.1).
    /// </summary>
    public static CheckResult Pick(IEnumerable<Release> releases, UpdateChannel channel, ReleaseVersion current, string? skipped, bool needMsi)
    {
        var latest = releases
            .Where(r => channel == UpdateChannel.Beta || !r.Prerelease)
            .Where(r => !needMsi || r.Msi != null)
            .MaxBy(r => r.Version);
        if (latest == null) return new CheckResult(CheckOutcome.NoRelease, null);
        if (latest.Version <= current)
        {
            bool ahead = channel == UpdateChannel.Stable && current.IsPrerelease && latest.Version < current;
            return new CheckResult(ahead ? CheckOutcome.PrereleaseAhead : CheckOutcome.UpToDate, latest);
        }
        if (ReleaseVersion.Parse(skipped) is { } skip && skip.Equals(latest.Version)) return new CheckResult(CheckOutcome.Skipped, latest);
        return new CheckResult(CheckOutcome.UpdateAvailable, latest);
    }

    /// <summary>The channel in effect: the chosen one, else 测试版 for a prerelease build and 正式版 otherwise.</summary>
    public static UpdateChannel EffectiveChannel(UpdateChannel? chosen, ReleaseVersion? current) =>
        chosen ?? (current is { IsPrerelease: true } ? UpdateChannel.Beta : UpdateChannel.Stable);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>The check or the download failed in a way worth one log line (HTTP status, rate limit, bad file).</summary>
internal sealed class UpdateCheckException(string message) : Exception(message);
