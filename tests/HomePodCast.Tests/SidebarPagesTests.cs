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
        Assert.All(All<FluentComboBox>(page), combo => Assert.False(string.IsNullOrWhiteSpace(combo.AccessibleName)));
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
        var list = TestApp.Field<StackPanel>(page, "_list");
        Assert.All(list.Controls.OfType<NavItem>(), item => Assert.Equal(AccessibleRole.ListItem, item.AccessibilityObject.Role));
        Assert.Single(list.Controls.OfType<NavItem>(), item => item.Selected);
        Assert.Equal(TestApp.DeviceId, config.DeviceId);
        Assert.Equal("other", TestApp.Field<string>(page, "_selected"));
        page.PageHidden();
    });

    [Fact]
    public void Speaker_info_names_each_kind_of_receiver_and_its_processing_time_once() => Sta.Run(() =>
    {
        // 2026-10-15: above the card, a note of 连接方式 repeated the card's 设备类型 (「Apple TV（实验性）」 for an Apple TV
        // not connected, then 「未连接；连接后显示实际协议。」) and the processing time under the card.
        var config = TestApp.Config();
        var app = TestApp.Create(config); // the scan: the HomePod 卧室
        typeof(TrayApp).GetField("<SpeakerInfo>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(app, new SpeakerInfoCache((_, _) => throw new IOException("connection refused"))); // described by the scan
        var tv = new AirPlayDevice("书房电视", "AA:00:00:00:00:02", System.Net.IPAddress.Parse("192.0.2.12"), 7000, "AppleTV14,1",
            new Dictionary<string, string> { ["model"] = "AppleTV14,1" });
        var airPlay2 = new AirPlayDevice("客厅音箱", "AA:00:00:00:00:03", System.Net.IPAddress.Parse("192.0.2.13"), 7000, "Speaker1,1",
            new Dictionary<string, string> { ["srcvers"] = "377.40.00" });
        var airPlay1 = new AirPlayDevice("投影仪", "AA:00:00:00:00:04", System.Net.IPAddress.Parse("192.0.2.14"), 7001, "ShairportSync",
            new Dictionary<string, string> { ["am"] = "ShairportSync", ["srcvers"] = "105.1" }) { HasAirPlay = false, RaopPort = 7001 };
        ((List<AirPlayDevice>)app.LastScan).AddRange([tv, airPlay2, airPlay1]);
        // Selected, offline: described by its saved preferences, with the processing time of its last connection.
        var offline = new AirPlayDevice("会议室", "AA:00:00:00:00:05", System.Net.IPAddress.Parse("192.0.2.15"), 7000, "Projector1,1",
            new Dictionary<string, string>());
        var saved = ReceiverPreferences.Of(offline);
        saved.ArrivalToRenderMs = 137;
        config.ReceiverSettings[RaopPassword.Key(offline.DeviceId)] = saved;
        (config.DeviceId, config.DeviceName, config.Host) = (offline.DeviceId, offline.Name, "192.0.2.15");
        using var page = new SpeakersPage(app);
        using var host = Host(page);
        page.PageShown();
        var connection = TestApp.Field<ReceiverConnectionPanel>(page, "_receiverConnection");

        foreach (var (id, kind) in new[]
                 {
                     (offline.DeviceId, ReceiverKind.Unknown), (TestApp.DeviceId, ReceiverKind.HomePod), (tv.DeviceId, ReceiverKind.AppleTv),
                     (airPlay2.DeviceId, ReceiverKind.ThirdPartyAirPlay2), (airPlay1.DeviceId, ReceiverKind.ThirdPartyAirPlay1),
                 })
        {
            page.Inspect(id);
            Assert.Equal(id, TestApp.Field<string>(page, "_selected"));
            var texts = Shown(page).ToList();
            if (kind == ReceiverKind.HomePod) Assert.Single(texts, t => t == "HomePod");
            else Assert.Single(texts, t => t.Contains(ReceiverKinds.Label(kind)));
            Assert.DoesNotContain(texts, t => t.Contains("连接后显示实际协议"));
            // 连接方式 only where there is a choice; the card says the rest.
            Assert.Equal(kind is ReceiverKind.HomePod or ReceiverKind.AppleTv, connection.Collapsed);
            Assert.Equal(id == offline.DeviceId ? 1 : 0, texts.Count(t => t.Contains("137 ms")));
        }
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
            Assert.Equal(rail.Items.Count, rail.Items.Select(item => item.Glyph).Distinct().Count());
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

    /// <summary>Every non-empty text of the text blocks not collapsed (with all that holds them).</summary>
    private static IEnumerable<string> Shown(Control root) => root.Controls.Cast<Control>().Where(c => !LayoutPanel.IsCollapsed(c))
        .SelectMany(c => (c is TextBlock { Text.Length: > 0 } t ? [t.Text] : Array.Empty<string>()).Concat(Shown(c)));
}
