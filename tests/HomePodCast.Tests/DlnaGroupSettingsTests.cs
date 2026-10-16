using System.Text.Json;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaGroupSettingsTests
{
    private static DlnaOutputSettings Output(string udn = "uuid:renderer") => new(udn, "Renderer");

    [Fact]
    public void Explicit_DLNA_only_selection_also_excludes_a_remembered_stereo_pair()
    {
        var config = new AppConfig { DeviceId = StereoPairs.PairId("remembered"), GroupCaptureDeviceIds = [],
            GroupDlnaOutputs = [Output()], GroupCaptureDlnaUdns = ["uuid:renderer"] };
        var plan = Assert.IsType<GroupPlan>(GroupPlan.FromConfig(config));
        Assert.Equal(GroupKind.MultiRoom, plan.Kind);
        Assert.Null(plan.PairTsid);
        Assert.Empty(plan.Members);
        Assert.Equal("dlna:UUID:RENDERER", OwnSession.KeyOf(plan));
    }

    [Fact]
    public void Preference_bank_does_not_activate_a_group_or_change_plain_single_path()
    {
        var config = new AppConfig { DeviceId = "AABBCCDDEEFF", DeviceName = "HomePod", GroupDlnaOutputs = [Output()] };
        Assert.Null(GroupPlan.FromConfig(config));
        Assert.Equal("AABBCCDDEEFF", OwnSession.KeyOf(config));
    }

    [Fact]
    public void Explicit_DLNA_only_selection_does_not_connect_the_remembered_AirPlay_primary()
    {
        var config = new AppConfig { DeviceId = "AABBCCDDEEFF", GroupCaptureDeviceIds = [],
            GroupDlnaOutputs = [Output()], GroupCaptureDlnaUdns = ["UUID:RENDERER"] };
        var plan = Assert.IsType<GroupPlan>(GroupPlan.FromConfig(config));
        Assert.Empty(plan.Members);
        Assert.Single(plan.DlnaOutputs);
        Assert.Equal("Renderer", plan.Name);
        Assert.Equal("dlna:UUID:RENDERER", OwnSession.KeyOf(plan));
    }

    [Fact]
    public void One_AirPlay_plus_DLNA_forms_a_group_and_preserves_each_identity()
    {
        var config = new AppConfig { DeviceId = "AABBCCDDEEFF", DeviceName = "HomePod", Host = "192.0.2.1",
            GroupDlnaOutputs = [Output()], GroupCaptureDlnaUdns = ["uuid:renderer"] };
        var plan = Assert.IsType<GroupPlan>(GroupPlan.FromConfig(config));
        Assert.Equal("AABBCCDDEEFF", Assert.Single(plan.Members).DeviceId);
        Assert.Single(plan.DlnaOutputs);
        Assert.Equal("HomePod + Renderer", plan.Name);
        Assert.Equal("AABBCCDDEEFF+dlna:UUID:RENDERER", OwnSession.KeyOf(plan));
    }

    [Fact]
    public void UDN_selection_needs_no_AirPlay_primary_and_different_renderers_get_different_keys()
    {
        var config = new AppConfig { GroupDlnaOutputs = [Output(), Output("uuid:other")], GroupCaptureDlnaUdns = ["uuid:renderer"] };
        var first = Assert.IsType<GroupPlan>(GroupPlan.FromConfig(config));
        config.GroupCaptureDlnaUdns = ["uuid:other"];
        var second = Assert.IsType<GroupPlan>(GroupPlan.FromConfig(config));
        Assert.NotEqual(OwnSession.KeyOf(first), OwnSession.KeyOf(second));
        Assert.Equal("dlna:UUID:RENDERER", OwnSession.KeyOf(first));
    }

    [Fact]
    public void Preferences_are_bounded_and_do_not_persist_live_discovery_metadata()
    {
        var outputs = DlnaGroupSettings.Copy([Output() with { EstimatedLatencyMs = 99999, TrimMs = -99999,
            VolumeOffset = 999, Channels = (ChannelMode)999, PreferredFormat = "bad", Framing = "bad" },
            Output("UUID:RENDERER"), Output("")]);
        var value = Assert.Single(outputs);
        Assert.Equal(20000, value.EstimatedLatencyMs);
        Assert.Equal(-5000, value.TrimMs);
        Assert.Equal(100, value.VolumeOffset);
        Assert.Equal(ChannelMode.Stereo, value.Channels);
        Assert.Null(value.PreferredFormat);
        Assert.Equal("chunked", value.Framing);
        string json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("ControlUri", json);
        Assert.DoesNotContain("Address", json);
        Assert.DoesNotContain("TakeOver", json);
    }

    [Fact]
    public void Usage_profile_copies_DLNA_only_selection_and_restores_preferences_without_aliasing()
    {
        var source = new AppConfig { GroupDlnaOutputs = [Output() with { TrimMs = -37, Required = true }],
            GroupCaptureDlnaUdns = ["uuid:renderer"], GroupCaptureDeviceIds = [] };
        var saved = UsageProfiles.Capture(source);
        source.GroupDlnaOutputs.Clear(); source.GroupCaptureDlnaUdns.Clear();
        var target = new AppConfig { DeviceId = "AABBCCDDEEFF" };
        UsageProfiles.Apply(target, saved);
        Assert.Null(target.DeviceId);
        Assert.Equal(-37, Assert.Single(target.GroupDlnaOutputs).TrimMs);
        Assert.True(Assert.Single(target.GroupDlnaOutputs).Required);
        Assert.Single(Assert.IsType<GroupPlan>(GroupPlan.FromConfig(target)).DlnaOutputs);
        target.GroupCaptureDlnaUdns!.Clear();
        Assert.Single(saved.GroupCaptureDlnaUdns!);
    }
}
