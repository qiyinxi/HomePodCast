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
        string[] sections = ["音画同步测试", "日志", "诊断报告"];
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
