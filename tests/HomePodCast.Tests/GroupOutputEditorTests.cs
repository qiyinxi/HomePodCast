using System.Net;
using System.Text.Json;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

public class GroupOutputEditorTests
{
    private static AirPlayDevice Home() => new("Home", "AA:01", IPAddress.Loopback, 7000, "AudioAccessory6,1", new Dictionary<string, string>());
    private static DlnaDevice Renderer(string udn = "uuid:a-b") => new(udn, "Renderer", IPAddress.Loopback,
        new("http://127.0.0.1:1/description"), new("urn:schemas-upnp-org:service:AVTransport:1", new("http://127.0.0.1:1/transport")),
        new("urn:schemas-upnp-org:service:ConnectionManager:1", new("http://127.0.0.1:1/connection")), null, null, null);
    private static AppConfig Config(bool auxiliaries = false)
    {
        var cfg = new AppConfig
        {
            DeviceId = "AA:01", DeviceName = "Home", Host = "127.0.0.1", AutoConnect = false, Volume = 40,
            CaptureDeviceId = "capture", GroupSpeakers = [new() { DeviceId = "AA:01", Name = "Home", Host = "127.0.0.1",
                Position = SpeakerPosition.Right, DelayMs = 83, VolumeOffset = -4 }],
            GroupCaptureDeviceIds = ["AA:01"], GroupCapturePositions = new() { ["AA01"] = SpeakerPosition.Left },
            GroupCaptureDelayTrims = new() { ["AA01"] = -137 }, GroupRequiredOutputs = new() { ["AA01"] = false },
        };
        if (auxiliaries)
        {
            cfg.GroupLocalOutputs = [new("local:a-b", "Local") { IsBluetooth = true, ExtraLatencyMs = 317, TrimMs = 29 },
                new("local:ab", "Unused local")];
            cfg.GroupCaptureLocalDeviceIds = ["local:a-b"];
            cfg.GroupDlnaOutputs = [new("uuid:a-b", "Renderer") { EstimatedLatencyMs = 2460, TrimMs = 61 }, new("uuid:ab", "Unused renderer")];
            cfg.GroupCaptureDlnaUdns = ["uuid:a-b"];
        }
        GroupSettings.Normalize(cfg); OutputGroups.Normalize(cfg); return cfg;
    }
    private static IEnumerable<T> All<T>(Control control) where T : Control
    {
        if (control is T match) yield return match;
        foreach (Control child in control.Controls) foreach (var found in All<T>(child)) yield return found;
    }
    private static FluentComboBox Combo(Control panel, string name) => All<FluentComboBox>(panel).Single(c => c.AccessibleName == name);
    private static FluentSlider Slider(Control panel, string name) => All<FluentSlider>(panel).Single(c => c.AccessibleName == name);
    private static FluentButton Button(Control panel, string name) => All<FluentButton>(panel).Single(b => b.Text == name);
    private static ToggleSwitch Toggle(Control panel, string name) => All<ToggleSwitch>(panel).Single(t => t.AccessibleName == name);
    private static void ClickHiddenDialog(Control dialog, string name)
    {
        var button = Button(dialog, name); Assert.True(button.Enabled);
        // Exercise its click event without showing a native dialog or enabling any real audio path.
        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(button, [EventArgs.Empty]);
    }

    [Fact]
    public void Auxiliary_only_startup_respects_autoconnect_and_restart_intent_without_AirPlay_discovery()
    {
        var cfg = new AppConfig { AutoConnect = true, DeviceId = null, GroupCaptureDeviceIds = [],
            GroupLocalOutputs = [new("local", "local")], GroupCaptureLocalDeviceIds = ["local"] };
        Assert.True(TrayApp.ShouldConnectOnStartup(cfg, null));
        Assert.False(TrayApp.ShouldConnectOnStartup(cfg, false));
        cfg.AutoConnect = false;
        Assert.False(TrayApp.ShouldConnectOnStartup(cfg, null));
        Assert.True(TrayApp.ShouldConnectOnStartup(cfg, true));
        cfg.GroupCaptureLocalDeviceIds = [];
        Assert.False(TrayApp.ShouldConnectOnStartup(cfg, true));
        cfg.GroupDlnaOutputs = [new("uuid:renderer", "renderer")];
        cfg.GroupCaptureDlnaUdns = ["uuid:renderer"];
        Assert.True(TrayApp.ShouldConnectOnStartup(cfg, true));
    }

