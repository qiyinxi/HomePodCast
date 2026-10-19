using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class DiagnosticsPageTests
{
    [Fact]
    public void Diagnostic_actions_run_only_after_their_own_explicit_click() => Sta.Run(() =>
    {
        List<string> calls = [];
        IWin32Window? syncOwner = null, exportOwner = null;
        using var page = new DiagnosticsPage(owner => { syncOwner = owner; calls.Add("sync"); },
            () => calls.Add("logs"), owner => { exportOwner = owner; calls.Add("export"); });
        using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000),
            Opacity = 0, ClientSize = new Size(740, 900) };
        page.Dock = DockStyle.Fill;
        host.Controls.Add(page);
        host.Show();
        Application.DoEvents();
        page.PageShown();
        Assert.Equal("诊断", page.TitleText!.Text);
        string[] sections = ["播放统计", "音画同步测试", "日志", "诊断报告"];
        Assert.Equal(sections, All<TextBlock>(page).Select(text => text.Text).Where(sections.Contains));
        var buttons = All<FluentButton>(page).ToArray();
        Assert.Equal(new[] { "开始", "打开日志文件夹", "导出诊断" }, buttons.Select(button => button.Text));
        Assert.Empty(calls);
        buttons[0].PerformClick();
        Assert.Equal(new[] { "sync" }, calls);
        Assert.Same(host, syncOwner);
        buttons[1].PerformClick();
        Assert.Equal(new[] { "sync", "logs" }, calls);
        buttons[2].PerformClick();
        Assert.Equal(new[] { "sync", "logs", "export" }, calls);
        Assert.Same(host, exportOwner);
        page.PageHidden();
        page.PageShown();
        Assert.Equal(3, calls.Count);
        Assert.Equal(DialogResult.None, host.DialogResult);
    });

    [Fact]
    public void Playback_counters_stay_separate_and_unwatched_networks_are_not_reported_as_zero() => Sta.Run(() =>
    {
        var session = new object();
        var counters = new PlaybackCounters(session, 10, 0);
        using var page = new DiagnosticsPage(_ => { }, () => { }, _ => { }, () => counters);
        page.UpdateState();
        counters = counters with { BufferUnderruns = 13, NetworkFluctuations = 5 };
        page.UpdateState();
        var tiles = All<MetricTile>(page).ToDictionary(tile => tile.Caption);
        Assert.Equal("3", tiles["缓冲欠载"].ValueText);
        Assert.Equal("5", tiles["网络波动"].ValueText);
        Assert.Contains(All<TextBlock>(page), text => text.Text == "本次连接的缓冲和网络统计，不代表实际听到的中断。");
        counters = counters with { NetworkFluctuations = null };
        page.UpdateState();
        Assert.Equal("3", tiles["缓冲欠载"].ValueText);
        Assert.Equal("—", tiles["网络波动"].ValueText);
        counters = counters with { Session = null };
        page.UpdateState();
        Assert.All(tiles.Values, tile => Assert.Equal("—", tile.ValueText));
    });

    [Fact]
    public void Visiting_diagnostics_keeps_the_connection_count_and_reconnecting_starts_a_new_count() => Sta.Run(() =>
    {
        PlaybackCounters counters = default;
        using var page = new DiagnosticsPage(_ => { }, () => { }, _ => { }, () => counters);
        var tile = All<MetricTile>(page).Single(tile => tile.Caption == "缓冲欠载");
        // As MainWindow does, observe the connection change while another page is selected.
        counters = new(new object(), 10, 0);
        page.UpdateState();
        counters = counters with { BufferUnderruns = 14 };
        page.PageShown();
        Assert.Equal("4", tile.ValueText);
        page.PageHidden();
        Assert.False(TestApp.Field<System.Windows.Forms.Timer>(page, "_statsTimer").Enabled);
        counters = counters with { BufferUnderruns = 16 };
        page.PageShown();
        Assert.Equal("6", tile.ValueText);
        page.PageHidden();
        counters = counters with { Session = null };
        page.UpdateState();
        counters = new(new object(), 20, null);
        page.UpdateState();
        counters = counters with { BufferUnderruns = 22 };
        page.PageShown();
        Assert.Equal("2", tile.ValueText);
    });

    [Fact]
    public void Home_keeps_live_metrics_without_claiming_an_audible_dropout_count() => Sta.Run(() =>
    {
        var app = TestApp.Create(TestApp.Config());
        using var fx = app.Fx;
        using var window = new MainWindow(app);
        var home = TestApp.Field<HomePage>(window, "_home");
        var tiles = home.Content.Controls.OfType<TileGrid>().Single().Controls.OfType<MetricTile>();
        Assert.Equal(new[] { "实际延迟", "缓冲", "音箱处理", "重传" }, tiles.Select(tile => tile.Caption));
        Assert.DoesNotContain("断音", TestApp.Field<TextBlock>(home, "_streamStats").Text);
    });

    [Fact]
    public void Diagnostics_reuses_the_sync_navigation_slot_and_about_keeps_application_information() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        var app = TestApp.Create(config);
        using var fx = app.Fx;
        using var window = new MainWindow(app);
        string before = JsonSerializer.Serialize(config); // MainWindow may normalize existing group preferences on construction.
        var rail = TestApp.Field<NavRail>(window, "_rail");
        Assert.Equal(7, (int)AppPage.Sync);
        Assert.Equal("诊断", rail.Items[(int)AppPage.Sync].Text);
        Assert.False(rail.Items[(int)AppPage.Sync].Collapsed);
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        var diagnostics = Assert.IsType<DiagnosticsPage>(pages[(int)AppPage.Sync]);
        window.ShowPage(AppPage.Sync);
        diagnostics.PageShown();
        diagnostics.PageHidden();
        var about = TestApp.Field<AboutPage>(window, "_about");
        var aboutText = All<TextBlock>(about).Select(text => text.Text).ToArray();
        Assert.Contains("软件更新", aboutText);
        Assert.Contains("许可证", aboutText);
        Assert.Contains(aboutText, text => text.StartsWith("HomePodCast "));
        Assert.DoesNotContain("日志", aboutText);
        Assert.DoesNotContain("诊断报告", aboutText);
        Assert.DoesNotContain(All<FluentButton>(about), button => button.Text is "打开日志文件夹" or "导出诊断" or "开始");
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.Equal(StreamState.Idle, app.Controller.State);
        Assert.False(fx.MicOn);
    });

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(control => (control is T item ? new[] { item } : []).Concat(All<T>(control)));
}
