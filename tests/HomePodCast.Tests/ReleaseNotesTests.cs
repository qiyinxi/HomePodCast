using System.Text.Json;
using HomePodCast.UI;
using HomePodCast.Update;

namespace HomePodCast.Tests;

public class ReleaseNotesTests
{
    private static ReleaseVersion V(string version) => ReleaseVersion.Parse(version)!;
    private static Release R(string version, string body = "notes") => new(V(version), "v" + version,
        $"{GitHubReleases.ReleasesPage}/tag/v{version}", V(version).IsPrerelease, null, body);

    [Theory]
    [InlineData("zh-CN", "### 新功能\n\n- 中文")]
    [InlineData("zh-TW", "### 新功能\n\n- 中文")]
    [InlineData("en", "## New features\n\n- English")]
    [InlineData("ja", "## New features\n\n- English")]
    [InlineData("de", "## New features\n\n- English")]
    public void Only_the_requested_language_is_shown(string language, string expected) =>
        Assert.Equal(expected, ReleaseNotes.ForLanguage("## New features\r\n\r\n- English\r\n\r\n## 简体中文\r\n\r\n### 新功能\r\n\r\n- 中文", language));

    [Theory]
    [InlineData("English\n---\n中文")]
    [InlineData("Only English")]
    [InlineData("只有中文")]
    [InlineData("English\n## 简体中文\n")]
    [InlineData("## 简体中文\n中文")]
    [InlineData("English\n## 简体中文\n中文\n## 简体中文\n重复")]
    [InlineData("English\n```markdown\n## 简体中文\n中文\n```")]
    public void Old_or_ambiguous_format_falls_back_to_the_entire_body(string body)
    {
        Assert.Equal(body, ReleaseNotes.ForLanguage(body, "en"));
        Assert.Equal(body, ReleaseNotes.ForLanguage(body, "zh-TW"));
    }

    [Fact]
    public void Betas_are_accumulated_in_semver_order_not_publication_order()
    {
        Release[] feed = [R("1.2.0-beta.8"), R("1.2.0-beta.6"), R("1.2.0-beta.10"), R("1.2.0-beta.9"), R("1.2.0-beta.7"), R("1.2.0-beta.5")];
        var notes = ReleaseNotes.Collect(feed, V("1.2.0-beta.5"), feed[3], UpdateChannel.Beta);
        Assert.Equal(["1.2.0-beta.9", "1.2.0-beta.8", "1.2.0-beta.7", "1.2.0-beta.6"], notes.Releases.Select(r => r.Version.ToString()));
        Assert.False(notes.EarlierChanges);
    }

    [Fact]
    public void Stable_summaries_replace_their_betas_but_not_the_next_versions_betas()
    {
        Release[] feed = [R("1.2.0-beta.1"), R("1.1.0-beta.9"), R("1.1.0"), R("1.0.0"), R("0.2.2"), R("1.0.0-beta.6")];
        Assert.Equal(["1.1.0"], ReleaseNotes.Collect(feed, V("1.0.0"), feed[2], UpdateChannel.Stable).Releases.Select(r => r.Version.ToString()));
        var notes = ReleaseNotes.Collect(feed, V("0.2.2"), feed[0], UpdateChannel.Beta);
        Assert.Equal(["1.2.0-beta.1", "1.1.0", "1.0.0"], notes.Releases.Select(r => r.Version.ToString()));
        Assert.False(notes.EarlierChanges);
        Assert.Equal(["1.1.0"], ReleaseNotes.Collect(feed, V("1.1.0-beta.9"), feed[2], UpdateChannel.Beta).Releases.Select(r => r.Version.ToString()));
    }

    [Fact]
    public void A_truncated_list_says_that_earlier_changes_are_on_the_release_page()
    {
        var feed = Enumerable.Range(6, 20).Select(n => R($"1.2.0-beta.{n}")).ToArray();
        var notes = ReleaseNotes.Collect(feed, V("1.2.0-beta.5"), feed[^1], UpdateChannel.Beta);
        Assert.Equal(20, notes.Releases.Count); Assert.True(notes.EarlierChanges);
    }

