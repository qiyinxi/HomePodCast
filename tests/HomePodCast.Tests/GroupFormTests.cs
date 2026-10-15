using System.Net;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

/// <summary>Per-output settings, against synthetic scans. No window is shown, no receiver is contacted.</summary>
public class GroupFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void File_mode_cannot_remove_a_speaker_still_referenced_by_live_capture(bool explicitLiveIds) => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), "hpc-group-ui-" + Guid.NewGuid().ToString("N"), "config.json");
        try
        {
            var cfg = new AppConfig { DeviceId = A.DeviceId, DeviceName = A.Name, MultiRoomDeviceId = B.DeviceId, MultiRoomDeviceName = B.Name };
            using var panel = new GroupPanel(cfg);
            panel.SetFound([A, B]);
            if (explicitLiveIds) panel.Apply();
            panel.SetSource(true);
            var remove = All<FluentButton>(panel).Single(b => b.AccessibleName == "移除 书房");
            Assert.False(remove.Enabled);
            remove.PerformClick();
            panel.Apply(activateCapture: false);
            Assert.Equal([A.DeviceId, B.DeviceId], GroupPlan.FromConfig(cfg)!.Members.Select(m => m.DeviceId));
            cfg.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal([A.DeviceId, B.DeviceId], GroupPlan.FromConfig(loaded)!.Members.Select(m => m.DeviceId));
            var restored = new AppConfig();
            UsageProfiles.Apply(restored, UsageProfiles.Capture(loaded));
            Assert.Equal([A.DeviceId, B.DeviceId], GroupPlan.FromConfig(restored)!.Members.Select(m => m.DeviceId));
            panel.SetSource(false);
            Assert.True(All<FluentButton>(panel).Single(b => b.AccessibleName == "移除 书房").Enabled);
        }
        finally
        {
            if (System.IO.Directory.Exists(Path.GetDirectoryName(path)))
                System.IO.Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    });

    [Fact]
    public void A_partial_pair_scan_cannot_reassign_the_remaining_member_to_the_first_channel() => OnSta(() =>
    {
        var (left, right) = StereoPairTests.IdlePair();
        var cfg = new AppConfig { DeviceId = StereoPairs.PairId(StereoPairs.Tsid(left)!), GroupSplitChannels = true };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([left, right]);
        Assert.Equal("右", Position(panel, right.Name).SelectedText);
        panel.SetFound([right]);
        Assert.Equal("右", Position(panel, right.Name).SelectedText);
        Assert.Contains("两只都在线", panel.BlockReason);
        panel.SetFound([right, left]);
        panel.Apply();
        Assert.Equal(ChannelMode.RightOnly, GroupPlan.FromConfig(cfg)!.ChannelsFor(1, right.DeviceId));
    });

    [Fact]
    public void The_unified_editor_imports_the_latest_legacy_offset() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = A.DeviceId, DeviceName = A.Name, MultiRoomDeviceId = B.DeviceId,
            MultiRoomDeviceName = B.Name, GroupVolumeOffsets = new() { [GroupPlan.Key(B.DeviceId)] = -5 } };
        GroupSettings.Normalize(cfg);
        cfg.GroupVolumeOffsets![GroupPlan.Key(B.DeviceId)] = -9;
        using var panel = new GroupPanel(cfg);
        Assert.Equal(-9, All<FluentSlider>(panel).Single(s => s.AccessibleName == "书房 音量偏移").Value);
        panel.Apply();
        Assert.Equal(-9, GroupPlan.FromConfig(cfg)!.VolumeOffsetFor(B.DeviceId));
    });

    private static AirPlayDevice Device(string name, string id, string model = "AudioAccessory6,1") =>
        new(name, id, IPAddress.Parse("192.0.2.10"), 7000, model, new Dictionary<string, string>());
    private static readonly AirPlayDevice A = Device("客厅", "AA:01"), B = Device("书房", "AA:02"), Other = Device("接收端", "AA:03", "OtherSpeaker1,1");
    private static FluentComboBox Position(Control panel, string name) => All<FluentComboBox>(panel).Single(c => c.AccessibleName == name + " 位置");

    [Fact]
    public void File_edits_keep_live_positions_and_members_until_live_apply() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = A.DeviceId, DeviceName = A.Name, MultiRoomDeviceId = B.DeviceId, MultiRoomDeviceName = B.Name };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([A, B, Other]);
        panel.SetSource(true);
        Position(panel, A.Name).SelectedIndex = (int)SpeakerPosition.Center;
        All<FluentButton>(panel).Single(b => b.Text == "添加音箱").PerformClick();
        panel.Apply(activateCapture: false);
        Assert.Equal(SpeakerPosition.Center, cfg.GroupSpeakers![0].Position);
        Assert.Equal(ChannelMode.Stereo, GroupPlan.FromConfig(cfg)!.ChannelsFor(0));
        Assert.Equal([A.DeviceId, B.DeviceId], GroupPlan.FromConfig(cfg)!.Members.Select(s => s.DeviceId));
        panel.SetSource(false);
        Assert.Equal("完整立体声", Position(panel, A.Name).SelectedText);
        Position(panel, A.Name).SelectedIndex = 1;
        panel.Apply();
        Assert.Equal(3, GroupPlan.FromConfig(cfg)!.Members.Count);
        Assert.Equal(ChannelMode.LeftOnly, GroupPlan.FromConfig(cfg)!.ChannelsFor(0));
        panel.SetSource(true);
        Assert.Equal("中置", Position(panel, A.Name).SelectedText);
        Position(panel, A.Name).SelectedIndex = (int)SpeakerPosition.RearLeft;
        panel.Apply(activateCapture: false);
        Assert.Equal(ChannelMode.LeftOnly, GroupPlan.FromConfig(cfg)!.ChannelsFor(0));
    });

    [Fact]
    public void Rows_replace_legacy_split_and_swap_with_positions_delay_and_volume() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = A.DeviceId, DeviceName = A.Name, MultiRoomDeviceId = B.DeviceId,
            MultiRoomDeviceName = B.Name, GroupSplitChannels = true, GroupSwapChannels = true };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([A, B]);
        Assert.Equal("右", Position(panel, A.Name).SelectedText);
        Assert.Equal("左", Position(panel, B.Name).SelectedText);
        Assert.DoesNotContain(All<ToggleSwitch>(panel), t => t.Text is "电脑端分声道" or "交换左右");
        Position(panel, A.Name).SelectedIndex = 1;
        All<FluentSlider>(panel).Single(s => s.AccessibleName == "客厅 延迟（毫秒）").Value = 37;
        All<FluentSlider>(panel).Single(s => s.AccessibleName == "书房 音量偏移").Value = -6;
        panel.Apply();
        Assert.Equal(SpeakerPosition.Left, cfg.GroupCapturePositions![GroupPlan.Key(A.DeviceId)]);
        Assert.Equal(SpeakerPosition.Left, cfg.GroupCapturePositions![GroupPlan.Key(B.DeviceId)]);
        Assert.Equal(37, cfg.GroupSpeakers![0].DelayMs);
        Assert.Equal(-6, GroupPlan.FromConfig(cfg)!.VolumeOffsetFor(B.DeviceId));
        Assert.False(cfg.GroupSplitChannels); Assert.False(cfg.GroupSwapChannels);
    });

    [Fact]
    public void Capture_only_offers_stereo_positions_and_multichannel_restricts_receiver_kinds() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = A.DeviceId, GroupSpeakers = [
            new() { DeviceId = A.DeviceId, Name = A.Name, Position = SpeakerPosition.RearLeft },
            new() { DeviceId = Other.DeviceId, Name = Other.Name, Position = SpeakerPosition.Stereo } ] };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([A, Other]);
        Assert.Equal(["完整立体声", "左", "右"], Position(panel, A.Name).Items);
        panel.SetSource(true);
        Assert.Equal(9, Position(panel, A.Name).Items.Count);
        Assert.Equal("后左", Position(panel, A.Name).SelectedText); // source switching preserves the saved surround position
        Assert.Equal(["完整立体声"], Position(panel, Other.Name).Items);
        Assert.Equal("第三方 AirPlay 设备暂不支持多音箱分组。", panel.BlockReason);
        Assert.Contains(All<TextBlock>(panel), t => t.Text == panel.BlockReason);
        Assert.Contains(All<TextBlock>(panel), t => t.Text.Contains("同一种接收端才能共用延迟和时钟"));
        panel.SetSource(false);
        Assert.Equal(["完整立体声", "左", "右"], Position(panel, A.Name).Items);
        Assert.All(panel.SelectedLayout(), s => Assert.Equal(Audio.CinemaRole.Stereo, s.Role));
    });

    [Fact]
    public void Adding_and_removing_outputs_uses_the_same_group_and_returns_to_the_single_path() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = A.DeviceId, DeviceName = A.Name };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([A, B, Other]);
        Assert.Null(GroupPlan.FromConfig(cfg));
        Assert.False(Position(panel, A.Name).Enabled);
        Assert.All(All<FluentSlider>(panel), slider => Assert.False(slider.Enabled));
        All<FluentButton>(panel).Single(b => b.Text == "添加音箱").PerformClick();
        Assert.True(Position(panel, A.Name).Enabled);
        panel.Apply();
        Assert.Equal([A.DeviceId, B.DeviceId], GroupPlan.FromConfig(cfg)!.Members.Select(m => m.DeviceId));
        All<FluentButton>(panel).Single(b => b.AccessibleName == "移除 书房").PerformClick();
        panel.Apply();
        Assert.Null(cfg.MultiRoomDeviceId);
        Assert.Null(GroupPlan.FromConfig(cfg));
    });

    [Fact]
    public void Pair_keeps_the_experimental_leader_only_control_and_blocks_discrete_file_channels() => OnSta(() =>
    {
        var (left, right) = StereoPairTests.IdlePair();
        var cfg = new AppConfig { DeviceId = StereoPairs.PairId(StereoPairs.Tsid(left)!) };
        using var panel = new GroupPanel(cfg);
        panel.SetFound([right, left]);
        Assert.Equal("完整立体声", Position(panel, left.Name).SelectedText);
        Assert.Equal("完整立体声", Position(panel, right.Name).SelectedText);
        panel.SetSource(true);
        Assert.All(All<FluentComboBox>(panel).Where(c => c.AccessibleName?.EndsWith(" 位置") == true), c => Assert.Equal(["完整立体声"], c.Items));
        Assert.Null(panel.BlockReason);
        All<ToggleSwitch>(panel).Single(t => t.Text == "只连接主音箱（实验）").Checked = true;
        panel.Apply();
        Assert.True(GroupPlan.FromConfig(cfg)!.PairLeaderOnly);
        Assert.Contains(All<TextBlock>(panel), t => t.Text.Contains("另一只会不会一起响，需要实测"));
    });

    private static List<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(c => (c is T t ? new[] { t } : Array.Empty<T>()).Concat(All<T>(c))).ToList();
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
