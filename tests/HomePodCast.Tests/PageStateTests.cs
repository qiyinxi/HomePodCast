using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using HomePodCast.Protocol;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;
using HomePodCast.Update;

namespace HomePodCast.Tests;

/// <summary>Pages keep up with what changes while they are shown (a theme switch, a saved pairing) and show the release.</summary>
[Collection("Layout work")]
public class PageStateTests
{
    [Fact]
    public void About_names_the_release_version_like_the_update_row() => Sta.Run(() =>
    {
        // The assembly version drops a beta's prerelease part (1.3.0 next to 「当前版本 1.3.0-beta.2」).
        using var page = new AboutPage(TestApp.Create(TestApp.Config()));
        var titles = All<SettingRow>(page).Select(r => r.TitleText.Text).ToList();
        Assert.Contains($"HomePodCast {ReleaseVersion.CurrentText}", titles);
        Assert.Contains(L.F("当前版本 {0}", ReleaseVersion.CurrentText), titles);
    });

    [Fact]
    public void Speaker_list_beside_the_details_follows_a_theme_switch_without_a_layout() => Sta.Run(() =>
    {
        // A Windows light/dark switch (Auto) only raises Theme.Changed; the list's panel took its colour in Arrange only.
        string previous = Theme.Mode;
        try
        {
            Theme.Mode = Theme.Light;
            using var page = new SpeakersPage(TestApp.Create(TestApp.Config()));
            using var host = Host(page);
            page.PageShown();
            var browser = page.Content.Controls.Cast<Control>().Single(c => c.GetType().Name == "Browser");
            var scroll = TestApp.Field<Panel>(browser, "_scroll");
            Assert.Equal(Theme.P.Window, scroll.BackColor);
            int arranged = 0;
            LayoutPanel.Arranged = _ => arranged++;
            try { Theme.Mode = Theme.Dark; }
            finally { LayoutPanel.Arranged = null; }
            Assert.Equal(0, arranged);
            Assert.Equal(Theme.P.Window, scroll.BackColor);
            page.PageHidden();
        }
        finally { Theme.Mode = previous; }
    });

    [Fact]
    public void A_pairing_saved_while_the_speaker_page_is_shown_appears_at_once() => Sta.Run(() =>
    {
        // With no pairings the panel is empty and painted by its parent, so it has no window to marshal through.
        var app = TestApp.Create(TestApp.Config());
        try
        {
            using var page = new SpeakersPage(app);
            using var host = Host(page);
            page.PageShown();
            var pairings = TestApp.Field<PairingsPanel>(page, "_pairings");
            Assert.True(pairings.Collapsed);
            // Saved on another thread, as the pairing dialog's session does.
            Task.Run(() => app.Pairings.Save("11:22:33:44:55:66", "客厅 Apple TV",
                new HapPairing("x", RandomNumberGenerator.GetBytes(32), [1], RandomNumberGenerator.GetBytes(32)))).Wait();
            for (int i = 0; i < 20 && pairings.Collapsed; i++) { Application.DoEvents(); Thread.Sleep(10); }
            Assert.False(pairings.Collapsed);
            Assert.Contains(All<SettingRow>(pairings), r => r.TitleText.Text == "客厅 Apple TV");
            page.PageHidden();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(app.Pairings.FilePath)!, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    });

    private static Form Host(ScrollPage page)
    {
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Opacity = 0,
            ClientSize = new Size(740, 600) };
        page.Dock = DockStyle.Fill; form.Controls.Add(page); form.Show(); Application.DoEvents();
        return form;
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(c => (c is T t ? new[] { t } : []).Concat(All<T>(c)));
}
