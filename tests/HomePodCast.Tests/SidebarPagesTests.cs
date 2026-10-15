using System.Drawing;
using System.Windows.Forms;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class SidebarPagesTests
{
    [Fact]
    public void Profile_page_saves_and_applies_without_closing_the_host() => Sta.Run(() =>
    {
        var config = new AppConfig();
        string? applied = null;
        using var page = new ProfilesPage(config, (_, name) => config.UsageProfiles.Add(new UsageProfile { Id = "saved", Name = name }),
            (id, _) => { applied = id; return true; }, _ => { });
        using var host = Host(page);
        All<TextBox>(page).Single().Text = "夜间电影";
        All<FluentButton>(page).Single(b => b.Text == L.T("保存当前设置")).PerformClick();
        All<FluentButton>(page).Single(b => b.Text == L.T("应用")).PerformClick();
        Assert.Equal("saved", applied);
        Assert.Equal(DialogResult.None, host.DialogResult);
        Assert.True(host.Visible);
    });

    [Fact]
    public void Inspecting_another_speaker_does_not_change_the_playback_target() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        var app = TestApp.Create(config);
        ((List<AirPlayDevice>)app.LastScan).Add(new AirPlayDevice("书房", "other", System.Net.IPAddress.Parse("192.0.2.11"),
            7000, "AudioAccessory6,1", new Dictionary<string, string>()));
        using var page = new SpeakersPage(app);
        using var host = Host(page);
        page.PageShown();
        page.Inspect("other");
        Assert.Equal(TestApp.DeviceId, config.DeviceId);
        Assert.Equal("other", TestApp.Field<string>(page, "_selected"));
        page.PageHidden();
    });

    [Fact]
    public void Editing_an_inactive_receiver_protocol_does_not_reconnect_the_active_speaker() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        var app = TestApp.Create(config);
        var other = new AirPlayDevice("Other receiver", "11:22:33:44:55:66", System.Net.IPAddress.Parse("192.0.2.11"),
            7000, "OtherSpeaker1,1", new Dictionary<string, string>());
        app.Controller.ReceiverLookup = id => id == other.DeviceId ? other : null;
        using var window = new MainWindow(app);
        typeof(TrayApp).GetField("_form", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, window);
        typeof(TrayApp).GetField("_wantConnected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, true);
        app.SetReceiverProtocol(other.DeviceId, ReceiverProtocol.Raop);
        Assert.Equal(ReceiverProtocol.Raop, config.ReceiverSettings[RaopPassword.Key(other.DeviceId)].ProtocolOverride);
        Assert.Equal(TestApp.DeviceId, config.DeviceId);
        Assert.Equal(StreamState.Idle, app.Controller.State);
    });

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Compact_navigation_keeps_the_footer_accessible_and_all_pages_reachable(string mode) => Sta.Run(() =>
    {
        string previous = Theme.Mode;
        try
        {
            Theme.Mode = mode;
            using var window = new MainWindow(TestApp.Create(TestApp.Config()));
            window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-20000, -20000); window.Opacity = 0;
            window.ClientSize = new Size(600, 430); window.Show(); Application.DoEvents();
            var rail = TestApp.Field<NavRail>(window, "_rail");
            Assert.NotEqual(window.SurfaceColor, rail.SurfaceColor);
            var about = rail.Items.Last();
            Assert.True(rail.ClientRectangle.Contains(about.Bounds));
            var action = TestApp.Field<NavItem>(rail, "_themeButton");
            Assert.Equal(AccessibleRole.PushButton, action.AccessibilityObject.Role);
            Assert.False(string.IsNullOrWhiteSpace(action.AccessibleName));
            foreach (var (item, i) in rail.Items.Select((item, i) => (item, i)))
            {
                item.AccessibilityObject.DoDefaultAction(); Application.DoEvents();
                Assert.Equal((AppPage)i, window.CurrentPage);
                Assert.NotNull(item.AccessibleName);
                Assert.Equal(1, rail.Items.Count(n => n.Selected));
                if (i < (int)AppPage.Settings)
                {
                    var viewport = TestApp.Field<Panel>(rail, "_viewport");
                    Assert.True(viewport.RectangleToScreen(viewport.ClientRectangle).Contains(item.RectangleToScreen(item.ClientRectangle)));
                }
            }
            window.Hide();
        }
        finally { Theme.Mode = previous; }
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
