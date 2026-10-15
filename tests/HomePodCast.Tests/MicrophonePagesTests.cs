using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class MicrophonePagesTests
{
    [Fact]
    public void Output_choices_explain_the_destination_immediately_without_opening_the_microphone() => Sta.Run(() =>
    {
        var config = TestApp.Config(); config.LatencyMs = 200;
        using var fx = new MicEffects(config.Effects);
        using var page = new EffectsPage(config, fx);
        var destination = TestApp.Field<Segmented>(page, "_destination");
        Assert.Equal(["HomePod", "本机", "两者"], destination.Items);
        Assert.Equal("输出到", destination.AccessibleName);
        Assert.Equal("麦克风监听", TestApp.Field<ToggleSwitch>(page, "_micOn").Text);
        Assert.Equal("麦克风监听已关闭。", TestApp.Field<TextBlock>(page, "_micStatus").Text);
        Assert.Equal("输入设备", TestApp.Field<FluentComboBox>(page, "_micDevice").AccessibleName);
        Assert.Equal("本机输出设备", TestApp.Field<FluentComboBox>(page, "_monitorDevice").AccessibleName);
        foreach (int index in new[] { 0, 1, 2, 0 })
        {
            destination.AccessibilityObject.GetChild(index)!.DoDefaultAction();
            var expected = index switch
            {
                1 => "你的声音只在监听设备（耳机）里播放，不会推到 HomePod。",
                2 => "音箱和耳机里都有你的声音；音箱里的晚约 220 ms。",
                _ => "你的声音会和电脑声音一起从 HomePod 播出，比实际晚约 220 ms。",
            };
            Assert.Equal(expected, TestApp.Field<TextBlock>(page, "_delayHint").Text);
            Assert.Equal((MicDestination)index, config.Effects.Destination);
            Assert.Equal(index != 0, TestApp.Field<FluentComboBox>(page, "_monitorDevice").Enabled);
            Assert.False(fx.MicOn);
            Assert.Null(fx.Mic);
            Assert.Null(fx.Monitor);
        }
        config.LatencyMs = 500;
        page.PageShown(); page.PageHidden();
        Assert.Contains("520 ms", TestApp.Field<TextBlock>(page, "_delayHint").Text);
        Assert.Contains(All<TextBlock>(page), text => text.Text == "每次启动 HomePodCast 时麦克风默认关闭。");
    });

    [Fact]
    public void Reverb_controls_describe_the_effect_of_increasing_each_parameter() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        using var fx = new MicEffects(config.Effects);
        using var page = new EffectsPage(config, fx);
        var tips = TestApp.Field<ToolTip>(page, "_tips");
        var names = new[] { "衰减（Room Size）", "高频阻尼（Damping）", "干湿比（Wet/Dry）", "预延迟（Pre-delay）" };
        foreach (string name in names)
        {
            var row = All<FieldRow>(page).Single(r => r.Caption.Text == name);
            Assert.StartsWith("调大后", tips.GetToolTip(row.Field));
            Assert.Equal(tips.GetToolTip(row.Field), tips.GetToolTip(row.Caption));
            Assert.Equal(tips.GetToolTip(row.Field), row.Field.AccessibleDescription);
        }
        TestApp.Field<ToggleSwitch>(page, "_reverbOn").Flip();
        TestApp.Field<FluentSlider>(page, "_room").HandleKey(Keys.End);
        TestApp.Field<FluentSlider>(page, "_damping").HandleKey(Keys.End);
        TestApp.Field<FluentSlider>(page, "_mix").HandleKey(Keys.End);
        TestApp.Field<FluentSlider>(page, "_preDelay").HandleKey(Keys.End);
        Assert.True(fx.Reverb.Enabled);
        Assert.Equal(1f, fx.Reverb.RoomSize);
        Assert.Equal(1f, fx.Reverb.Damping);
        Assert.Equal(1f, fx.Reverb.Mix);
        Assert.Equal(200f, fx.Reverb.PreDelayMs);
    });

    [Fact]
    public void Output_EQ_is_independent_of_mic_EQ_and_night_mode_restores_edits_made_during_override() => Sta.Run(() =>
    {
        var config = TestApp.Config(); config.Effects.MicEq.Preset = "vocal-boost";
        using var fx = new MicEffects(config.Effects);
        using var microphone = new EffectsPage(config, fx);
        using var equalizer = new EqualizerPage(config, fx);
        Assert.DoesNotContain(All<TextBlock>(microphone), text => text.Text == "HomePod 输出均衡");
        Assert.Contains(All<TextBlock>(equalizer), text => text.Text == "HomePod 输出均衡");
        var editor = TestApp.Field<EqEditor>(equalizer, "_outputEq");
        var preset = TestApp.Field<FluentComboBox>(editor, "_preset");
        preset.HandleKey(Keys.Down); // flat → bass boost
        Assert.Equal(EqPreset.BassBoost, fx.OutputEq.Preset);
        Assert.Equal("bass-boost", config.Effects.OutputEq.Preset);
        Assert.Equal(EqPreset.VocalBoost, fx.MicEq.Preset);

        config.NightMode = true; fx.OutputPresetOverride = "reduce-bass"; equalizer.ShowNightMode();
        Assert.NotEmpty(TestApp.Field<TextBlock>(equalizer, "_nightNote").Text);
        TestApp.Field<FluentSlider[]>(editor, "_bands")[0].HandleKey(Keys.Up); // saved curve starts at bass boost
        Assert.Equal("custom", config.Effects.OutputEq.Preset);
        Assert.Equal(7, config.Effects.OutputEq.Gains[0]);
        Assert.Equal(EqPreset.ReduceBass, fx.OutputEq.Preset);
        config.NightMode = false; fx.OutputPresetOverride = null; equalizer.PageShown();
        Assert.Empty(TestApp.Field<TextBlock>(equalizer, "_nightNote").Text);
        Assert.Equal(config.Effects.OutputEq.Gains, fx.OutputEq.GainsDb);
        Assert.Equal(EqPreset.VocalBoost, fx.MicEq.Preset);
        Assert.False(fx.MicOn);
    });

    [Fact]
    public void Main_window_flushes_output_EQ_and_reloads_the_editor_after_a_profile_switch() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        var app = TestApp.Create(config);
        using var fx = app.Fx;
        using var window = new MainWindow(app);
        var equalizer = TestApp.Field<EqualizerPage>(window, "_equalizer");
        var editor = TestApp.Field<EqEditor>(equalizer, "_outputEq");
        var preset = TestApp.Field<FluentComboBox>(editor, "_preset");
        preset.HandleKey(Keys.Down);
        Assert.True(TestApp.Field<System.Windows.Forms.Timer>(equalizer, "_saveDebounce").Enabled);
        window.Flush();
        Assert.False(TestApp.Field<System.Windows.Forms.Timer>(equalizer, "_saveDebounce").Enabled);
        Assert.Equal("bass-boost", AppConfig.Load().Effects.OutputEq.Preset);

        config.Effects.OutputEq = new EqSettings { Preset = "vocal-boost" };
        fx.Apply(); window.ReloadConfiguration();
        Assert.Equal("人声增强", preset.SelectedText);
        TestApp.Field<FluentSlider[]>(editor, "_bands")[0].HandleKey(Keys.Up);
        Assert.Equal("custom", config.Effects.OutputEq.Preset);
        Assert.Equal(-2, config.Effects.OutputEq.Gains[0]);
        Assert.Equal(config.Effects.OutputEq.Gains, fx.OutputEq.GainsDb);
    });

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(c => (c is T t ? new[] { t } : []).Concat(All<T>(c)));
}
