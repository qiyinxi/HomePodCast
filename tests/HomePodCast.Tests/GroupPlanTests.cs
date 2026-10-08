using System.Text.Json;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class GroupPlanTests
{
    private const string Tsid = "6A1B2C3D-0000-4000-8000-0123456789AB";

    [Fact]
    public void An_existing_single_speaker_config_stays_on_the_single_speaker_path()
    {
        // config.json as written before groups existed
        var cfg = JsonSerializer.Deserialize<AppConfig>("""
            { "DeviceId": "02:DC:8E:68:21:FB", "DeviceName": "卧室", "Host": "192.168.50.226",
              "LatencyMs": 105, "Volume": 35, "AutoConnect": true }
            """)!;
        Assert.Null(GroupPlan.FromConfig(cfg));
        Assert.False(cfg.GroupSplitChannels);
        Assert.Null(cfg.MultiRoomDeviceId);

        Assert.Null(GroupPlan.FromConfig(new AppConfig()));
    }

    [Fact]
    public void A_pair_id_selects_the_stereo_pair()
    {
        var plan = GroupPlan.FromConfig(new AppConfig { DeviceId = StereoPairs.PairId(Tsid), DeviceName = "客厅" })!;
        Assert.Equal(GroupKind.StereoPair, plan.Kind);
        Assert.Equal(Tsid, plan.PairTsid);
        Assert.Equal("客厅", plan.Name);
        Assert.Empty(plan.Members); // found by tsid when connecting

        // A multi-room extra is ignored while a pair is selected.
        var withExtra = new AppConfig { DeviceId = StereoPairs.PairId(Tsid), MultiRoomDeviceId = "AA:00:00:00:00:09" };
        Assert.Equal(GroupKind.StereoPair, GroupPlan.FromConfig(withExtra)!.Kind);
    }

    [Fact]
    public void A_second_speaker_makes_a_multi_room_group_unless_it_is_the_same_one()
    {
        var cfg = new AppConfig
        {
            DeviceId = "02:DC:8E:68:21:FB", DeviceName = "卧室", Host = "192.168.50.226",
            MultiRoomDeviceId = "AA:00:00:00:00:09", MultiRoomDeviceName = "书房", MultiRoomHost = "192.168.50.30",
        };
        var plan = GroupPlan.FromConfig(cfg)!;
        Assert.Equal(GroupKind.MultiRoom, plan.Kind);
        Assert.Equal(["02:DC:8E:68:21:FB", "AA:00:00:00:00:09"], plan.Members.Select(m => m.DeviceId));
        Assert.Equal("192.168.50.30", plan.Members[1].Host);

        cfg.MultiRoomDeviceId = "02-dc-8e-68-21-fb"; // same speaker, other spelling
        Assert.Null(GroupPlan.FromConfig(cfg));
        cfg.MultiRoomDeviceId = StereoPairs.PairId(Tsid); // a pair can't be the extra speaker
        Assert.Null(GroupPlan.FromConfig(cfg));
    }

    [Theory]
    [InlineData(false, false, ChannelMode.Stereo, ChannelMode.Stereo)]
    [InlineData(false, true, ChannelMode.Stereo, ChannelMode.Stereo)]
    [InlineData(true, false, ChannelMode.LeftOnly, ChannelMode.RightOnly)]
    [InlineData(true, true, ChannelMode.RightOnly, ChannelMode.LeftOnly)]
    public void Channels_are_full_stereo_by_default_and_split_or_swapped_on_request(bool split, bool swap,
        ChannelMode first, ChannelMode second)
    {
        var plan = GroupPlan.FromConfig(new AppConfig
        {
            DeviceId = StereoPairs.PairId(Tsid), GroupSplitChannels = split, GroupSwapChannels = swap,
        })!;
        Assert.Equal(first, plan.ChannelsFor(0));
        Assert.Equal(second, plan.ChannelsFor(1));
    }

    [Fact]
    public void Volume_offsets_are_looked_up_by_device_id_in_any_spelling()
    {
        var plan = GroupPlan.FromConfig(new AppConfig
        {
            DeviceId = StereoPairs.PairId(Tsid),
            GroupVolumeOffsets = new() { [GroupPlan.Key("aa:00:00:00:00:01")] = -5 },
        })!;
        Assert.Equal(-5, plan.VolumeOffsetFor("AA-00-00-00-00-01"));
        Assert.Equal(0, plan.VolumeOffsetFor("AA:00:00:00:00:02"));
    }
}
