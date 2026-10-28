using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.Tests;

/// <summary>
/// What 关于 → 软件更新 and 首页 show after a check, and when checks run (device checklist 「软件更新」): the decisions in
/// TrayApp.Update.cs on top of GitHubReleases.Pick, without a request, a timer or a window. Installing is not here.
/// </summary>
public class UpdateViewTests
{
    private static ReleaseVersion V(string s) => ReleaseVersion.Parse(s)!;

    private static Release Rel(string version, bool pre) => new(V(version), "v" + version,
        $"https://github.com/qiyinxi/HomePodCast/releases/tag/v{version}", pre,
        new ReleaseAsset($"HomePodCast-{version}-x64.msi",
            $"https://github.com/qiyinxi/HomePodCast/releases/download/v{version}/HomePodCast-{version}-x64.msi", 100,
            "475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e"));

    private static TrayApp.UpdateCheckView View(IEnumerable<Release> releases, UpdateChannel channel, string current, bool automatic,
        string? skipped = null, Release? previous = null, bool previousBanner = false, string? notified = null) =>
        TrayApp.CheckView(GitHubReleases.Pick(releases, channel, V(current), skipped, needMsi: true), automatic, previous, previousBanner, notified);

    [Fact]
    public void Automatic_checks_are_off_after_a_fresh_install_so_only_the_off_line_is_logged_and_nothing_is_requested()
    {
        var fresh = new AppConfig();
        Assert.False(fresh.AutoUpdateCheck);
        // A release build, not the Store version: nothing is scheduled, so no timer ever calls the automatic check.
        Assert.Equal("automatic checks are off (only 「检查更新」 contacts GitHub)",
            TrayApp.NoAutomaticCheck(store: false, fresh.AutoUpdateCheck, ReleaseVersion.WhyNotAutomatic("0.2.2", null, debugBuild: false)));
        Assert.Equal("Microsoft Store package: never contacts GitHub (the Store updates it)", TrayApp.NoAutomaticCheck(true, true, null));
        Assert.Equal("no automatic checks in this build (debug build)", TrayApp.NoAutomaticCheck(false, true, "debug build"));
        Assert.Null(TrayApp.NoAutomaticCheck(false, true, ReleaseVersion.WhyNotAutomatic("0.2.3-beta.1", null, debugBuild: false)));
    }

    [Fact]
    public void A_manual_check_answers_in_settings_up_to_date_or_with_the_new_version_and_no_banner()
    {
        Release[] releases = [Rel("0.2.2", false), Rel("0.2.3", false)];
        var upToDate = View(releases, UpdateChannel.Stable, "0.2.3", automatic: false);
        Assert.Equal(("已是最新版本", TextRole.Success), (upToDate.Status, upToDate.Role));
        Assert.Null(upToDate.Update);                                 // no 更新 button
        Assert.False(upToDate.Banner);

        var found = View(releases, UpdateChannel.Stable, "0.2.2", automatic: false);
        Assert.Equal(("发现新版本 0.2.3", TextRole.Accent), (found.Status, found.Role));
        Assert.Equal("0.2.3", found.Update!.Version.ToString());       // 更新 in 设置
        Assert.False(found.Banner);                                   // a manual check answers in 设置 only
        Assert.False(found.Notify);                                   // and never with a balloon
    }

    [Fact]
    public void A_beta_switched_to_the_stable_channel_is_told_it_is_ahead_and_gets_no_update_button()
    {
        Release[] releases = [Rel("0.2.1", false), Rel("0.2.2-beta.1", true), Rel("0.2.2-beta.2", true)];
        Assert.Equal(UpdateChannel.Beta, GitHubReleases.EffectiveChannel(null, V("0.2.2-beta.1"))); // the default on a beta
        Assert.Equal(UpdateChannel.Stable, GitHubReleases.EffectiveChannel(null, V("0.2.1")));      // and on a stable build
        foreach (bool automatic in new[] { false, true })
        {
            var view = View(releases, UpdateChannel.Stable, "0.2.2-beta.1", automatic);
            Assert.Equal("当前测试版比最新正式版新，下一个正式版发布后会提示更新", view.Status);
            Assert.Null(view.Update);                                 // never a downgrade to 0.2.1
            Assert.False(view.Banner);
            Assert.False(view.Notify);
        }
        // An update offered before the switch is gone too.
        var before = View(releases, UpdateChannel.Beta, "0.2.2-beta.1", automatic: true);
        Assert.Equal("0.2.2-beta.2", before.Update!.Version.ToString());
        Assert.Null(View(releases, UpdateChannel.Stable, "0.2.2-beta.1", false, previous: before.Update, previousBanner: true).Update);
    }

    [Fact]
    public void An_automatic_find_shows_the_banner_and_the_balloon_only_once_per_version()
    {
        Release[] releases = [Rel("0.2.2", false), Rel("0.2.3", false)];
        var first = View(releases, UpdateChannel.Stable, "0.2.2", automatic: true);
        Assert.True(first.Banner);
        Assert.True(first.Notify);

        // The next day's check (UpdateNotified now says 0.2.3): the banner stays, no second balloon.
        var again = View(releases, UpdateChannel.Stable, "0.2.2", automatic: true, previous: first.Update, previousBanner: true, notified: "0.2.3");
        Assert.True(again.Banner);
        Assert.False(again.Notify);
        // A manual check of the same version keeps the banner it had.
        Assert.True(View(releases, UpdateChannel.Stable, "0.2.2", false, previous: first.Update, previousBanner: true, notified: "0.2.3").Banner);

        // A newer version: announced again.
        Release[] newer = [.. releases, Rel("0.2.4", false)];
        var next = View(newer, UpdateChannel.Stable, "0.2.2", automatic: true, previous: first.Update, previousBanner: true, notified: "0.2.3");
        Assert.Equal("0.2.4", next.Update!.Version.ToString());
        Assert.True(next.Notify);
        // A manual check finding a newer version than the banner showed: answered in 设置, the old banner is not kept.
        Assert.False(View(newer, UpdateChannel.Stable, "0.2.2", false, previous: first.Update, previousBanner: true).Banner);
    }

    [Fact]
    public void A_skipped_version_is_not_offered_again_but_a_newer_one_is()
    {
        Release[] releases = [Rel("0.2.2", false), Rel("0.2.3", false)];
        // 跳过此版本 saved UpdateSkipped = 0.2.3.
        var skipped = View(releases, UpdateChannel.Stable, "0.2.2", automatic: true, skipped: "0.2.3", notified: "0.2.3");
        Assert.Equal("已跳过 0.2.3，有更新的版本时会再提示", skipped.Status);
        Assert.Null(skipped.Update);
        Assert.False(skipped.Banner);
        Assert.False(skipped.Notify);

        var newer = View([.. releases, Rel("0.2.4", false)], UpdateChannel.Stable, "0.2.2", automatic: true, skipped: "0.2.3", notified: "0.2.3");
        Assert.Equal("0.2.4", newer.Update!.Version.ToString());
        Assert.True(newer.Banner && newer.Notify);
    }

}