    [Fact]
    public void Overwriting_the_selected_preset_keeps_playback_settings_but_allows_reselecting_it()
    {
        var cfg = Config(true);
        var saved = OutputGroups.Save(cfg, "only group", OutputGroups.Capture(cfg));
        cfg.SelectedOutputGroupId = saved.Id;
        var draft = OutputGroups.Capture(cfg); draft.GroupDelayMs = 4000;
        OutputGroups.Save(cfg, "only group", draft, saved.Id);
        Assert.Null(cfg.SelectedOutputGroupId);
        Assert.Null(cfg.GroupDelayMs);
        Assert.Equal(4000, Assert.Single(cfg.SavedOutputGroups).Settings.GroupDelayMs);
        OutputGroups.Apply(cfg, cfg.SavedOutputGroups[0].Settings);
        Assert.Equal(4000, cfg.GroupDelayMs);
    }

    [Fact]
    public void Inspecting_a_saved_file_layout_never_adds_its_unselected_speakers_to_live_capture() => Sta.Run(() =>
    {
        var cfg = Config();
        cfg.GroupSpeakers!.Add(new() { DeviceId = "BB:02", Name = "File only", Position = SpeakerPosition.RearLeft, DelayMs = 57 });
        using var panel = new GroupPanel(cfg);
        Assert.Equal(1, panel.LiveOutputCount);
        panel.SetSource(false, fileSource: true);
        Assert.Equal(2, panel.SelectedLayout().Length);
        panel.SetSource(false);
        Assert.Equal(1, panel.LiveOutputCount);
        panel.Apply();
        Assert.Equal(["AA:01"], cfg.GroupCaptureDeviceIds);
        Assert.Contains(cfg.GroupSpeakers!, row => row.DeviceId == "BB:02" && row.DelayMs == 57);
        Button(panel, "清空选择").PerformClick();
        panel.SetSource(false, fileSource: true);
        panel.SetSource(false);
        Assert.Equal(0, panel.LiveOutputCount);
    });

