using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>The real Home controls on a constructor-free app, with fake PC output and no speaker connection.</summary>
[Collection("Layout work")]
public class HomeVolumeTests
{
    [Fact]
    public void The_volume_icon_toggles_speaker_mute_without_a_late_slider_save_undoing_it() => Sta.Run(() =>
    {
        using var host = new Host();
        var mute = TestApp.Field<FluentButton>(host.Page, "_mute");
        var volume = TestApp.Field<FluentSlider>(host.Page, "_volume");
        var wait = TestApp.Field<TimerDebounce>(host.Page, "_volumeWait");
        var tips = TestApp.Field<ToolTip>(host.Page, "_tips");
        Assert.Equal(Glyph.Volume, mute.Glyph);
        Assert.Equal("HomePod 静音", mute.AccessibleName);
        Assert.True(mute.TabStop);

        volume.UserSetValue(23);
        Assert.True(wait.Enabled);
        mute.PerformClick();
        Assert.True(host.App.Controller.Muted);
        Assert.False(wait.Enabled);
        Assert.Equal(23, host.App.Config.Volume);
        Assert.Equal(23, host.App.Controller.Volume);
        Assert.Equal(Glyph.Mute, mute.Glyph);
        Assert.Equal("取消静音", mute.AccessibleName);
        Assert.Equal("取消静音", tips.GetToolTip(mute));
        Assert.False(host.App.Config.MutePcWhileStreaming);
        Assert.Empty(host.Pc.Writes);

        mute.PerformClick();
        Assert.False(host.App.Controller.Muted);
        Assert.Equal(23, host.App.Controller.Volume);
        Assert.Equal(Glyph.Volume, mute.Glyph);
        Assert.Equal("HomePod 静音", mute.AccessibleName);
    });

    [Fact]
    public void HomePod_only_mutes_and_restores_the_pc_without_changing_its_volume_or_speaker_mute() => Sta.Run(() =>
    {
        using var host = new Host(streaming: true);
        var only = TestApp.Field<ToggleSwitch>(host.Page, "_onlyHomePod");
        var tips = TestApp.Field<ToolTip>(host.Page, "_tips");
        Assert.Equal("仅 HomePod 输出", only.Text);
        Assert.Contains("电脑音量数值不变", tips.GetToolTip(only));
        Assert.Contains("实验性", only.AccessibleDescription);
        only.Flip();
        Assert.True(host.App.Config.MutePcWhileStreaming);
        Assert.True(host.Pc[FakePcOutput.Speakers].Muted);
        Assert.Equal(0.5f, host.Pc[FakePcOutput.Speakers].Level);
        Assert.False(host.App.Controller.Muted);
        Assert.Equal(22, host.App.Controller.Volume);
        Assert.Equal(VolumeKeyMode.FollowWindows, host.App.Config.VolumeKeys);
        Assert.Equal(VolumeKeyMode.WhileStreaming, host.App.EffectiveVolumeKeys);

        only.Flip();
        Assert.False(host.App.Config.MutePcWhileStreaming);
        Assert.False(host.Pc[FakePcOutput.Speakers].Muted);
        Assert.Equal(0.5f, host.Pc[FakePcOutput.Speakers].Level);
        host.App.PlayOnlyOnHomePod(); // The existing notice's action also refreshes the new switch.
        Assert.True(only.Checked);
        host.App.SetMutePcWhileStreaming(false);
        Assert.False(only.Checked);
    });

