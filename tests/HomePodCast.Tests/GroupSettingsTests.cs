using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class GroupSettingsTests
{
    [Theory]
    [InlineData(false, SpeakerPosition.Left, SpeakerPosition.Right)]
    [InlineData(true, SpeakerPosition.Right, SpeakerPosition.Left)]
    public void Old_split_and_swap_migrate_to_device_positions(bool swap, SpeakerPosition first, SpeakerPosition second)
    {
        var cfg = new AppConfig { DeviceId = "AA:01", MultiRoomDeviceId = "AA:02", GroupSplitChannels = true,
            GroupSwapChannels = swap, GroupVolumeOffsets = new() { [GroupPlan.Key("AA:02")] = -5 } };
        GroupSettings.Normalize(cfg);
        Assert.Equal([first, second], cfg.GroupSpeakers!.Select(s => s.Position));
        Assert.Equal(-5, cfg.GroupSpeakers[1].VolumeOffset);
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
        cfg.GroupSpeakers[1].Position = SpeakerPosition.Stereo;
        GroupSettings.Normalize(cfg);
        Assert.Equal(SpeakerPosition.Stereo, cfg.GroupSpeakers[1].Position);
        var single = new AppConfig { DeviceId = "AA:01" };
        GroupSettings.Normalize(single);
        Assert.Null(GroupPlan.FromConfig(single));
    }
}