    [Fact]
    public void Leader_only_pairs_offer_only_the_connected_member_for_relative_alignment() => Sta.Run(() =>
    {
        var (left, right) = StereoPairTests.IdlePair();
        var cfg = new AppConfig { DeviceId = StereoPairs.PairId(StereoPairs.Tsid(left)!), GroupPairLeaderOnly = true };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([right, left]);
        Assert.Equal(1, panel.LiveOutputCount);
        Assert.Single(panel.AlignmentOutputs());
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Leader_only_pair_accepts_one_discovered_member_without_a_saved_bank(bool leaderPresent) => Sta.Run(() =>
    {
        var (left, right) = StereoPairTests.IdlePair();
        var cfg = new AppConfig { DeviceId = StereoPairs.PairId(StereoPairs.Tsid(left)!), GroupPairLeaderOnly = true };
        using var panel = new GroupPanel(cfg);
        var found = leaderPresent ? left : right;
        panel.SetFound([found]);
        Assert.Equal(1, panel.LiveOutputCount);
        Assert.Equal(found.Name, Assert.Single(panel.AlignmentOutputs()).Name);
        Assert.Null(panel.BlockReason);
    });

    [Fact]
    public void Opening_switching_sources_and_inspecting_offline_banks_never_discovers_or_changes_selection() => Sta.Run(() =>
    {
        var cfg = Config(true); string before = JsonSerializer.Serialize(cfg); int local = 0, dlna = 0;
        using var panel = new GroupPanel(cfg, () => { local++; throw new InvalidOperationException("unexpected endpoint probe"); },
            _ => { dlna++; throw new InvalidOperationException("unexpected network browse"); });
        panel.SetFound([Home()]);
        Assert.NotNull(Slider(panel, "Local 延迟（毫秒）"));
        Assert.DoesNotContain(All<FluentComboBox>(panel), c => c.AccessibleName == "Unused local 位置" || c.AccessibleName == "Unused renderer 位置");
        panel.SetSource(false, fileSource: true);
        Assert.DoesNotContain(All<FluentComboBox>(panel), c => c.AccessibleName == "Local 位置");
        Assert.Equal(83, Slider(panel, "Home 延迟（毫秒）").Value);
        Assert.Equal("右", Combo(panel, "Home 位置").SelectedText);
        panel.SetSource(false);
        Assert.Equal(-137, Slider(panel, "Home 延迟（毫秒）").Value);
        Assert.Equal("左", Combo(panel, "Home 位置").SelectedText);
        Combo(panel, "输出类型").SelectedIndex = 1;
        Assert.Empty(Combo(panel, "选择输出").Items);
        Assert.False(Button(panel, "添加输出").Enabled);
        Assert.Equal(0, local); Assert.Equal(0, dlna);
        Assert.Equal(before, JsonSerializer.Serialize(cfg));
    });

    [Fact]
    public void Injected_discovery_adds_exact_endpoint_and_UDN_and_applies_only_after_explicit_action() => Sta.Run(() =>
    {
        var cfg = Config();
        using var panel = new GroupPanel(cfg);
        panel.SetFound([Home()]);
        panel.SetAvailableOutputs([new("local:a-b", "Local", EndpointFormFactor.Speakers)], [Renderer()]);
        Combo(panel, "输出类型").SelectedIndex = 1; Button(panel, "添加输出").PerformClick();
        Slider(panel, "Local 附加延迟（毫秒）").Value = 317;
        Toggle(panel, "Local 蓝牙输出").Checked = true;
        Toggle(panel, "Local 必需输出").Checked = true;
        Slider(panel, "Local 延迟（毫秒）").Value = -23;
        Slider(panel, "Local 音量偏移").Value = 9;
        Combo(panel, "Local 位置").SelectedIndex = (int)ChannelMode.RightOnly;
        Combo(panel, "输出类型").SelectedIndex = 2; Button(panel, "添加输出").PerformClick();
        Slider(panel, "Renderer 估计延迟（毫秒）").Value = 2460;
        Combo(panel, "Renderer 首选格式").SelectedIndex = 2;
        Combo(panel, "Renderer HTTP 传输").SelectedIndex = 1;
        Toggle(panel, "Renderer 必需输出").Checked = true;
        Combo(panel, "整组延迟").SelectedIndex = 1; Slider(panel, "整组延迟（毫秒）").Value = 2740;
        Assert.Empty(cfg.GroupLocalOutputs); Assert.Empty(cfg.GroupDlnaOutputs);
        panel.Apply();
        var plan = GroupPlan.FromConfig(cfg)!;
        var local = Assert.Single(plan.LocalOutputs); var renderer = Assert.Single(plan.DlnaOutputs);
        Assert.Equal("local:a-b", local.DeviceId); Assert.True(local.IsBluetooth); Assert.True(local.Required);
        Assert.Equal((317, -23, 9, ChannelMode.RightOnly), (local.ExtraLatencyMs, local.TrimMs, local.VolumeOffset, local.Channels));
        Assert.Equal(("uuid:a-b", 2460, "wav", "length", true), (renderer.Udn, renderer.EstimatedLatencyMs, renderer.PreferredFormat, renderer.Framing, renderer.Required));
        Assert.Equal(2740, plan.GroupDelayMs); Assert.Equal(83, cfg.GroupSpeakers!.Single().DelayMs);
        Assert.Equal(-137, plan.DelayTrimFor("AA:01")); Assert.Equal(40, cfg.Volume);
    });

    [Fact]
    public void Clearing_the_primary_then_adding_local_output_cannot_resurrect_the_old_HomePod() => Sta.Run(() =>
    {
        var cfg = Config();
        using var panel = new GroupPanel(cfg);
        panel.SetFound([Home()]);
        Button(panel, "清空选择").PerformClick();
        Assert.Equal("请至少选择一个输出。", panel.BlockReason);
        panel.SetAvailableOutputs([new("local", "Local", EndpointFormFactor.Speakers)], []);
        Combo(panel, "输出类型").SelectedIndex = 1; Button(panel, "添加输出").PerformClick();
        panel.Apply();
        Assert.Null(cfg.DeviceId); Assert.Empty(GroupPlan.FromConfig(cfg)!.Members);
        Assert.Single(GroupPlan.FromConfig(cfg)!.LocalOutputs); Assert.Equal(40, cfg.Volume);
        Assert.Single(cfg.GroupSpeakers!); // file layout/preferences remain saved
    });

    [Fact]
    public void File_apply_does_not_commit_pending_live_endpoint_edits_or_live_timing() => Sta.Run(() =>
    {
        var cfg = Config(true);
        using var panel = new GroupPanel(cfg); panel.SetFound([Home()]);
        Slider(panel, "Local 附加延迟（毫秒）").Value = 999;
        panel.SetSource(false, fileSource: true);
        Slider(panel, "Home 延迟（毫秒）").Value = 95; panel.Apply(activateCapture: false);
        Assert.Equal(95, cfg.GroupSpeakers!.Single().DelayMs);
        Assert.Equal(-137, cfg.GroupCaptureDelayTrims["AA01"]);
        Assert.Equal(317, cfg.GroupLocalOutputs.Single(o => o.Name == "Local").ExtraLatencyMs);
        Assert.Equal(["local:a-b"], cfg.GroupCaptureLocalDeviceIds);
        panel.SetSource(false); panel.Apply();
        Assert.Equal(999, cfg.GroupLocalOutputs.Single(o => o.Name == "Local").ExtraLatencyMs);
        Assert.Equal(95, cfg.GroupSpeakers!.Single().DelayMs);
    });

    [Fact]
    public void Feedback_is_refused_and_runtime_protocol_fallback_changes_the_alignment_tier() => Sta.Run(() =>
    {
        var cfg = Config(true); cfg.CaptureDeviceId = "LOCAL:A-B";
        using var panel = new GroupPanel(cfg); panel.SetFound([Home()]);
        Assert.Equal(L.T(LocalOutputSink.CapturedOutputText), panel.BlockReason);
        panel.ShowOutputStatus([new("AA:01", "Home", OutputKind.AirPlay2, OutputState.Streaming, null, false, false, false, 2000, ReceiverProtocol.Raop)], 2740);
        Assert.Equal("已连接", All<TextBlock>(panel).Single(t => t.AccessibleName == "Home 状态").Text);
        Assert.Contains(All<TextBlock>(panel), t => t.Text == "当前整组延迟：2740 ms");
    });

    [Theory]
    [InlineData(137, 6, 60, 37)]
    [InlineData(-137, -6, 60, -37)]
    [InlineData(0, 1, 60, -17)]
    [InlineData(0, -1, 60, 17)]
    [InlineData(29, 1, 240, 25)]
    public void Phone_video_calibration_uses_relative_signed_offsets(int current, int frames, int fps, int expected)
    {
        Assert.True(ManualGroupAlignment.TryTrim(current, frames, fps, out int actual)); Assert.Equal(expected, actual);
    }

    [Fact]
    public void Phone_video_calibration_rejects_invalid_rates_and_out_of_range_trims()
    {
        Assert.False(ManualGroupAlignment.TryTrim(0, 1, 0, out _));
        Assert.False(ManualGroupAlignment.TryTrim(-5000, 1, 60, out _));
        Assert.False(ManualGroupAlignment.TryTrim(5000, -1, 60, out _));
    }

    [Fact]
    public void Manual_dialog_is_silent_until_test_is_explicitly_clicked_and_trim_stays_a_draft() => Sta.Run(() =>
    {
        var cfg = Config(true);
        using var panel = new GroupPanel(cfg);
        int starts = 0;
        using var dialog = new GroupAlignmentForm(panel.AlignmentOutputs(), true, _ => { starts++; return Task.CompletedTask; });
        Assert.Equal(0, starts);
        ClickHiddenDialog(dialog, "开始测试音"); Assert.Equal(1, starts); // fake callback only
        Slider(dialog, "相对偏移帧数").Value = 6;
        ClickHiddenDialog(dialog, "应用微调");
        Assert.NotNull(dialog.Adjustment);
        panel.ApplyManualTrim(dialog.Adjustment!);
        Assert.Equal(29, cfg.GroupLocalOutputs.Single(o => o.Name == "Local").TrimMs);
        Assert.Equal(-71, Slider(panel, "Local 延迟（毫秒）").Value);
        panel.Apply();
        Assert.Equal(-71, cfg.GroupLocalOutputs.Single(o => o.Name == "Local").TrimMs);
    });
}