    [Theory]
    [InlineData("https://github.com/qiyinxi/HomePodCast", true)]
    [InlineData("https://github.com/qiyinxi/HomePodCast/releases/tag/v1.0.0#notes", true)]
    [InlineData("https://github.com/qiyinxi/HomePodCast/blob/dev/README.md", true)]
    [InlineData("http://github.com/qiyinxi/HomePodCast", false)]
    [InlineData("https://github.com/qiyinxi/HomePodCast.evil/releases", false)]
    [InlineData("https://github.com.evil/qiyinxi/HomePodCast", false)]
    [InlineData("https://github.com@evil.test/qiyinxi/HomePodCast", false)]
    [InlineData("https://evil@github.com/qiyinxi/HomePodCast", false)]
    [InlineData("https://github.com:8443/qiyinxi/HomePodCast", false)]
    [InlineData("https://github.com/qiyinxi/HomePodCast/../other", false)]
    [InlineData("https://github.com/qiyinxi/HomePodCast/%2f..%2fother", false)]
    [InlineData("https://github.com/qiyinxi/HomePodCast/\n", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    public void Only_this_repositorys_https_links_can_open(string url, bool allowed) => Assert.Equal(allowed, ReleaseNotes.CanOpen(url));

    [Fact]
    public void Markdown_renders_headings_lists_bold_links_and_inert_html()
    {
        var blocks = ReleaseNotes.Parse("## New\n\nA **bold** paragraph with [guide](https://github.com/qiyinxi/HomePodCast/blob/main/README.md).\n\n- [external](https://example.com)\n  - Nested\n\n### Fixes\n\n<script>alert(1)</script>");
        Assert.Equal(2, blocks[0].Heading);
        Assert.Equal("bold", Assert.Single(blocks[1].Runs, r => r.Bold).Text);
        Assert.Equal("guide", Assert.Single(blocks[1].Runs, r => r.Url != null).Text);
        Assert.Equal(0, blocks[2].ListDepth); Assert.Null(Assert.Single(blocks[2].Runs).Url);
        Assert.Equal("external", blocks[2].Runs[0].Text);
        Assert.Equal(1, blocks[3].ListDepth); Assert.Equal(3, blocks[4].Heading);
        Assert.Equal("<script>alert(1)</script>", Assert.Single(blocks[5].Runs).Text);
    }

    [Fact]
    public void Only_a_manual_new_version_opens_the_window()
    {
        foreach (var outcome in Enum.GetValues<CheckOutcome>())
            foreach (bool automatic in new[] { false, true })
                foreach (bool store in new[] { false, true })
                    Assert.Equal(outcome == CheckOutcome.UpdateAvailable && !automatic && !store,
                        TrayApp.ShouldShowNotes(outcome, automatic, store));
    }

    [Fact]
    public void Gui_test_feed_is_only_available_in_ci_and_stays_on_loopback()
    {
        Assert.Same(ReleaseFeed.GitHub, ReleaseFeed.ForGuiSmoke([], false));
        Assert.Throws<ArgumentException>(() => ReleaseFeed.ForGuiSmoke(["--gui-update-feed=http://127.0.0.1/list"], false));
        Assert.Throws<ArgumentException>(() => ReleaseFeed.ForGuiSmoke(["--gui-update-feed=https://evil.test/list"], true));
        var feed = ReleaseFeed.ForGuiSmoke(["--gui-update-feed=http://127.0.0.1:8123/list"], true);
        Assert.False(feed.FollowRedirects); Assert.Equal("http://127.0.0.1:8123/list", feed.ApiUrl);
    }

    [Fact]
    public async Task The_existing_release_request_supplies_bodies_dates_and_all_intermediate_notes()
    {
        await using var fake = new UpdateSelfTestRunTests.FakeFeed();
        var json = JsonSerializer.Serialize(new[] { "1.2.0-beta.7", "1.2.0-beta.6", "1.2.0-beta.5" }.Select(version => new
        {
            tag_name = "v" + version, published_at = "2026-10-15T10:30:00Z", body = $"English {version}\n## 简体中文\n中文 {version}",
            html_url = GitHubReleases.ReleasesPage + "/tag/v" + version,
        }));
        fake.Routes["/releases"] = (200, System.Text.Encoding.UTF8.GetBytes(json), null);
        var feed = ReleaseFeed.Loopback(fake.Base + "/releases");
        var list = await UpdateFlow.FetchReleasesAsync(feed, CancellationToken.None);
        var result = GitHubReleases.Pick(list, UpdateChannel.Beta, V("1.2.0-beta.5"), null, false);
        var notes = ReleaseNotes.Collect(list, V("1.2.0-beta.5"), result.Release!, UpdateChannel.Beta);
        Assert.Equal(["1.2.0-beta.7", "1.2.0-beta.6"], notes.Releases.Select(r => r.Version.ToString()));
        Assert.Equal("中文 1.2.0-beta.7", ReleaseNotes.ForLanguage(notes.Releases[0].Body, "zh-CN"));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero), notes.Releases[0].PublishedAt);
        Assert.Equal(["/releases"], fake.Requests);
    }
}
