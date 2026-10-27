using System.Windows.Forms;
using HomePodCast.Players;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// Texts and balloons that send the user to a page name (or open) the page that holds what they mean. The sections moved
/// when 设置 was split into pages (2026-10-15), and these kept pointing at 设置.
/// </summary>
[Collection("Layout work")]
public class PageLinksTests
{
    [Fact]
    public void Install_balloons_open_the_page_with_the_update_section() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        var page = TestApp.Field<ScrollPage[]>(window, "_pages")[(int)TrayApp.UpdatePage];
        Assert.Contains(All<FluentButton>(page), b => b.Text == L.T("检查更新"));
        Assert.Contains(All<TextBlock>(page), t => t.Text == L.T("软件更新"));
        Assert.Equal($"更新没有完成，详情见「{page.TitleText!.Text}」→「软件更新」。", TrayApp.NotInstalledBalloon);
    });

    [Fact]
    public void Player_hints_name_the_page_with_the_player_setup() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        foreach (var status in new[]
                 {
                     new PlayerStatus(PlayerKind.Mpv, "mpv", PlayerState.NoInterface),
                     new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.NoInterface),
                     new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.NoPassword),
                     new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.LoginFailed),
                 })
        {
            string line = PlayerText.Line(status);
            var named = Assert.Single(pages, p => p.TitleText is { } title && line.Contains($"「{title.Text}」"));
            Assert.IsType<ViewingPage>(named); // the mpv line, the VLC steps and the VLC password box
            Assert.Single(All<PasswordBox>(named));
        }
        // Under the password box itself: no page to name.
        var refused = PlayerText.VlcPasswordStatus([new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.LoginFailed)], manualSaved: true);
        Assert.Equal(("密码错误，请在这里重新输入", true), refused);
    });

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(c => (c is T t ? new[] { t } : []).Concat(All<T>(c)));
}
