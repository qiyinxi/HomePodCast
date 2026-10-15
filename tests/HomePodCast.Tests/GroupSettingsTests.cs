using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class GroupSettingsTests
{
    [Fact]
    public void Loading_and_resaving_keeps_file_and_capture_positions_independent()
    {
        string path = Path.Combine(Path.GetTempPath(), "hpc-group-" + Guid.NewGuid().ToString("N"), "config.json");
        try
        {
            var cfg = new AppConfig { DeviceId = "AA:01", MultiRoomDeviceId = "AA:02", Cinema = new() { Speakers = [
                new() { DeviceId = "AA:01", Role = CinemaRole.FrontLeft }, new() { DeviceId = "AA:02", Role = CinemaRole.FrontRight } ] } };
            cfg.Save(path);
            cfg = AppConfig.Load(path);
            Assert.Equal(ChannelMode.Stereo, GroupPlan.FromConfig(cfg)!.ChannelsFor(0));
            cfg.GroupCaptureDeviceIds = ["AA:01", "AA:02"];
            cfg.GroupCapturePositions = new() { [GroupPlan.Key("AA:01")] = SpeakerPosition.Left, [GroupPlan.Key("AA:02")] = SpeakerPosition.Right };
            cfg.GroupSpeakers![0].Position = SpeakerPosition.Center;
            cfg.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(SpeakerPosition.Center, loaded.GroupSpeakers![0].Position);
            Assert.Equal(ChannelMode.LeftOnly, GroupPlan.FromConfig(loaded)!.ChannelsFor(0));
            Assert.Equal(ChannelMode.RightOnly, GroupPlan.FromConfig(loaded)!.ChannelsFor(1));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Theory]
    [InlineData(false, ChannelMode.Stereo, ChannelMode.Stereo)]
    [InlineData(true, ChannelMode.LeftOnly, ChannelMode.RightOnly)]
    public void Cinema_positions_cannot_rewrite_live_capture_positions(bool split, ChannelMode a, ChannelMode b)
    {
        var cfg = new AppConfig { DeviceId = "AA:01", MultiRoomDeviceId = "AA:02", GroupSplitChannels = split,
            Cinema = new() { Speakers = [new() { DeviceId = "AA:01", Role = CinemaRole.FrontLeft },
                new() { DeviceId = "AA:02", Role = CinemaRole.FrontRight }] } };
        GroupSettings.Normalize(cfg);
        Assert.Equal([SpeakerPosition.Left, SpeakerPosition.Right], cfg.GroupSpeakers!.Select(s => s.Position));
        Assert.Equal(a, GroupPlan.FromConfig(cfg)!.ChannelsFor(0));
        Assert.Equal(b, GroupPlan.FromConfig(cfg)!.ChannelsFor(1));
        cfg.GroupSpeakers![0].Position = SpeakerPosition.Center; // a later file-only save
        Assert.Equal(a, GroupPlan.FromConfig(cfg)!.ChannelsFor(0));
        var restored = new AppConfig();
        UsageProfiles.Apply(restored, UsageProfiles.Capture(cfg));
        Assert.Equal(a, GroupPlan.FromConfig(restored)!.ChannelsFor(0));
        Assert.Equal(b, GroupPlan.FromConfig(restored)!.ChannelsFor(1));
    }

    [Fact]
    public void Saved_file_speakers_never_activate_or_expand_the_live_group()
    {
        var cfg = new AppConfig { DeviceId = "AA:01", Cinema = new() { Speakers = [
            new() { DeviceId = "AA:01", Role = CinemaRole.FrontLeft },
            new() { DeviceId = "AA:02", Role = CinemaRole.FrontRight },
            new() { DeviceId = "AA:03", Role = CinemaRole.Center } ] } };
        GroupSettings.Normalize(cfg);
        Assert.Equal(3, cfg.GroupSpeakers!.Count);
        Assert.Null(GroupPlan.FromConfig(cfg)); // startup keeps the original low-latency single session
        cfg.MultiRoomDeviceId = "AA:02";
        Assert.Equal(["AA:01", "AA:02"], GroupPlan.FromConfig(cfg)!.Members.Select(s => s.DeviceId));
        var saved = UsageProfiles.Capture(cfg);
        var restored = new AppConfig();
        UsageProfiles.Apply(restored, saved);
        Assert.Equal(["AA:01", "AA:02"], GroupPlan.FromConfig(restored)!.Members.Select(s => s.DeviceId));
        saved.MultiRoomDeviceId = null;
        UsageProfiles.Apply(restored, saved);
        Assert.Null(GroupPlan.FromConfig(restored));
        cfg.GroupCaptureDeviceIds = ["AA:01", "AA:03"];
        Assert.Equal(["AA:01", "AA:03"], GroupPlan.FromConfig(cfg)!.Members.Select(s => s.DeviceId));
        cfg.GroupCaptureDeviceIds = ["AA:01"];
        Assert.Null(GroupPlan.FromConfig(cfg));
    }

    [Theory]
    [InlineData(false, SpeakerPosition.Left, SpeakerPosition.Right)]
    [InlineData(true, SpeakerPosition.Right, SpeakerPosition.Left)]
    public void Old_split_and_swap_migrate_to_device_positions(bool swap, SpeakerPosition first, SpeakerPosition second)
    {
        var cfg = new AppConfig { DeviceId = "AA:01", MultiRoomDeviceId = "AA:02", GroupSplitChannels = true,
            GroupSwapChannels = swap, GroupVolumeOffsets = new() { [GroupPlan.Key("AA:02")] = -5 } };
        GroupSettings.Normalize(cfg);
        Assert.Equal([first, second], cfg.GroupSpeakers!.Select(s => s.Position));
        Assert.Equal(-5, cfg.GroupSpeakers![1].VolumeOffset);
        Assert.False(cfg.GroupSplitChannels); Assert.False(cfg.GroupSwapChannels);
        var plan = GroupPlan.FromConfig(cfg)!;
        Assert.Equal(swap ? ChannelMode.RightOnly : ChannelMode.LeftOnly, plan.ChannelsFor(0));
        cfg.GroupSpeakers[0].DelayMs = 42;
        Assert.Equal(42, GroupPlan.FromConfig(cfg)!.DelayTrimFor("aa-01"));
        GroupSettings.Normalize(cfg);
        Assert.Equal([first, second], cfg.GroupSpeakers.Select(s => s.Position));
    }

    [Fact]
    public void Pair_migration_waits_for_a_complete_scan_in_stable_member_order()
    {
        var (left, right) = StereoPairTests.IdlePair();
        var cfg = new AppConfig { DeviceId = StereoPairs.PairId(StereoPairs.Tsid(left)!), GroupSplitChannels = true, GroupSwapChannels = true };
        GroupSettings.Normalize(cfg);
        Assert.True(cfg.GroupSplitChannels);
        GroupSettings.Normalize(cfg, [right]);
        Assert.True(cfg.GroupSplitChannels);
        GroupSettings.Normalize(cfg, [right, left]);
        Assert.Equal([SpeakerPosition.Right, SpeakerPosition.Left], cfg.GroupSpeakers!.Select(s => s.Position));
        Assert.False(cfg.GroupSplitChannels);
        Assert.Equal(ChannelMode.RightOnly, GroupPlan.FromConfig(cfg)!.ChannelsFor(0, left.DeviceId));
    }

    [Fact]
    public void Cinema_layout_is_imported_once_and_single_speaker_path_stays_single()
    {
        var cfg = new AppConfig { DeviceId = "AA:01", Cinema = new() { Speakers = [
            new() { DeviceId = "AA:01", Role = CinemaRole.FrontLeft, DelayMs = 12 },
            new() { DeviceId = "AA:02", Role = CinemaRole.FrontRight, DelayMs = 30 } ] } };
        GroupSettings.Normalize(cfg);
        Assert.Equal([12, 30], cfg.GroupSpeakers!.Select(s => s.DelayMs));
        cfg.GroupSpeakers![1].Position = SpeakerPosition.Stereo;
        GroupSettings.Normalize(cfg);
        Assert.Equal(SpeakerPosition.Stereo, cfg.GroupSpeakers[1].Position);
        var single = new AppConfig { DeviceId = "AA:01" };
        GroupSettings.Normalize(single);
        Assert.Null(GroupPlan.FromConfig(single));
    }
}