    [Fact]
    public void HomePod_only_is_saved_before_connecting_and_external_mute_changes_refresh_the_icon() => Sta.Run(() =>
    {
        using var host = new Host();
        TestApp.Field<ToggleSwitch>(host.Page, "_onlyHomePod").Flip();
        Assert.True(host.App.Config.MutePcWhileStreaming);
        Assert.Empty(host.Pc.Writes);
        host.App.ToggleSpeakerMute(); // Same action as the hotkey and tray.
        Assert.Equal(Glyph.Mute, TestApp.Field<FluentButton>(host.Page, "_mute").Glyph);
        Assert.True(TestApp.Field<ToggleSwitch>(host.Page, "_onlyHomePod").Checked);
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("de")]
    [InlineData("en")]
    public void Both_switch_labels_fit_in_a_narrow_volume_card(string language) => Sta.Run(() =>
    {
        string before = L.Language;
        try
        {
            L.Use(language);
            using var host = new Host();
            var only = TestApp.Field<ToggleSwitch>(host.Page, "_onlyHomePod");
            var night = TestApp.Field<ToggleSwitch>(host.Page, "_night");
            var row = Assert.IsType<RowPanel>(LogicalLayout.Parent(only));
            row.Width = Theme.Dp(298, row.DeviceDpi);
            row.PerformLayout();
            foreach (var toggle in new[] { only, night })
            {
                Assert.True(row.ClientRectangle.Contains(LogicalLayout.Bounds(toggle)), $"{language}: {toggle.Text} outside row");
                Assert.True(toggle.Width >= toggle.GetPreferredSize(Size.Empty).Width, $"{language}: {toggle.Text} clipped");
            }
            Assert.False(only.Bounds.IntersectsWith(night.Bounds));
        }
        finally { L.Use(before); }
    });

    /// <summary>5.1 环绕声 (experimental since 1.4) has a row of its own: beside the other two it wrapped outside theirs.</summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("de")]
    [InlineData("en")]
    public void The_surround_switch_fits_its_own_row_in_a_narrow_volume_card(string language) => Sta.Run(() =>
    {
        string before = L.Language;
        try
        {
            L.Use(language);
            using var host = new Host();
            var surround = TestApp.Field<ToggleSwitch>(host.Page, "_surround");
            var only = TestApp.Field<ToggleSwitch>(host.Page, "_onlyHomePod");
            var row = Assert.IsType<RowPanel>(LogicalLayout.Parent(surround));
            Assert.NotSame(row, LogicalLayout.Parent(only));
            row.Width = Theme.Dp(298, row.DeviceDpi);
            row.PerformLayout();
            Assert.True(row.ClientRectangle.Contains(LogicalLayout.Bounds(surround)), $"{language}: {surround.Text} outside row");
            Assert.True(surround.Width >= surround.GetPreferredSize(Size.Empty).Width, $"{language}: {surround.Text} clipped");
        }
        finally { L.Use(before); }
    });

    /// <summary>
    /// A WinForms tooltip may be as wide as the screen: in one piece the 5.1 description (experimental since 1.4) ran across
    /// a whole monitor. Every line stays under about 800 px at 100 % in every language, in the tooltip's own font.
    /// </summary>
    [Fact]
    public void The_surround_description_keeps_every_line_narrow_in_every_language()
    {
        string before = L.Language;
        try
        {
            using var screen = Graphics.FromHwnd(IntPtr.Zero);
            // Windows' tooltip font, Segoe UI 9 pt (CJK falls back as in the tooltip), measured on this DC and scaled to 100 %.
            using var font = new Font("Segoe UI", 9f);
            float scale = 96f / screen.DpiX;
            foreach (var language in L.Languages)
            {
                L.Use(language);
                var lines = HomePage.SurroundDescription().Split('\n');
                Assert.True(lines.Length >= 8, $"{language}: {lines.Length} lines");
                foreach (var line in lines)
                {
                    int width = (int)(TextRenderer.MeasureText(screen, line, font).Width * scale);
                    Assert.True(width <= 820, $"{language}: {width} px at 100 %: {line}");
                }
            }
        }
        finally { L.Use(before); }
    }

    [Fact]
    public void The_surround_switch_is_saved_and_described_as_experimental() => Sta.Run(() =>
    {
        using var host = new Host();
        var surround = TestApp.Field<ToggleSwitch>(host.Page, "_surround");
        var tips = TestApp.Field<ToolTip>(host.Page, "_tips");
        Assert.Equal("5.1 环绕声", surround.Text);
        Assert.False(surround.Checked); // off on a fresh config
        Assert.Contains($"约 {SurroundPolicy.LatencyMs / 1000.0:F1} 秒", surround.AccessibleDescription);
        Assert.EndsWith("实验性功能。", surround.AccessibleDescription);
        Assert.Equal(surround.AccessibleDescription, tips.GetToolTip(surround));

        // The unit-tests profile's file, which other test classes save to meanwhile: only that a save happened is certain.
        string file = Path.Combine(AppConfig.Directory, "config.json");
        var longAgo = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try { if (File.Exists(file)) File.SetLastWriteTimeUtc(file, longAgo); }
        catch (IOException) { } // being replaced by another test's save right now
        surround.Flip();
        Assert.True(host.App.Config.Surround51);
        Assert.True(File.Exists(file) && File.GetLastWriteTimeUtc(file) > longAgo, "the switch was not saved");
        Assert.Equal(StreamState.Idle, host.App.Controller.State); // not connected: nothing to reconnect

        host.App.SetSurround51(false); // as from elsewhere: the switch follows
        Assert.False(surround.Checked);
        Assert.False(host.App.Config.Surround51);
    });

    private sealed class Host : IDisposable
    {
        private readonly MainWindow _window;

        public Host(bool streaming = false)
        {
            var config = TestApp.Config();
            config.Volume = 22;
            App = TestApp.Create(config);
            Pc = FakePcOutput.WithSpeakers();
            App.UsePcOutput(Pc);
            App.Controller.SetVolume(22);
            if (streaming)
                typeof(StreamController).GetProperty(nameof(StreamController.State))!.SetValue(App.Controller, StreamState.Streaming);
            _window = new MainWindow(App) { PrepareAhead = false, Opacity = 0, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            typeof(TrayApp).GetField("_form", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(App, _window);
            Page = TestApp.Field<HomePage>(_window, "_home");
            _window.Show(); // An invisible fake app; never starts discovery, capture or a sender.
            _window.ShowSoundOptions();
        }

        public TrayApp App { get; }
        public FakePcOutput Pc { get; }
        public HomePage Page { get; }

        public void Dispose()
        {
            App.SetMutePcWhileStreaming(false);
            _window.Dispose();
            App.Fx.Dispose();
        }
    }
}
