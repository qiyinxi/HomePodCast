using System.Net;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public sealed class MixerRoutingV2Tests
{
    [Theory]
    [InlineData(AudioRoute.HomePod, MixerOutputKind.Current, false)]
    [InlineData(AudioRoute.Local, MixerOutputKind.Local, false)]
    [InlineData(AudioRoute.Both, MixerOutputKind.Current, true)]
    public void Migration_is_in_memory_idempotent_and_keeps_legacy_choices(AudioRoute legacy, MixerOutputKind kind, bool local)
    {
        var c = new AppConfig { RouteDefault = legacy, AppRoutes = new() { [" Browser.EXE "] = legacy },
            AppSendVolumes = new() { ["browser"] = 37 }, AppSendVolumesOff = new() { ["browser"] = 62 },
            CaptureDeviceId = "capture", RoutingNoticeAccepted = true };
        MixerRoutes.Migrate(c);
        string first = c.MixerRouting!.Value.GetRawText();
        MixerRoutes.Migrate(c);
        Assert.Equal(first, c.MixerRouting.Value.GetRawText());
        var s = MixerRoutes.Read(c).Settings!;
        Assert.Equal(new MixerRoute(new MixerOutput(kind), local), s.Default);
        Assert.Equal(s.Default, s.Apps["browser"]);
        Assert.Equal(legacy, c.RouteDefault);
        Assert.Equal(37, c.AppSendVolumes["browser"]);
        Assert.Equal(62, c.AppSendVolumesOff["browser"]);
        Assert.Equal("capture", c.CaptureDeviceId);
        Assert.True(c.RoutingNoticeAccepted);
    }

    [Fact]
    public void Missing_routing_retains_the_endpoint_choice_without_choosing_a_speaker()
    {
        var c = new AppConfig();
        var s = MixerRoutes.Read(c).Settings!;
        Assert.Equal(MixerRoute.Current, s.Default);
        Assert.Empty(s.Apps);
        Assert.Null(c.MixerRouting);
        Assert.Equal(MixerRoutingError.NoSelection, MixerDestinationResolver.Resolve(c, s.Default).Error);
        Assert.Null(c.DeviceId);
    }

    [Theory]
    [InlineData("{\"Version\":97,\"Default\":{\"future\":true},\"secret\":[1,2]}", MixerRoutingError.UnknownVersion)]
    [InlineData("null", MixerRoutingError.InvalidConfig)]
    [InlineData("{}", MixerRoutingError.InvalidConfig)]
    [InlineData("{\"Version\":2,\"Default\":null}", MixerRoutingError.InvalidConfig)]
    [InlineData("{\"Version\":2,\"Apps\":null}", MixerRoutingError.InvalidConfig)]
    [InlineData("{\"Version\":2,\"Apps\":{\"x\":null}}", MixerRoutingError.InvalidConfig)]
    [InlineData("{\"Version\":2,\"Apps\":{\"x\":{\"Output\":{\"Kind\":\"Speaker\",\"Id\":\"bad\"}}}}", MixerRoutingError.InvalidConfig)]
    [InlineData("{\"Version\":2,\"Apps\":{\"x\":{},\"X.exe\":{}}}", MixerRoutingError.InvalidConfig)]
    [InlineData("{\"Version\":2,\"Version\":2}", MixerRoutingError.InvalidConfig)]
    public void Uneditable_sections_survive_roundtrip_and_refuse_overwrite(string raw, MixerRoutingError error)
    {
        var c = new AppConfig { MixerRouting = JsonDocument.Parse(raw).RootElement.Clone() };
        MixerRoutes.Migrate(c);
        Assert.Equal(error, MixerRoutes.Read(c).Error);
        Assert.Throws<InvalidOperationException>(() => MixerRoutes.Write(c, new()));
        var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(c))!;
        Assert.Equal(raw, saved.MixerRouting?.GetRawText() ?? "null");
    }

    [Fact]
    public void Oversized_routing_is_preserved_instead_of_truncated()
    {
        var s = new MixerRoutingSettings { Apps = Enumerable.Range(0, MixerRoutes.MaxApps + 1)
            .ToDictionary(i => "app" + i, _ => MixerRoute.Local) };
        var c = new AppConfig { MixerRouting = JsonSerializer.SerializeToElement(s) };
        Assert.Equal(MixerRoutingError.InvalidConfig, MixerRoutes.Read(c).Error);
        Assert.Equal(MixerRoutes.MaxApps + 1, c.MixerRouting.Value.GetProperty("Apps").EnumerateObject().Count());
    }

    [Fact]
    public void Current_named_and_Both_share_the_single_speaker_identity_and_are_detached()
    {
        var c = Speaker();
        string before = JsonSerializer.Serialize(c);
        var current = MixerDestinationResolver.Resolve(c, MixerRoute.Current);
        var named = MixerDestinationResolver.Resolve(c, Named("airplay:aa-bb-cc-dd-ee-ff") with { AlsoLocal = true });
        Assert.True(current.Available);
        Assert.Equal(current.Key, named.Key);
        Assert.Null(GroupPlan.FromConfig(named.Config!));
        Assert.Equal("127.0.0.1", named.Config!.Host);
        named.Config.LatencyMs = 999;
        Assert.Equal(before, JsonSerializer.Serialize(c));
    }

    [Fact]
    public void Presets_use_stable_ids_fresh_addresses_and_independent_delay()
    {
        var c = Speaker();
        var selection = OutputGroups.Capture(c);
        selection.Host = "192.0.2.1";
        var preset = OutputGroups.Save(c, "bedroom", selection, "saved");
        c.SelectedOutputGroupId = preset.Id;
        var route = new MixerRoute(new MixerOutput(MixerOutputKind.Group, preset.Id));
        MixerRoutes.Write(c, new() { Default = route, DestinationSettings = new()
            { ["group:saved"] = new(Scene.Game, 105) } });
        var named = MixerDestinationResolver.Resolve(c, route);
        Assert.Equal(named.Key, MixerDestinationResolver.Resolve(c, MixerRoute.Current).Key);
        Assert.Equal(105, named.Config!.LatencyMs);
        Assert.Equal(c.LatencyMs, MixerDestinationResolver.Resolve(c, MixerRoute.Current).Config!.LatencyMs);
        Assert.Equal("127.0.0.1", named.Config.Host);
        preset.Name = "renamed";
        Assert.Equal(named.Key, MixerDestinationResolver.Resolve(c, route).Key);
        c.SavedOutputGroups.Clear();
        Assert.Equal(MixerRoutingError.MissingDestination, MixerDestinationResolver.Resolve(c, route).Error);
        Assert.Equal(route, MixerRoutes.Read(c).Settings!.Default);
    }

    [Fact]
    public void Offline_speaker_remains_resolvable_and_deleted_speaker_is_not_replaced()
    {
        var c = Speaker();
        c.DeviceId = c.DeviceName = c.Host = null;
        var destination = MixerDestinationResolver.Resolve(c, Named("airplay:AABBCCDDEEFF"));
        Assert.True(destination.Available);
        Assert.Null(destination.Config!.Host);
        c.GroupSpeakers!.Clear();
        Assert.Equal(MixerRoutingError.MissingDestination, MixerDestinationResolver.Resolve(c, Named("airplay:AABBCCDDEEFF")).Error);
    }

    [Fact]
    public void Current_anonymous_group_does_not_create_saved_groups()
    {
        var c = Speaker();
        c.MultiRoomDeviceId = "112233445566";
        c.MultiRoomDeviceName = "other";
        var first = MixerDestinationResolver.Resolve(c, MixerRoute.Current);
        c.MultiRoomHost = "192.0.2.9";
        Assert.Equal(first.Key, MixerDestinationResolver.Resolve(c, MixerRoute.Current).Key);
        Assert.Empty(c.SavedOutputGroups);
    }

    [Fact]
    public void Actual_load_does_not_save_and_normal_save_roundtrips_all_rules()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mixer-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "config.json");
            var c = Speaker();
            c.AppRoutes["game"] = AudioRoute.Both;
            c.Save(path);
            string original = File.ReadAllText(path);
            c = AppConfig.Load(path);
            Assert.Equal(original, File.ReadAllText(path));
            var s = MixerRoutes.Read(c).Settings!;
            s.Apps["music"] = Named("airplay:AABBCCDDEEFF");
            MixerRoutes.Write(c, s);
            c.Save(path);
            var reload = AppConfig.Load(path);
            Assert.Equal(s.Apps["music"], MixerRoutes.Read(reload).Settings!.Apps["music"]);
            Assert.Equal(AudioRoute.Both, reload.AppRoutes["game"]);
            Assert.False(reload.AppRoutes.ContainsKey("music"));
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static MixerRoute Named(string id) => new(new MixerOutput(MixerOutputKind.Speaker, id));
    internal static AppConfig Speaker() => new()
    {
        DeviceId = "AA:BB:CC:DD:EE:FF", DeviceName = "bedroom", Host = IPAddress.Loopback.ToString(),
        AutoConnect = false, GroupSpeakers = [new() { DeviceId = "AABBCCDDEEFF", Name = "bedroom", Host = "192.0.2.1" }],
    };
}
