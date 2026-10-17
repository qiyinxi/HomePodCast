using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class AvSyncPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Player_controls_remain_available_when_the_optional_module_is_not_offered(bool moduleOffered) => Sta.Run(() =>
    {
        var config = TestApp.Config();
        config.MoviePlayerSync = false;
        config.VideoWindowModuleDirectory = "module-not-started";
        var app = TestApp.Create(config);
        using var fx = app.Fx;
        string before = JsonSerializer.Serialize(config);
        using var page = new ViewingPage(app, moduleOffered);
        page.PageShown();
        Assert.Equal("音画同步", page.TitleText!.Text);
        Assert.Equal(new[] { "影视场景自动调整播放器", "mpv", "VLC", "VLC 网页接口密码（自动读取失败时填写）", "PotPlayer · MPC-HC · MPC-BE" },
            All<SettingRow>(page).Select(row => row.TitleText.Text));
        Assert.Contains(All<TextBlock>(page), text => text.Text == "本地播放器");
        Assert.Equal(moduleOffered, All<TextBlock>(page).Any(text => text.Text == "同步观影窗口（实验性）"));
        Assert.Equal(moduleOffered, All<FluentButton>(page).Any(button => button.Text == "选择模块…"));
        Assert.Equal(moduleOffered, All<FluentButton>(page).Any(button => button.Text == "打开"));
        Assert.False(TestApp.Field<ToggleSwitch>(page, "_playerSync").Checked);
        page.PageHidden();
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.Equal(StreamState.Idle, app.Controller.State);
        Assert.False(fx.MicOn);
    });

    [Fact]
    public void Navigation_names_the_combined_page_and_settings_contains_only_preferences() => Sta.Run(() =>
    {
        var app = TestApp.Create(TestApp.Config());
        using var fx = app.Fx;
        using var window = new MainWindow(app);
        var rail = TestApp.Field<NavRail>(window, "_rail");
        var item = rail.Items[(int)AppPage.Viewing];
        Assert.Equal("音画同步", item.Text);
        Assert.False(item.Collapsed);
        var settings = TestApp.Field<SettingsPage>(window, "_settings");
        Assert.DoesNotContain(All<TextBlock>(settings), text => text.Text == "本地播放器");
        Assert.DoesNotContain(All<SettingRow>(settings), row => row.TitleText.Text is "影视场景自动调整播放器" or "VLC" or "mpv");
        Assert.Empty(All<PasswordBox>(settings));
        Assert.Single(All<PasswordBox>(TestApp.Field<ViewingPage>(window, "_viewing")));
    });

    [Fact]
    public void The_movie_switch_and_profile_reload_use_the_existing_configuration_field() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        config.MoviePlayerSync = false;
        var app = TestApp.Create(config);
        using var fx = app.Fx;
        using var window = new MainWindow(app);
        SetHost(app, window);
        TestApp.Field<MixerPage>(window, "_mixer").FindApps = _ => [];
        var page = TestApp.Field<ViewingPage>(window, "_viewing");
        var toggle = TestApp.Field<ToggleSwitch>(page, "_playerSync");
        toggle.Flip();
        Assert.True(config.MoviePlayerSync);
        toggle.Flip();
        Assert.False(config.MoviePlayerSync);
        config.MoviePlayerSync = true;
        window.ReloadConfiguration();
        Assert.True(toggle.Checked);
        Assert.Equal(StreamState.Idle, app.Controller.State);
    });

    [Fact]
    public void Player_updates_reach_the_new_page_and_leaving_discards_only_unsaved_password_text() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        var app = TestApp.Create(config);
        using var fx = app.Fx;
        using var window = new MainWindow(app);
        var page = TestApp.Field<ViewingPage>(window, "_viewing");
        var password = TestApp.Field<PasswordBox>(page, "_vlcPassword");
        var save = TestApp.Field<FluentButton>(page, "_vlcSave");
        var clear = TestApp.Field<FluentButton>(page, "_vlcClear");
        Assert.False(save.Enabled);
        Assert.False(clear.Enabled);
        config.VlcPasswordProtected = "encrypted fixture, never displayed";
        window.ShowPlayers();
        Assert.True(clear.Enabled);
        Assert.Contains("已保存", TestApp.Field<SettingRow>(page, "_vlcPasswordRow").DescriptionText.Text);
        password.Password = "typed, not saved";
        Assert.True(save.Enabled);
        page.PageHidden();
        Assert.Empty(password.Password);
        Assert.False(save.Enabled);
        Assert.True(clear.Enabled);
        Assert.Equal("encrypted fixture, never displayed", config.VlcPasswordProtected);
        Assert.DoesNotContain(All<TextBlock>(page), text => text.Text.Contains("encrypted fixture"));
    });

    private static void SetHost(TrayApp app, MainWindow window) => typeof(TrayApp)
        .GetField("_form", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, window);

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(control => (control is T item ? new[] { item } : []).Concat(All<T>(control)));
}
