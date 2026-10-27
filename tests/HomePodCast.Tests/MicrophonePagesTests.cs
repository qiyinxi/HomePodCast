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
        var graph = TestApp.Field<EqGraph>(editor, "_graph");
        graph.SelectedIndex = 0;
        graph.HandleKey(Keys.Up); // the saved curve starts at bass boost's filters
        var bass = Equalizer.LegacyBands(EqPresets.Gains(EqPreset.BassBoost)!);
        Assert.Equal("custom", config.Effects.OutputEq.Preset);
        Assert.Equal(bass.Length, config.Effects.OutputEq.Bands!.Count);
        Assert.Equal(Math.Round(bass[0].GainDb + 0.5, 1), config.Effects.OutputEq.Bands[0].GainDb, 6);
        Assert.Equal(EqPreset.ReduceBass, fx.OutputEq.Preset);
        config.NightMode = false; fx.OutputPresetOverride = null; equalizer.PageShown();
        Assert.Empty(TestApp.Field<TextBlock>(equalizer, "_nightNote").Text);
        Assert.Equal(config.Effects.OutputEq.Bands.Select(b => b.ToBand()), fx.OutputEq.Bands);
        Assert.Equal(config.Effects.OutputEq.Gains, fx.OutputEq.GainsDb); // what an older version would play
        Assert.Equal(EqPreset.VocalBoost, fx.MicEq.Preset);
        Assert.False(fx.MicOn);
    });

    [Fact]
    public void EQ_editor_edits_bands_from_the_graph_and_the_fields() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        using var fx = new MicEffects(config.Effects);
        using var page = new EqualizerPage(config, fx);
        var editor = TestApp.Field<EqEditor>(page, "_outputEq");
        var graph = TestApp.Field<EqGraph>(editor, "_graph");
        var eq = config.Effects.OutputEq;
        Assert.Equal(5, graph.Bands.Count); // 平直: the five-band layout at 0 dB
        Assert.Null(eq.Bands);

        graph.HandleKey(Keys.D3);           // the 1 kHz peak
        graph.HandleKey(Keys.Right);        // + 1/12 octave
        graph.HandleKey(Keys.Up);           // + 0.5 dB
        graph.HandleKey(Keys.PageUp);       // Q × 2^(1/6)
        Assert.Equal("custom", eq.Preset);
        var band = eq.Bands![2].ToBand();
        Assert.Equal(EqFilterType.Peak, band.Type);
        Assert.InRange(band.Frequency, 1058, 1061);
        Assert.Equal(0.5, band.GainDb, 6);
        Assert.InRange(band.Q, 1.11, 1.13);
        Assert.Equal(eq.Bands.Select(b => b.ToBand()), fx.OutputEq.Bands);

        // The fields edit the selected band: type, then slope of a cut.
        var type = TestApp.Field<FluentComboBox>(editor, "_type");
        type.AccessibilityObject.Value = "低切";
        Assert.Equal(EqFilterType.LowCut, eq.Bands[2].Type);
        Assert.False(TestApp.Field<FluentSlider>(editor, "_gain").Enabled);
        Assert.False(TestApp.Field<FieldRow>(editor, "_slopeRow").Collapsed);
        TestApp.Field<Segmented>(editor, "_slope").AccessibilityObject.GetChild(3)!.DoDefaultAction();
        Assert.Equal(48, eq.Bands[2].Slope);
        Assert.Equal(new EqBand(EqFilterType.LowCut, eq.Bands[2].Frequency, 0.5, 0.707, 48), fx.OutputEq.Bands[2]);

        // Insert adds a band in the widest gap, Delete removes the selected one; Space switches one off.
        graph.HandleKey(Keys.Insert);
        Assert.Equal(6, eq.Bands.Count);
        Assert.Equal(5, graph.SelectedIndex);
        graph.HandleKey(Keys.Space);
        Assert.False(eq.Bands[5].Enabled);
        graph.HandleKey(Keys.Delete);
        Assert.Equal(5, eq.Bands.Count);

        // Output gain, and the switch that bypasses the whole EQ.
        TestApp.Field<FluentSlider>(editor, "_output").UserSetValue(-35);
        Assert.Equal(-3.5, eq.OutputGainDb, 6);
        Assert.Equal(-3.5, fx.OutputEq.OutputGainDb, 6);
        TestApp.Field<ToggleSwitch>(editor, "_on").Flip();
        Assert.False(eq.Enabled);
        Assert.All(fx.OutputEq.Bands, b => Assert.False(b.Enabled));
        Assert.Equal(0, fx.OutputEq.OutputGainDb);
    });

    [Fact]
    public void EQ_editor_offers_to_make_room_for_a_boost() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        config.Effects.OutputEq = new EqSettings { Preset = "custom", Bands = [new() { Type = EqFilterType.Peak, Frequency = 100, GainDb = 9, Q = 1 }] };
        using var fx = new MicEffects(config.Effects);
        using var page = new EqualizerPage(config, fx);
        var editor = TestApp.Field<EqEditor>(page, "_outputEq");
        Assert.Contains(EqText.Db(9), TestApp.Field<TextBlock>(editor, "_headroom").Text);
        var fix = TestApp.Field<FluentButton>(editor, "_fixHeadroom");
        Assert.False(fix.Collapsed);
        fix.PerformClick();
        Assert.InRange(config.Effects.OutputEq.OutputGainDb, -9.1, -8.9);
        Assert.InRange(fx.OutputEq.PeakGainDb, 0, 0.1);
        Assert.Empty(TestApp.Field<TextBlock>(editor, "_headroom").Text);
        Assert.True(fix.Collapsed);
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
        var graph = TestApp.Field<EqGraph>(editor, "_graph");
        graph.SelectedIndex = 0;
        graph.HandleKey(Keys.Up);
        var vocal = Equalizer.LegacyBands(EqPresets.Gains(EqPreset.VocalBoost)!);
        Assert.Equal("custom", config.Effects.OutputEq.Preset);
        Assert.Equal(Math.Round(vocal[0].GainDb + 0.5, 1), config.Effects.OutputEq.Bands![0].GainDb, 6);
        Assert.Equal(config.Effects.OutputEq.Bands.Select(b => b.ToBand()), fx.OutputEq.Bands);
    });

    [Fact]
    public void Voice_chain_sections_switch_and_tune_the_engine_without_opening_the_microphone() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        using var fx = new MicEffects(config.Effects);
        using var page = new EffectsPage(config, fx);
        var sliders = ((System.Collections.IEnumerable)TestApp.Field<object>(page, "_params")).Cast<object>()
            .Select(p => (FluentSlider)p.GetType().GetProperty("Slider")!.GetValue(p)!).ToList();
        Assert.Equal(12, sliders.Count);
        Assert.All(sliders, s => Assert.False(s.Enabled)); // every new stage starts off, its controls with it

        TestApp.Field<ToggleSwitch>(page, "_compressorOn").Flip();
        Assert.True(fx.Compressor.Enabled);
        Assert.True(sliders[1].Enabled);
        sliders[1].HandleKey(Keys.End);   // threshold: 0 dB
        sliders[2].HandleKey(Keys.Home);  // ratio: 1:1
        Assert.Equal((0.0, 1.0), (config.Effects.CompressorThresholdDb, config.Effects.CompressorRatio));
        Assert.Equal((0f, 1f), (fx.Compressor.ThresholdDb, fx.Compressor.Ratio));

        TestApp.Field<ToggleSwitch>(page, "_lowCutOn").Flip();
        sliders[0].UserSetValue(500);     // halfway on the log scale from 20 to 400 Hz
        Assert.InRange(config.Effects.LowCutHz, 88, 91);
        TestApp.Field<Segmented>(page, "_lowCutSlope").AccessibilityObject.GetChild(1)!.DoDefaultAction();
        Assert.Equal(new EqBand(EqFilterType.LowCut, config.Effects.LowCutHz, Slope: 24), fx.LowCut.Bands.Single());

        TestApp.Field<ToggleSwitch>(page, "_limiterOn").Flip();
        sliders[10].UserSetValue(-60);
        Assert.Equal(-6, config.Effects.LimiterCeilingDb, 6);
        Assert.Equal(-6f, fx.Limiter.CeilingDb);
        Assert.Equal($"{-6.0:0.0} dB", sliders[10].AccessibilityObject.Value); // in the user's number format
        Assert.Equal("噪声门阈值", TestApp.Field<FluentSlider>(page, "_gateThreshold").AccessibleName);
        Assert.False(fx.MicOn);
        Assert.Null(fx.Mic);
    });

    [Fact]
    public void A_saved_input_channel_is_shown_and_can_mix_all_channels() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        config.Effects.MicChannel = 1;
        using var fx = new MicEffects(config.Effects);
        using var page = new EffectsPage(config, fx);
        var channel = TestApp.Field<FluentComboBox>(page, "_micChannel");
        Assert.False(TestApp.Field<FieldRow>(page, "_channelRow").Collapsed);
        Assert.Equal("声道 2", channel.SelectedText);
        channel.AccessibilityObject.Value = "全部声道混合";
        Assert.Equal(-1, config.Effects.MicChannel);

        var plain = TestApp.Config();
        using var fx2 = new MicEffects(plain.Effects);
        using var page2 = new EffectsPage(plain, fx2);
        Assert.True(TestApp.Field<FieldRow>(page2, "_channelRow").Collapsed); // a one-channel or unknown device: no choice
    });

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(c => (c is T t ? new[] { t } : []).Concat(All<T>(c)));
}
