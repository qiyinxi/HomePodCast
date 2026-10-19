using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class OutputGroupSettingsTests
{
    private static AppConfig Mixed() => new()
    {
        DeviceId = "AA:BB", DeviceName = "HomePod", GroupCaptureDeviceIds = ["AA:BB"],
        GroupSpeakers = [new() { DeviceId = "AA:BB", Name = "HomePod", Position = SpeakerPosition.Left, DelayMs = 30 }],
        GroupCapturePositions = new() { ["AABB"] = SpeakerPosition.Right },
        GroupCaptureDelayTrims = new() { ["AABB"] = -270 }, GroupRequiredOutputs = new() { ["AABB"] = false },
        GroupDelayMs = 4500,
        GroupLocalOutputs = [new("{endpoint-1}", "Bluetooth") { IsBluetooth = true, ExtraLatencyMs = 187,
            TrimMs = 14, Channels = ChannelMode.LeftOnly, VolumeOffset = -4 }],
        GroupCaptureLocalDeviceIds = ["{endpoint-1}"],
        GroupDlnaOutputs = [new("uuid:renderer-1", "Projector") { EstimatedLatencyMs = 3100, TrimMs = -60,
            Required = true, PreferredFormat = "aac", Framing = "length" }],
        GroupCaptureDlnaUdns = ["uuid:renderer-1"],
    };

    [Fact]
    public void Saving_and_overwriting_a_named_group_never_changes_the_active_selection()
    {
        var c = Mixed();
        c.SelectedOutputGroupId = "active";
        var snapshot = OutputGroups.Capture(c);
        var preset = OutputGroups.Save(c, "  Cinema  ", snapshot);
        snapshot.GroupSpeakers![0].Name = "changed";
        snapshot.GroupCaptureLocalDeviceIds!.Clear();
        snapshot.GroupCaptureDelayTrims["AABB"] = 900;
        Assert.Equal("Cinema", preset.Name);
        Assert.Equal("HomePod", preset.Settings.GroupSpeakers![0].Name);
        Assert.Single(preset.Settings.GroupCaptureLocalDeviceIds!);
        Assert.Equal(-270, preset.Settings.GroupCaptureDelayTrims["AABB"]);
        Assert.Equal("active", c.SelectedOutputGroupId);
        Assert.Equal("AA:BB", c.DeviceId);
        Assert.Equal(-270, c.GroupCaptureDelayTrims["AABB"]);
        var replacement = OutputGroups.Save(c, "Renamed", new OutputGroupSettings { GroupCaptureDeviceIds = [] }, preset.Id);
        Assert.Equal(preset.Id, Assert.Single(c.SavedOutputGroups).Id);
        Assert.Equal("Renamed", replacement.Name);
        Assert.Equal("AA:BB", c.DeviceId);
        Assert.Single(c.GroupCaptureLocalDeviceIds!);
    }

    [Fact]
    public void Applying_changes_outputs_only_and_preserves_the_file_layout_and_unselected_preferences()
    {
        var saved = OutputGroups.Capture(Mixed());
        saved.Host = "192.0.2.99";
        var target = new AppConfig { DeviceId = "different", Host = "192.0.2.2", AutoConnect = false,
            CaptureDeviceId = "capture", Language = "ja", LatencyMs = 172, VlcPasswordProtected = "credential",
            GroupSpeakers = [new() { DeviceId = "AA-BB", Name = "old", Position = SpeakerPosition.RearLeft, DelayMs = 123 }],
            GroupLocalOutputs = [new("other-endpoint", "USB") { ExtraLatencyMs = 9 }] };
        target.Effects.MicGainDb = 6;
        var effects = target.Effects;
        OutputGroups.Apply(target, saved);
        Assert.Equal("AA:BB", target.DeviceId);
        Assert.Null(target.Host); // the saved DHCP address is never trusted
        Assert.Equal(SpeakerPosition.RearLeft, target.GroupSpeakers![0].Position);
        Assert.Equal(123, target.GroupSpeakers[0].DelayMs);
        Assert.Equal(SpeakerPosition.Right, target.GroupCapturePositions!["AABB"]);
        Assert.Equal(-270, target.GroupCaptureDelayTrims["AABB"]);
        Assert.Equal(2, target.GroupLocalOutputs.Count);
        Assert.False(target.AutoConnect);
        Assert.Equal("capture", target.CaptureDeviceId);
        Assert.Equal("ja", target.Language);
        Assert.Equal(172, target.LatencyMs);
        Assert.Equal("credential", target.VlcPasswordProtected);
        Assert.Same(effects, target.Effects);
        Assert.Equal(6, target.Effects.MicGainDb);
        target.GroupSpeakers[0].Name = "another name";
        target.GroupCapturePositions.Clear(); target.GroupCaptureDlnaUdns!.Clear();
        Assert.Equal("HomePod", saved.GroupSpeakers![0].Name);
        Assert.Single(saved.GroupCapturePositions!); Assert.Single(saved.GroupCaptureDlnaUdns!);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(saved));
        foreach (string excluded in new[] { "CaptureDeviceId", "CaptureDeviceName", "Effects", "VlcPasswordProtected",
                     "RaopPasswordsProtected", "AutoConnect", "Language", "LatencyMs" })
            Assert.False(json.RootElement.TryGetProperty(excluded, out _), $"Output groups must not capture {excluded}");
    }

    [Fact]
    public void Applying_synchronizes_the_primary_for_the_single_speaker_path_and_respects_auxiliary_only_choices()
    {
        var target = new AppConfig { DeviceId = "remembered", DeviceName = "Old", Host = "192.0.2.1" };
        OutputGroups.Apply(target, new() { DeviceId = "remembered", GroupCaptureDeviceIds = ["AABB"],
            GroupSpeakers = [new() { DeviceId = "AABB", Name = "Chosen" }] });
        Assert.Equal("AABB", target.DeviceId);
        Assert.Equal("Chosen", target.DeviceName);
        Assert.Null(target.Host);
        Assert.Null(GroupPlan.FromConfig(target));
        OutputGroups.Apply(target, new() { GroupCaptureDeviceIds = [],
            GroupLocalOutputs = [new("endpoint", "USB")], GroupCaptureLocalDeviceIds = ["endpoint"] });
        Assert.Null(target.DeviceId);
        Assert.Empty(GroupPlan.FromConfig(target)!.Members);
        Assert.Single(GroupPlan.FromConfig(target)!.LocalOutputs);
    }

    [Fact]
    public void Preference_banks_do_not_select_outputs_and_bluetooth_classification_keeps_measured_latency()
    {
        var c = JsonSerializer.Deserialize<AppConfig>("""{"DeviceId":"AABB"}""")!;
        Assert.Null(c.GroupCaptureLocalDeviceIds);
        Assert.Null(GroupPlan.FromConfig(c));
        c.GroupLocalOutputs = [new("endpoint", "Speaker") { ExtraLatencyMs = 173 }];
        c.GroupLocalOutputs[0] = c.GroupLocalOutputs[0] with { IsBluetooth = true };
        OutputGroups.Normalize(c);
        Assert.Equal(173, Assert.Single(c.GroupLocalOutputs).ExtraLatencyMs);
        Assert.Null(GroupPlan.FromConfig(c));
        Assert.Empty(LocalGroupSettings.Selected(c));
    }

    [Fact]
    public void Normalization_preserves_endpoint_and_UDN_identity_and_bounds_live_settings()
    {
        var c = new AppConfig { GroupLocalOutputs = [new("end-point", "One"), new("endpoint", "Two")],
            GroupCaptureLocalDeviceIds = ["end-point", "END-POINT", "endpoint"],
            GroupDlnaOutputs = [new("uuid:a-b", "One"), new("uuid:ab", "Two")],
            GroupCaptureDlnaUdns = ["UUID:A-B", "uuid:ab"],
            GroupRequiredOutputs = new() { ["local:end-point"] = true, ["local:endpoint"] = false,
                ["dlna:uuid:a-b"] = true, ["dlna:uuid:ab"] = false, ["aa:bb"] = false },
            GroupCaptureDelayTrims = new() { ["aa:bb"] = int.MinValue }, GroupDelayMs = int.MaxValue };
        OutputGroups.Normalize(c);
        Assert.Equal(2, LocalGroupSettings.Selected(c).Count);
        Assert.Equal(2, c.GroupCaptureDlnaUdns!.Count);
        Assert.Equal(5, c.GroupRequiredOutputs.Count);
        Assert.Equal(-5000, c.GroupCaptureDelayTrims["AABB"]);
        Assert.Equal(30000, c.GroupDelayMs);
        c.GroupDelayMs = -100;
        OutputGroups.Normalize(c);
        Assert.Equal(100, c.GroupDelayMs);
        c.GroupDelayMs = null;
        OutputGroups.Normalize(c);
        Assert.Null(c.GroupDelayMs);
    }

    [Fact]
    public void Named_groups_and_all_output_settings_round_trip_without_activation()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HomePodCast-output-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = Mixed();
            var saved = OutputGroups.Save(c, "Cinema", OutputGroups.Capture(c));
            c.SelectedOutputGroupId = saved.Id;
            c.GroupCaptureLocalDeviceIds = null; c.GroupCaptureDlnaUdns = null;
            string path = Path.Combine(folder, "config.json");
            c.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(saved.Id, loaded.SelectedOutputGroupId);
            Assert.Null(GroupPlan.FromConfig(loaded));
            var settings = Assert.Single(loaded.SavedOutputGroups).Settings;
            Assert.True(Assert.Single(settings.GroupLocalOutputs).IsBluetooth);
            Assert.Equal(187, settings.GroupLocalOutputs[0].ExtraLatencyMs);
            Assert.Equal("aac", Assert.Single(settings.GroupDlnaOutputs).PreferredFormat);
            Assert.Equal(4500, settings.GroupDelayMs);
            OutputGroups.Apply(loaded, settings);
            var plan = GroupPlan.FromConfig(loaded)!;
            Assert.Single(plan.LocalOutputs); Assert.Single(plan.DlnaOutputs);
            Assert.False(plan.RequiredOutputs["AABB"]);
            Assert.Equal(-270, plan.DelayTrimFor("AA-BB"));
            Assert.True(OutputGroups.Remove(loaded, saved.Id));
            Assert.Null(loaded.SelectedOutputGroupId);
            Assert.Single(loaded.GroupCaptureLocalDeviceIds!);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
