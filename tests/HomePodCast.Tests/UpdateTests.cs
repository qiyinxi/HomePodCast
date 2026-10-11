using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomePodCast.Update;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

public class ReleaseVersionTests
{
    private static ReleaseVersion V(string s) => ReleaseVersion.Parse(s) ?? throw new ArgumentException(s);

    [Theory]
    [InlineData("0.2.2", "0.2.2")]
    [InlineData("v0.2.2", "0.2.2")]
    [InlineData("V1.10.0", "1.10.0")]
    [InlineData("0.2.2-beta.1", "0.2.2-beta.1")]
    [InlineData("v0.2.2-beta.1", "0.2.2-beta.1")]
    [InlineData("0.2.2-beta.1+abc123", "0.2.2-beta.1")] // the SDK's informational version
    [InlineData("0.2.2+6f14c7d", "0.2.2")]
    [InlineData(" 0.2.0-dev.57 ", "0.2.0-dev.57")]
    [InlineData("0.2.0-pr.3.12", "0.2.0-pr.3.12")]
    public void Parses_release_tags_and_build_versions(string text, string expected) => Assert.Equal(expected, V(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("0.2")]
    [InlineData("0.2.2.1")]
    [InlineData("0.2.x")]
    [InlineData("0.2.2-")]
    [InlineData("0.2.2-beta..1")]
    [InlineData("0.2.2-beta_1")]
    [InlineData("latest")]
    [InlineData("0.-2.2")]
    public void Rejects_what_is_no_version(string? text) => Assert.Null(ReleaseVersion.Parse(text));

    [Fact]
    public void Orders_by_semver_precedence()
    {
        string[] ascending =
        [
            "0.1.0", "0.2.0-beta.8", "0.2.0", "0.2.1",
            "0.2.2-beta.1", "0.2.2-beta.2", "0.2.2-beta.9", "0.2.2-beta.10", "0.2.2-rc.1", "0.2.2",
            "0.2.3-beta.1", "0.2.10", "0.10.0", "1.0.0",
        ];
        for (int i = 0; i < ascending.Length; i++)
            for (int j = 0; j < ascending.Length; j++)
                Assert.True(Math.Sign(V(ascending[i]).CompareTo(V(ascending[j]))) == Math.Sign(i.CompareTo(j)),
                    $"{ascending[i]} vs {ascending[j]}");
        var shuffled = Enumerable.Reverse(ascending).Select(V).ToList();
        shuffled.Sort();
        Assert.Equal(ascending, shuffled.Select(v => v.ToString()));
    }

    [Fact]
    public void Follows_the_semver_spec_example()
    {
        // semver.org, item 11
        string[] ascending = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
        for (int i = 1; i < ascending.Length; i++) Assert.True(V(ascending[i - 1]) < V(ascending[i]), $"{ascending[i - 1]} < {ascending[i]}");
    }

    [Fact]
    public void Build_metadata_does_not_count()
    {
        Assert.Equal(V("0.2.2+a"), V("0.2.2+b"));
        Assert.True(V("0.2.2") >= V("0.2.2+zzz"));
        Assert.Equal(V("0.2.2-beta.1").GetHashCode(), V("v0.2.2-beta.1+x").GetHashCode());
    }

    [Theory]
    [InlineData("0.2.2", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.2.2-beta.1", true)]
    [InlineData("0.2.2-beta.12", true)]
    [InlineData("0.2.2-rc.1", true)]
    [InlineData("0.2.0-dev.57", false)]          // dev branch build
    [InlineData("0.2.0-pr.3.12", false)]         // PR build
    [InlineData("0.2.0-local.1", false)]         // installer\build.ps1 example
    [InlineData("0.2.0-release-0-2.4", false)]   // release/0.2 branch build
    [InlineData("0.2.0-beta", false)]
    [InlineData("0.2.0-beta.x", false)]
    [InlineData("0.2.0-beta.1.2", false)]
    public void Only_tag_versions_are_release_builds(string version, bool release) => Assert.Equal(release, V(version).IsReleaseBuild);

    [Fact]
    public void Development_builds_and_test_profiles_never_check_by_themselves()
    {
        Assert.Null(ReleaseVersion.WhyNotAutomatic("0.2.2", null, debugBuild: false));
        Assert.Null(ReleaseVersion.WhyNotAutomatic("0.2.3-beta.1", null, debugBuild: false));
        Assert.Contains("profile", ReleaseVersion.WhyNotAutomatic("0.2.2", "test", debugBuild: false));
        Assert.Contains("debug", ReleaseVersion.WhyNotAutomatic("0.2.2", null, debugBuild: true));
        Assert.Contains("development", ReleaseVersion.WhyNotAutomatic("0.2.0-dev.57", null, debugBuild: false));
        Assert.Contains("development", ReleaseVersion.WhyNotAutomatic("0.2.0-pr.3.12", null, debugBuild: false));
        Assert.Contains("development", ReleaseVersion.WhyNotAutomatic("0.2.0-local.1", null, debugBuild: false));
        Assert.NotNull(ReleaseVersion.WhyNotAutomatic("garbage", null, debugBuild: false));
    }

    [Fact]
    public void The_running_version_is_the_informational_version_without_metadata()
    {
        var current = ReleaseVersion.CurrentText;
        Assert.DoesNotContain("+", current);
        Assert.NotNull(ReleaseVersion.Parse(current));
    }
}

public class ReleaseSelectionTests
{
    private const string Digest1 = "475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e";

    /// <summary>The API's shape (GET /repos/qiyinxi/HomePodCast/releases, 2026-10-11), trimmed to the fields that matter.</summary>
    internal static string Fixture => $$"""
        [
          { "tag_name": "v0.3.0", "draft": true, "prerelease": false, "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/untagged-1",
            "assets": [ {{Msi("0.3.0", Digest1)}} ] },
          { "tag_name": "v0.2.3-beta.2", "draft": false, "prerelease": true, "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/v0.2.3-beta.2",
            "assets": [ {{Zip("0.2.3-beta.2")}} ] },
          { "tag_name": "v0.2.3-beta.1", "draft": false, "prerelease": true, "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/v0.2.3-beta.1",
            "assets": [ {{Zip("0.2.3-beta.1")}}, {{Msi("0.2.3-beta.1", "SHA256:" + Digest1.ToUpperInvariant())}} ] },
          { "tag_name": "v0.2.2", "draft": false, "prerelease": false, "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/v0.2.2",
            "assets": [ {{Zip("0.2.2")}}, {{Msi("0.2.2", Digest1)}} ] },
          { "tag_name": "v0.2.2-beta.1", "draft": false, "prerelease": true, "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/v0.2.2-beta.1",
            "assets": [ {{Msi("0.2.2-beta.1", Digest1)}} ] },
          { "tag_name": "v0.2.1", "draft": false, "prerelease": false, "html_url": "https://evil.example/releases/v0.2.1",
            "assets": [ {{Msi("0.2.1", null)}} ] },
          { "tag_name": "nightly", "draft": false, "prerelease": true, "assets": [] },
          { "tag_name": "v0.1.0", "draft": false, "prerelease": false, "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/v0.1.0",
            "assets": [ {{Zip("0.1.0")}} ] }
        ]
        """;

    private static string Msi(string version, string? digest, string state = "uploaded", string? url = null) => $$"""
        { "name": "HomePodCast-{{version}}-x64.msi", "content_type": "application/x-msdownload", "state": "{{state}}", "size": 1761280,
          "digest": {{(digest == null ? "null" : $"\"{(digest.Contains(':') ? digest : "sha256:" + digest)}\"")}},
          "browser_download_url": "{{url ?? $"https://github.com/qiyinxi/HomePodCast/releases/download/v{version}/HomePodCast-{version}-x64.msi"}}" }
        """;

    private static string Zip(string version) => $$"""
        { "name": "HomePodCast-{{version}}-win-x64.zip", "state": "uploaded", "size": 761269,
          "digest": "sha256:325a054a6fdcf168900b63e9d500e3f840fd67b0e62d8c93214be625717928b8",
          "browser_download_url": "https://github.com/qiyinxi/HomePodCast/releases/download/v{{version}}/HomePodCast-{{version}}-win-x64.zip" }
        """;

    private static List<Release> Releases => GitHubReleases.Parse(Fixture);
    private static ReleaseVersion V(string s) => ReleaseVersion.Parse(s)!;

    private static CheckResult Pick(UpdateChannel channel, string current, string? skipped = null, bool needMsi = true) =>
        GitHubReleases.Pick(Releases, channel, V(current), skipped, needMsi);

    [Fact]
    public void Parse_leaves_out_drafts_and_tags_that_are_no_version()
    {
        var releases = Releases;
        Assert.Equal(new[] { "0.2.3-beta.2", "0.2.3-beta.1", "0.2.2", "0.2.2-beta.1", "0.2.1", "0.1.0" }, releases.Select(r => r.Version.ToString()));
        Assert.Equal(new[] { true, true, false, true, false, false }, releases.Select(r => r.Prerelease));
    }

    [Fact]
    public void Parse_reads_the_msi_and_its_digest()
    {
        var r = Releases.Single(r => r.Tag == "v0.2.2");
        Assert.Equal("https://github.com/qiyinxi/HomePodCast/releases/tag/v0.2.2", r.PageUrl);
        Assert.NotNull(r.Msi);
        Assert.Equal("HomePodCast-0.2.2-x64.msi", r.Msi.Name);
        Assert.Equal("https://github.com/qiyinxi/HomePodCast/releases/download/v0.2.2/HomePodCast-0.2.2-x64.msi", r.Msi.Url);
        Assert.Equal(1761280, r.Msi.Size);
        Assert.Equal(Digest1, r.Msi.Sha256);
        Assert.Equal(Digest1, Releases.Single(r => r.Tag == "v0.2.3-beta.1").Msi!.Sha256); // "SHA256:<UPPER>" too
        Assert.Null(Releases.Single(r => r.Tag == "v0.2.3-beta.2").Msi);                  // zip only
        Assert.Null(Releases.Single(r => r.Tag == "v0.2.1").Msi!.Sha256);                  // no digest
    }

    [Fact]
    public void Pages_and_files_from_anywhere_else_are_ignored()
    {
        // A page outside the repository becomes the tag's page there.
        Assert.Equal("https://github.com/qiyinxi/HomePodCast/releases/tag/v0.2.1", Releases.Single(r => r.Tag == "v0.2.1").PageUrl);
        var json = $$"""
            [ { "tag_name": "v9.0.0", "prerelease": false, "assets": [
                {{Msi("9.0.0", Digest1, url: "https://evil.example/HomePodCast-9.0.0-x64.msi")}},
                {{Msi("9.0.0", Digest1, url: "http://github.com/qiyinxi/HomePodCast/releases/download/v9.0.0/HomePodCast-9.0.0-x64.msi")}},
                {{Msi("9.0.0", Digest1, url: "https://github.com/someone/HomePodCast/releases/download/v9.0.0/HomePodCast-9.0.0-x64.msi")}} ] } ]
            """;
        Assert.Null(Assert.Single(GitHubReleases.Parse(json)).Msi);
    }

    [Fact]
    public void An_msi_still_uploading_is_not_ready()
    {
        var json = $"[ {{ \"tag_name\": \"v9.0.0\", \"prerelease\": false, \"assets\": [ {Msi("9.0.0", Digest1, state: "new")} ] }} ]";
        Assert.Null(Assert.Single(GitHubReleases.Parse(json)).Msi);
    }

    [Fact]
    public void Parse_rejects_what_is_not_a_release_list()
    {
        Assert.Throws<UpdateCheckException>(() => GitHubReleases.Parse("""{ "message": "API rate limit exceeded" }"""));
        Assert.ThrowsAny<JsonException>(() => GitHubReleases.Parse("<html>"));
        Assert.Empty(GitHubReleases.Parse("[]"));
    }

    [Fact]
    public void Stable_channel_offers_the_newest_stable_release_with_an_msi()
    {
        var r = Pick(UpdateChannel.Stable, "0.2.1");
        Assert.Equal(CheckOutcome.UpdateAvailable, r.Outcome);
        Assert.Equal("0.2.2", r.Release!.Version.ToString());
        Assert.Equal(CheckOutcome.UpToDate, Pick(UpdateChannel.Stable, "0.2.2").Outcome);
        Assert.Equal(CheckOutcome.UpToDate, Pick(UpdateChannel.Stable, "0.2.5").Outcome);
    }

    [Fact]
    public void Beta_channel_offers_the_highest_of_both()
    {
        // 0.2.3-beta.2 has no MSI yet: an MSI install gets 0.2.3-beta.1, a portable copy (release page) 0.2.3-beta.2.
        Assert.Equal("0.2.3-beta.1", Pick(UpdateChannel.Beta, "0.2.1").Release!.Version.ToString());
        Assert.Equal("0.2.3-beta.2", Pick(UpdateChannel.Beta, "0.2.1", needMsi: false).Release!.Version.ToString());
        Assert.Equal(CheckOutcome.UpdateAvailable, Pick(UpdateChannel.Beta, "0.2.2").Outcome); // stable → newer beta
        Assert.Equal(CheckOutcome.UpToDate, Pick(UpdateChannel.Beta, "0.2.3-beta.1").Outcome);
        Assert.Equal(CheckOutcome.UpdateAvailable, Pick(UpdateChannel.Beta, "0.2.3-beta.1", needMsi: false).Outcome);

        // A stable release wins over the betas of its own version.
        var list = new[] { Rel("0.2.2-beta.2", pre: true), Rel("0.2.2", pre: false), Rel("0.2.2-beta.9", pre: true) };
        Assert.Equal("0.2.2", GitHubReleases.Pick(list, UpdateChannel.Beta, V("0.2.2-beta.1"), null, true).Release!.Version.ToString());
    }

    [Fact]
    public void A_release_without_an_msi_is_left_out_for_msi_installs_only()
    {
        var list = new[] { Rel("0.2.2", pre: false), Rel("0.2.3", pre: false, msi: false) };
        Assert.Equal("0.2.2", GitHubReleases.Pick(list, UpdateChannel.Stable, V("0.2.1"), null, needMsi: true).Release!.Version.ToString());
        Assert.Equal("0.2.3", GitHubReleases.Pick(list, UpdateChannel.Stable, V("0.2.1"), null, needMsi: false).Release!.Version.ToString());
        Assert.Equal(CheckOutcome.NoRelease, GitHubReleases.Pick([Rel("0.2.3", false, msi: false)], UpdateChannel.Stable, V("0.2.1"), null, true).Outcome);
        Assert.Equal(CheckOutcome.NoRelease, GitHubReleases.Pick([], UpdateChannel.Beta, V("0.2.1"), null, false).Outcome);
    }

    [Fact]
    public void A_skipped_version_is_not_offered_but_a_newer_one_is()
    {
        var skipped = Pick(UpdateChannel.Stable, "0.2.1", skipped: "0.2.2");
        Assert.Equal(CheckOutcome.Skipped, skipped.Outcome);
        Assert.Equal("0.2.2", skipped.Release!.Version.ToString());
        Assert.Equal(CheckOutcome.UpdateAvailable, Pick(UpdateChannel.Stable, "0.2.1", skipped: "0.2.2-beta.1").Outcome);
        Assert.Equal(CheckOutcome.UpdateAvailable, Pick(UpdateChannel.Beta, "0.2.1", skipped: "0.2.2").Outcome); // 0.2.3-beta.1 is newer
        Assert.Equal(CheckOutcome.UpdateAvailable, Pick(UpdateChannel.Stable, "0.2.1", skipped: "not a version").Outcome);
    }

    [Fact]
    public void A_prerelease_flagged_stable_by_mistake_still_counts_as_prerelease()
    {
        var list = new[] { Rel("0.2.2", pre: false), Rel("0.2.3-beta.1", pre: false) }; // as Parse marks it from the version
        var parsed = GitHubReleases.Parse("""[ { "tag_name": "v0.2.3-beta.1", "prerelease": false, "assets": [] } ]""");
        Assert.True(Assert.Single(parsed).Prerelease);
        Assert.Equal("0.2.2", GitHubReleases.Pick(list.Select(r => r with { Prerelease = r.Version.IsPrerelease }), UpdateChannel.Stable,
            V("0.2.1"), null, true).Release!.Version.ToString());
    }

    // ---------------------------------------------------------------- channel switches

    [Fact]
    public void Switching_a_beta_to_the_stable_channel_never_offers_a_downgrade()
    {
        // Running 0.2.2-beta.1, latest stable 0.2.1: nothing to offer until a stable release of at least 0.2.2.
        var list = new[] { Rel("0.2.1", false), Rel("0.2.2-beta.1", true), Rel("0.2.0", false) };
        var r = GitHubReleases.Pick(list, UpdateChannel.Stable, V("0.2.2-beta.1"), null, true);
        Assert.Equal(CheckOutcome.PrereleaseAhead, r.Outcome);
        Assert.Equal("0.2.1", r.Release!.Version.ToString());

        // The stable release of the same X.Y.Z is an update.
        list = [.. list, Rel("0.2.2", false)];
        r = GitHubReleases.Pick(list, UpdateChannel.Stable, V("0.2.2-beta.1"), null, true);
        Assert.Equal(CheckOutcome.UpdateAvailable, r.Outcome);
        Assert.Equal("0.2.2", r.Release!.Version.ToString());

        // A newer beta on the stable channel is still not offered.
        list = [Rel("0.2.1", false), Rel("0.2.2-beta.2", true)];
        Assert.Equal(CheckOutcome.PrereleaseAhead, GitHubReleases.Pick(list, UpdateChannel.Stable, V("0.2.2-beta.1"), null, true).Outcome);
        Assert.Equal(CheckOutcome.UpdateAvailable, GitHubReleases.Pick(list, UpdateChannel.Beta, V("0.2.2-beta.1"), null, true).Outcome);
    }

    [Fact]
    public void Stable_builds_on_the_stable_channel_ignore_betas()
    {
        var list = new[] { Rel("0.2.2", false), Rel("0.2.3-beta.1", true) };
        Assert.Equal(CheckOutcome.UpToDate, GitHubReleases.Pick(list, UpdateChannel.Stable, V("0.2.2"), null, true).Outcome);
        Assert.Equal(CheckOutcome.UpdateAvailable, GitHubReleases.Pick(list, UpdateChannel.Beta, V("0.2.2"), null, true).Outcome);
    }

    [Fact]
    public void The_default_channel_follows_the_running_version_until_one_is_chosen()
    {
        Assert.Equal(UpdateChannel.Beta, GitHubReleases.EffectiveChannel(null, V("0.2.2-beta.1")));
        Assert.Equal(UpdateChannel.Stable, GitHubReleases.EffectiveChannel(null, V("0.2.2")));
        Assert.Equal(UpdateChannel.Stable, GitHubReleases.EffectiveChannel(UpdateChannel.Stable, V("0.2.2-beta.1")));
        Assert.Equal(UpdateChannel.Beta, GitHubReleases.EffectiveChannel(UpdateChannel.Beta, V("0.2.2")));
        Assert.Equal(UpdateChannel.Stable, GitHubReleases.EffectiveChannel(null, null));
    }

    private static Release Rel(string version, bool pre, bool msi = true) => new(V(version), "v" + version,
        $"https://github.com/qiyinxi/HomePodCast/releases/tag/v{version}", pre,
        msi ? new ReleaseAsset($"HomePodCast-{version}-x64.msi",
            $"https://github.com/qiyinxi/HomePodCast/releases/download/v{version}/HomePodCast-{version}-x64.msi", 100, Digest1) : null);
}

public class GitHubRequestTests
{
    private sealed class Api(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task One_get_of_the_release_list_and_nothing_else()
    {
        var api = new Api(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var http = new HttpClient(api);
        Assert.Equal("[]", await GitHubReleases.FetchAsync(http, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, api.Request!.Method);
        Assert.Equal("https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20", api.Request.RequestUri!.ToString());
        Assert.Equal("application/vnd.github+json", api.Request.Headers.Accept.ToString());
        Assert.Null(api.Request.Headers.Authorization);
        Assert.Null(api.Request.Content);
    }

    [Fact]
    public async Task Rate_limit_and_errors_become_one_log_line()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
        limited.Headers.Add("X-RateLimit-Remaining", "0");
        limited.Headers.Add("X-RateLimit-Reset", "1760184000");
        using (var http = new HttpClient(new Api(limited)))
            Assert.Contains("HTTP 403, rate limited until", (await Assert.ThrowsAsync<UpdateCheckException>(() => GitHubReleases.FetchAsync(http, CancellationToken.None))).Message);
        using (var http = new HttpClient(new Api(new HttpResponseMessage(HttpStatusCode.BadGateway))))
            Assert.Equal("HTTP 502", (await Assert.ThrowsAsync<UpdateCheckException>(() => GitHubReleases.FetchAsync(http, CancellationToken.None))).Message);
    }

    [Fact]
    public void The_user_agent_names_the_app_and_its_version_only()
    {
        using var http = GitHubReleases.CreateClient("0.2.2-beta.1", GitHubReleases.Timeout);
        Assert.Equal("HomePodCast/0.2.2-beta.1", http.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(TimeSpan.FromSeconds(15), http.Timeout);
    }
}

public class UpdateConfigTests
{
    [Fact]
    public void Automatic_checks_are_off_by_default_also_for_older_configs()
    {
        var fresh = new AppConfig();
        Assert.False(fresh.AutoUpdateCheck);
        Assert.Null(fresh.UpdateChannel);
        Assert.Null(fresh.UpdateSkipped);
        Assert.Null(fresh.UpdateLastCheck);

        var dir = Directory.CreateTempSubdirectory("hpc-config-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            File.WriteAllText(path, """{ "DeviceId": "AA:BB", "LatencyMs": 120, "Scene": "Recommended" }""");
            var old = AppConfig.Load(path);
            Assert.False(old.AutoUpdateCheck);
            Assert.Null(old.UpdateChannel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_settings_round_trip()
    {
        var dir = Directory.CreateTempSubdirectory("hpc-config-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            var checkedAt = new DateTime(2026, 10, 11, 8, 30, 0, DateTimeKind.Utc);
            new AppConfig
            {
                AutoUpdateCheck = true, UpdateChannel = UpdateChannel.Beta, UpdateSkipped = "0.2.3", UpdateLastCheck = checkedAt, UpdateNotified = "0.2.3",
            }.Save(path);
            Assert.Contains("\"UpdateChannel\": \"Beta\"", File.ReadAllText(path));
            var cfg = AppConfig.Load(path);
            Assert.True(cfg.AutoUpdateCheck);
            Assert.Equal(UpdateChannel.Beta, cfg.UpdateChannel);
            Assert.Equal("0.2.3", cfg.UpdateSkipped);
            Assert.Equal("0.2.3", cfg.UpdateNotified);
            Assert.Equal(checkedAt, cfg.UpdateLastCheck);
            Assert.Equal(DateTimeKind.Utc, cfg.UpdateLastCheck!.Value.Kind);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class UpdateScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void First_check_is_30_seconds_after_startup_when_never_checked_or_overdue()
    {
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstDue(null, Now));
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstDue(Now.AddHours(-25), Now));
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstDue(Now.AddDays(-30), Now));
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstDue(Now.AddHours(-24).AddSeconds(10), Now)); // due in 10 s: the delay wins
    }

    [Fact]
    public void A_restart_does_not_check_sooner_than_24_hours_after_the_last_check()
    {
        Assert.Equal(Now.AddHours(23), UpdateSchedule.FirstDue(Now.AddHours(-1), Now));
        Assert.Equal(Now.AddHours(24), UpdateSchedule.FirstDue(Now, Now));
    }

    [Fact]
    public void A_last_check_in_the_future_counts_as_none()
    {
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstDue(Now.AddHours(5), Now)); // the clock was set back
    }

    [Fact]
    public void The_timer_looks_at_the_clock_at_least_every_10_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateSchedule.Wait(Now.AddSeconds(30), Now));
        Assert.Equal(TimeSpan.FromMinutes(10), UpdateSchedule.Wait(Now.AddHours(23), Now));
        Assert.Equal(TimeSpan.FromSeconds(1), UpdateSchedule.Wait(Now.AddMinutes(-5), Now));
        Assert.Equal(TimeSpan.FromHours(24), UpdateSchedule.Interval);
        Assert.Equal(TimeSpan.FromHours(1), UpdateSchedule.RetryAfterFailure);
    }
}

public class UpdatePackageTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("HomePodCast MSI test payload. ", 4000)));
    private static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private const string Url = "https://github.com/qiyinxi/HomePodCast/releases/download/v9.9.9/HomePodCast-9.9.9-x64.msi";

    [Theory]
    [InlineData("sha256:475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e", "475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e")]
    [InlineData("SHA256:475C297FCC1A0C60EA97B9001DB47AB659177926AF5F0B191E548CFDCC2DF16E", "475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e")]
    [InlineData("sha512:475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e", null)]
    [InlineData("sha256:475c297f", null)]
    [InlineData("sha256:zz5c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e", null)]
    [InlineData("475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Digest_is_read_only_as_sha256_hex(string? digest, string? expected) => Assert.Equal(expected, UpdatePackage.Sha256FromDigest(digest));

    private static Release Release(string? sha256) => new(ReleaseVersion.Parse("9.9.9")!, "v9.9.9",
        "https://github.com/qiyinxi/HomePodCast/releases/tag/v9.9.9", false, new ReleaseAsset("HomePodCast-9.9.9-x64.msi", Url, Payload.Length, sha256));

    private sealed class FakeServer(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public async Task A_download_that_matches_githubs_sha256_is_kept()
    {
        var folder = UpdatePackage.CreateFolder();
        try
        {
            var server = new FakeServer(HttpStatusCode.OK, Payload);
            using var http = new HttpClient(server);
            var reports = new List<double>();
            var path = await UpdatePackage.DownloadAsync(http, Release(PayloadSha), folder, new SyncProgress(reports.Add), CancellationToken.None);
            Assert.Equal(Path.Combine(folder, "HomePodCast-9.9.9-x64.msi"), path);
            Assert.Equal(Payload, File.ReadAllBytes(path));
            Assert.True(UpdatePackage.Matches(path, PayloadSha));
            Assert.Equal(Url, Assert.Single(server.Requests).RequestUri!.ToString());
            Assert.Equal(1.0, reports[^1]);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), UpdatePackage.FolderPrefix), folder + Path.DirectorySeparatorChar);
        }
        finally
        {
            UpdatePackage.TryDelete(folder);
        }
    }

    [Fact]
    public async Task A_download_that_does_not_match_is_deleted()
    {
        var folder = UpdatePackage.CreateFolder();
        try
        {
            using var http = new HttpClient(new FakeServer(HttpStatusCode.OK, [.. Payload, 0x00]));
            await Assert.ThrowsAsync<UpdateVerificationException>(() => UpdatePackage.DownloadAsync(http, Release(PayloadSha), folder, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            UpdatePackage.TryDelete(folder);
        }
    }

    [Fact]
    public async Task Without_a_digest_nothing_is_downloaded()
    {
        var folder = UpdatePackage.CreateFolder();
        try
        {
            var server = new FakeServer(HttpStatusCode.OK, Payload);
            using var http = new HttpClient(server);
            await Assert.ThrowsAsync<UpdateCheckException>(() => UpdatePackage.DownloadAsync(http, Release(null), folder, null, CancellationToken.None));
            Assert.Empty(server.Requests);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            UpdatePackage.TryDelete(folder);
        }
    }

    [Fact]
    public async Task A_failed_or_cancelled_download_leaves_no_file()
    {
        var folder = UpdatePackage.CreateFolder();
        try
        {
            using (var http = new HttpClient(new FakeServer(HttpStatusCode.NotFound, [])))
                await Assert.ThrowsAsync<UpdateCheckException>(() => UpdatePackage.DownloadAsync(http, Release(PayloadSha), folder, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(folder));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using (var http = new HttpClient(new FakeServer(HttpStatusCode.OK, Payload)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePackage.DownloadAsync(http, Release(PayloadSha), folder, null, cts.Token));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            UpdatePackage.TryDelete(folder);
        }
    }

    [Fact]
    public void Clean_up_removes_every_update_folder_and_returns_the_newest_result()
    {
        var root = Directory.CreateTempSubdirectory("hpc-temp-").FullName;
        var keep = Path.Combine(root, "keep");
        try
        {
            var older = Directory.CreateDirectory(Path.Combine(root, UpdatePackage.FolderPrefix + "a")).FullName;
            var newer = Directory.CreateDirectory(Path.Combine(root, UpdatePackage.FolderPrefix + "b")).FullName;
            var empty = Directory.CreateDirectory(Path.Combine(root, UpdatePackage.FolderPrefix + "c")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(root, "SomethingElse")).FullName;
            File.WriteAllText(Path.Combine(older, UpdatePackage.ResultFile), "0");
            File.SetLastWriteTimeUtc(Path.Combine(older, UpdatePackage.ResultFile), DateTime.UtcNow.AddHours(-2));
            // What Windows PowerShell's Set-Content -Encoding UTF8 writes: a BOM and a line break.
            File.WriteAllText(Path.Combine(newer, UpdatePackage.ResultFile), "1603\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(Path.Combine(newer, UpdatePackage.MsiLogFile), "MSI (s) Product: HomePodCast -- Installation failed.");
            File.WriteAllBytes(Path.Combine(empty, "HomePodCast-9.9.9-x64.msi"), [1, 2, 3]);

            var result = UpdatePackage.CleanUp(root, keep);
            Assert.NotNull(result);
            Assert.Equal(1603, result.Code);
            Assert.False(result.Succeeded);
            Assert.False(result.WasCancelled);
            Assert.Equal(Path.Combine(keep, "update-msiexec.log"), result.MsiLog);
            Assert.Contains("Installation failed", File.ReadAllText(result.MsiLog!));
            Assert.False(Directory.Exists(older));
            Assert.False(Directory.Exists(newer));
            Assert.False(Directory.Exists(empty));
            Assert.True(Directory.Exists(other));
            Assert.Null(UpdatePackage.CleanUp(root, keep));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("0", 0, true, false)]
    [InlineData("3010", 3010, true, false)]
    [InlineData("1641", 1641, true, false)]
    [InlineData("1223", 1223, false, true)]   // UAC declined
    [InlineData("1602", 1602, false, true)]   // cancelled in msiexec's progress window
    [InlineData("1603", 1603, false, false)]
    [InlineData("1618", 1618, false, false)]  // another installation in progress
    [InlineData("\uFEFF0\r\n", 0, true, false)]
    [InlineData("timeout", null, false, false)]
    [InlineData("error: file not found", null, false, false)]
    public void Install_results(string text, int? code, bool succeeded, bool cancelled)
    {
        var r = InstallResult.Parse(text, null);
        Assert.Equal(code, r.Code);
        Assert.Equal(succeeded, r.Succeeded);
        Assert.Equal(cancelled, r.WasCancelled);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this one reports at once.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public class UpdateInstallerTests(ITestOutputHelper output)
{
    private static readonly string[] Paths =
    [
        @"C:\Program Files\HomePodCast\HomePodCast.exe",
        @"C:\Users\O'Brien\AppData\Local\Temp\HomePodCast-update-x1\HomePodCast-0.2.3-x64.msi",
        @"D:\it’s ‘mine’ ‚x‛\HomePodCast.exe",                    // PowerShell reads these as single quotes too
        @"D:\$env:TEMP\`$(Remove-Item x)`n\Home Pod Cast.exe",   // nothing may expand
        @"D:\音箱 — “dir”\HomePodCast.exe",
        @"D:\end with quote'\'';calc;'\HomePodCast.exe",
    ];

    private static UpdateInstaller.Helper Helper(string path, bool tray = false, string version = "0.2.2-beta.1") =>
        new(4242, path, path + ".log", path + ".result.txt", path, version, tray);

    [Fact]
    public void Exe_counts_as_installed_only_directly_in_the_install_folder()
    {
        Assert.True(UpdateInstaller.IsInFolder(@"C:\Program Files\HomePodCast\HomePodCast.exe", @"C:\Program Files\HomePodCast\"));
        Assert.True(UpdateInstaller.IsInFolder(@"C:\Program Files\HomePodCast\HomePodCast.exe", @"c:\program files\homepodcast"));
        Assert.True(UpdateInstaller.IsInFolder(@"D:\Apps\HPC\HomePodCast.exe", @" D:\Apps\HPC\ "));
        Assert.False(UpdateInstaller.IsInFolder(@"C:\Program Files\HomePodCast\sub\HomePodCast.exe", @"C:\Program Files\HomePodCast\"));
        Assert.False(UpdateInstaller.IsInFolder(@"C:\Users\me\Downloads\HomePodCast\HomePodCast.exe", @"C:\Program Files\HomePodCast\"));
        Assert.False(UpdateInstaller.IsInFolder(@"C:\Program Files\HomePodCast2\HomePodCast.exe", @"C:\Program Files\HomePodCast"));
        Assert.False(UpdateInstaller.IsInFolder(@"C:\Program Files\HomePodCast\HomePodCast.exe", ""));
        Assert.False(UpdateInstaller.IsInFolder(@"C:\Program Files\HomePodCast\HomePodCast.exe", null));
        Assert.False(UpdateInstaller.IsInFolder("", @"C:\Program Files\HomePodCast\"));
    }

    [Fact]
    public void Windows_installer_query_runs_and_a_temp_copy_is_portable()
    {
        // Read-only: lists the products with our UpgradeCode (none on a build machine).
        var locations = UpdateInstaller.InstallLocations().ToList();
        output.WriteLine($"installed: {(locations.Count == 0 ? "none" : string.Join(", ", locations))}");
        Assert.False(UpdateInstaller.IsMsiInstall(Path.Combine(Path.GetTempPath(), "HomePodCast.exe")));
        Assert.False(AppEnvironment.IsPackaged); // the test host is no MSIX package
    }

    [Fact]
    public void Helper_command_carries_every_path_as_one_single_quoted_literal()
    {
        foreach (var path in Paths)
        {
            var h = Helper(path);
            var args = UpdateInstaller.HelperArguments(h);
            Assert.StartsWith("-NoProfile -NonInteractive -EncodedCommand ", args);
            var encoded = args["-NoProfile -NonInteractive -EncodedCommand ".Length..];
            Assert.Matches("^[A-Za-z0-9+/=]+$", encoded); // nothing for a command line to interpret
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            Assert.Equal(UpdateInstaller.HelperScript(h), script);
            Assert.Contains($"$msi = {Firewall.PowerShellLiteral(path)}\n", script);
            Assert.Contains($"$exe = {Firewall.PowerShellLiteral(path)}\n", script);
            Assert.Contains($"$installDir = {Firewall.PowerShellLiteral(Path.GetDirectoryName(path)!)}\n", script);
            Assert.Contains("$appPid = 4242\n", script);
            Assert.Contains("$appArgs = @('--updated-from=0.2.2-beta.1')", script);
            // The steps only use the variables: no path appears outside the assignments.
            var steps = script[UpdateInstaller.HelperVariables(h).Length..];
            Assert.DoesNotContain(Path.GetFileName(path), steps);
        }
    }

    [Fact]
    public void Helper_runs_msiexec_elevated_into_the_same_folder_and_starts_the_app_as_the_user()
    {
        var script = UpdateInstaller.HelperScript(Helper(Paths[0], tray: true));
        Assert.Contains("$appArgs = @('--updated-from=0.2.2-beta.1', '--tray')", script);
        Assert.Contains("'msiexec.exe'", script);
        Assert.Contains("/passive /norestart /l*v", script);
        Assert.Contains("INSTALLFOLDER=\"' + $installDir + '\"'", script);
        Assert.Contains("$info.Verb = 'runas'", script);
        Assert.Contains("Start-Process -FilePath $exe -ArgumentList $appArgs", script);
        Assert.DoesNotContain("-Verb RunAs", script); // the app itself is never started elevated
        Assert.Contains("$_.ProcessName -eq 'HomePodCast'", script);
        Assert.True(script.IndexOf("Set-Content -LiteralPath $result -Value $code", StringComparison.Ordinal)
                    < script.IndexOf("Start-Process -FilePath $exe", StringComparison.Ordinal)); // the result is there before the app starts
    }

    [Fact]
    public void Only_version_characters_reach_the_command_line_and_double_quotes_are_refused()
    {
        Assert.Equal("0.2.2-beta.1", UpdateInstaller.SafeVersion("0.2.2-beta.1"));
        Assert.Equal("0.2.2calc", UpdateInstaller.SafeVersion("0.2.2'; calc"));
        Assert.Contains("'--updated-from=0.2.2calc'", UpdateInstaller.HelperVariables(Helper(Paths[0], version: "0.2.2'; calc")));
        Assert.Throws<ArgumentException>(() => UpdateInstaller.HelperScript(Helper(@"C:\a ""b""\HomePodCast.exe")));
        Assert.Equal(@"C:\Program Files\HomePodCast", Helper(Paths[0]).InstallDir);
    }

    [Fact]
    public void Updated_from_is_read_from_the_command_line()
    {
        Assert.Equal("0.2.2", UpdateInstaller.UpdatedFrom(["HomePodCast.exe", "--tray", "--updated-from=0.2.2"]));
        Assert.Null(UpdateInstaller.UpdatedFrom(["HomePodCast.exe", "--tray"]));
    }

    [Fact]
    public async Task PowerShell_parses_the_helper_and_reads_every_literal_back()
    {
        // Windows PowerShell parses each helper script (syntax only, nothing runs) and runs only its assignments,
        // handing the values back as base64 of UTF-8 so the console code page cannot change anything. One process
        // per path: all of them on one command line would be too long.
        await Task.WhenAll(Paths.Select(async path =>
        {
            var h = Helper(path);
            var script = Convert.ToBase64String(Encoding.UTF8.GetBytes(UpdateInstaller.HelperScript(h)));
            var check = new StringBuilder("$ErrorActionPreference = 'Stop'\n");
            check.Append("$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseInput(" +
                         $"[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{script}')), [ref]$null, [ref]$errors)\n");
            check.Append("[Console]::Out.WriteLine('errors:' + $errors.Count)\n");
            check.Append(UpdateInstaller.HelperVariables(h)).Append('\n');
            foreach (var v in new[] { "$msi", "$msiLog", "$installDir", "$result", "$exe" })
                check.Append($"[Console]::Out.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes({v})))\n");
            check.Append("[Console]::Out.WriteLine(($appArgs -join ' ') + '|' + $appPid)\n");

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(check.ToString())))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(p.ExitCode == 0, await stderr);
            var lines = new Queue<string>((await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            string Decode(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            Assert.Equal("errors:0", lines.Dequeue());
            Assert.Equal(h.Msi, Decode(lines.Dequeue()));
            Assert.Equal(h.MsiLog, Decode(lines.Dequeue()));
            Assert.Equal(h.InstallDir, Decode(lines.Dequeue()));
            Assert.Equal(h.Result, Decode(lines.Dequeue()));
            Assert.Equal(h.Exe, Decode(lines.Dequeue()));
            Assert.Equal("--updated-from=0.2.2-beta.1|4242", lines.Dequeue());
            Assert.Empty(lines);
        }));
    }
}
