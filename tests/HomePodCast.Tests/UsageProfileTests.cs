using System.Net;
using System.Reflection;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;

namespace HomePodCast.Tests;

public class UsageProfileTests
{
    [Fact]
    public void Saved_choices_are_independent_snapshots_and_keep_runtime_effects_object()
    {
        var c = new AppConfig { DeviceId = "speaker", AppRoutes = new() { ["game"] = AudioRoute.Both },
            AppSendVolumes = new() { ["game"] = 100 } };
        c.Effects.OutputEq.Gains[0] = 6;
        var saved = UsageProfiles.Capture(c);
        c.AppRoutes["game"] = AudioRoute.Local;
        c.Effects.OutputEq.Gains[0] = -6;
        var engineSettings = c.Effects;
        UsageProfiles.Apply(c, saved);
        Assert.Same(engineSettings, c.Effects);
        Assert.Equal(AudioRoute.Both, c.AppRoutes["game"]);
        Assert.Equal(100, c.AppSendVolumes["game"]);
        Assert.Equal(6, c.Effects.OutputEq.Gains[0]);
        c.Effects.OutputEq.Gains[0] = 1;
        Assert.Equal(6, saved.Effects.OutputEq.Gains[0]);
        c.AppSendVolumes.Remove("game");
        Assert.Equal(100, saved.AppSendVolumes["game"]);
    }

    [Fact]
    public void Profiles_do_not_copy_credentials_startup_updates_or_global_calibration()
    {
        var source = new AppConfig { Language = "ja", Theme = "dark", AutoConnect = true, VlcPasswordProtected = "secret",
            AutoUpdateCheck = true, LocalApiPort = 9876, VideoDelayExtraMs = 57, MeasuredAvOffsetMs = 189 };
        var target = new AppConfig { Language = "en", Theme = "light", AutoConnect = false, VlcPasswordProtected = "keep",
            AutoUpdateCheck = false, LocalApiPort = 1234, VideoDelayExtraMs = 36 };
        var profile = UsageProfiles.Capture(source);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(profile));
        UsageProfiles.Apply(target, profile);
        Assert.Equal("keep", target.VlcPasswordProtected);
        Assert.Equal("en", target.Language);
        Assert.Equal("light", target.Theme);
        Assert.False(target.AutoConnect);
        Assert.False(target.AutoUpdateCheck);
        Assert.Equal(1234, target.LocalApiPort);
        Assert.Equal(36, target.VideoDelayExtraMs);
        Assert.Null(target.MeasuredAvOffsetMs);
    }

    [Fact]
    public void Applying_caps_never_increases_volume_and_another_speaker_plays_at_its_own_volume()
    {
        var c = new AppConfig { DeviceId = "one", Volume = 70, ArrivalToRenderMs = 85 };
        UsageProfiles.Apply(c, new UsageSettings { DeviceId = "one", VolumeCapPercent = 30, Scene = Scene.Game });
        Assert.Equal(30, c.Volume);
        Assert.Equal(Scenes.GameMs, c.LatencyMs);
        UsageProfiles.Apply(c, new UsageSettings { DeviceId = "two", VolumeCapPercent = 80 });
        Assert.Null(c.Volume);
        // Kept as when another speaker is picked in the list (TrayApp.SelectDevice): the next connection measures it again.
        Assert.Equal(85, c.ArrivalToRenderMs);
    }

    [Fact]
    public void Loading_profiles_normalizes_nulls_bad_values_duplicates_and_names()
    {
        var c = new AppConfig { UsageProfiles = [
            new() { Id = "id", Name = "  gaming  ", Settings = new() { VolumeCapPercent = 999, LatencyMs = -1, CustomLatencyMs = 9999,
                AppRoutes = null!, AppSendVolumes = new() { ["Game.EXE"] = 100, [""] = 3 }, Effects = null! } },
            new() { Id = "ID", Name = "duplicate" }, new() { Id = "x", Name = " " }, null! ] };
        UsageProfiles.Normalize(c);
        var p = Assert.Single(c.UsageProfiles);
        Assert.Equal("gaming", p.Name);
        Assert.Equal(100, p.Settings.VolumeCapPercent);
        Assert.Equal(0, p.Settings.LatencyMs);
        Assert.Equal(LatencyTuner.MaxMs, p.Settings.CustomLatencyMs);
        Assert.Equal(100, p.Settings.AppSendVolumes["game"]);
        Assert.NotNull(p.Settings.Effects);
    }

    [Fact]
    public void A_hand_tuned_latency_below_100_ms_is_restored_as_saved()
    {
        // The speaker's floor (its processing time plus the margin) applies when connecting, as for a value set on 首页.
        var c = new AppConfig { DeviceId = "one", Scene = Scene.Recommended, LatencyMs = Scenes.RecommendedMs };
        UsageProfiles.Apply(c, new UsageSettings { DeviceId = "one", Scene = Scene.Custom, LatencyMs = 95, CustomLatencyMs = 95 });
        Assert.Equal((Scene.Custom, 95, 95), (c.Scene, c.LatencyMs, c.CustomLatencyMs));
    }

    private static AirPlayDevice Found(string id, string address) =>
        new("HomePod", id, IPAddress.Parse(address), 7000, "AudioAccessory6,1", new Dictionary<string, string>());

    [Fact]
    public void The_same_speaker_keeps_its_current_address_never_the_one_saved_in_the_profile()
    {
        // Since the profile was saved, DHCP gave .10 to another device: anything answering on port 7000 there would be taken.
        var c = new AppConfig { DeviceId = "AA:BB:CC:DD:EE:01", Host = "192.168.1.20" };
        var saved = new UsageSettings { DeviceId = "aabbccddee01", Host = "192.168.1.10" };
        UsageProfiles.Apply(c, saved, [Found("AA:BB:CC:DD:EE:02", "192.168.1.10")]);
        Assert.Equal(("aabbccddee01", "192.168.1.20"), (c.DeviceId, c.Host));
    }

    [Fact]
    public void Another_speaker_starts_from_the_last_scan_or_from_mdns_never_the_saved_address()
    {
        var kitchen = new UsageSettings { DeviceId = "AA:BB:CC:DD:EE:02", Host = "192.168.1.10" };
        var c = new AppConfig { DeviceId = "AA:BB:CC:DD:EE:01", Host = "192.168.1.20" };
        UsageProfiles.Apply(c, kitchen, [Found("AA:BB:CC:DD:EE:01", "192.168.1.20"), Found("aa-bb-cc-dd-ee-02", "192.168.1.44")]);
        Assert.Equal(("AA:BB:CC:DD:EE:02", "192.168.1.44"), (c.DeviceId, c.Host));

        // Not in the last scan, or only as the list's 「未发现」 entry: no address, so connecting finds it by its id (mDNS).
        foreach (var scan in new IReadOnlyList<AirPlayDevice>?[] { null, [], [Found("AA:BB:CC:DD:EE:02", "255.255.255.255")] })
        {
            c = new AppConfig { DeviceId = "AA:BB:CC:DD:EE:01", Host = "192.168.1.20" };
            UsageProfiles.Apply(c, kitchen, scan);
            Assert.Equal("AA:BB:CC:DD:EE:02", c.DeviceId);
            Assert.Null(c.Host);
        }
        Assert.Equal("192.168.1.10", kitchen.Host); // the profile itself is left as saved
    }

    [Fact]
    public void Multi_room_speakers_follow_the_same_address_rule_even_when_they_swap_roles()
    {
        var c = new AppConfig { DeviceId = "A1", Host = "10.0.0.1", MultiRoomDeviceId = "B2", MultiRoomHost = "10.0.0.2" };
        UsageProfiles.Apply(c, new UsageSettings { DeviceId = "B2", Host = "10.0.0.99", MultiRoomDeviceId = "A1", MultiRoomHost = "10.0.0.98" });
        Assert.Equal(("B2", "10.0.0.2"), (c.DeviceId, c.Host));
        Assert.Equal(("A1", "10.0.0.1"), (c.MultiRoomDeviceId, c.MultiRoomHost));

        var third = new UsageSettings { DeviceId = "B2", MultiRoomDeviceId = "C3", MultiRoomHost = "10.0.0.97" };
        UsageProfiles.Apply(c, third, [Found("C3", "10.0.0.3")]);
        Assert.Equal(("B2", "10.0.0.2"), (c.DeviceId, c.Host));
        Assert.Equal(("C3", "10.0.0.3"), (c.MultiRoomDeviceId, c.MultiRoomHost));

        c.MultiRoomDeviceId = "A1";
        c.MultiRoomHost = "10.0.0.1";
        UsageProfiles.Apply(c, third); // C3 not in a scan: found by its id when connecting
        Assert.Equal("C3", c.MultiRoomDeviceId);
        Assert.Null(c.MultiRoomHost);
    }

    [Fact]
    public void A_profile_saved_before_any_speaker_was_chosen_keeps_the_current_speaker_and_group()
    {
        var noSpeaker = UsageProfiles.Capture(new AppConfig { NightMode = true, VolumeCapPercent = 40 });
        Assert.Null(noSpeaker.DeviceId);
        var c = new AppConfig { DeviceId = "A1", DeviceName = "卧室", Host = "10.0.0.1", Volume = 60, ArrivalToRenderMs = 85,
            MultiRoomDeviceId = "B2", MultiRoomDeviceName = "书房", MultiRoomHost = "10.0.0.2", GroupSplitChannels = true,
            GroupVolumeOffsets = new() { ["B2"] = -5 } };
        UsageProfiles.Apply(c, noSpeaker);
        Assert.Equal(("A1", "卧室", "10.0.0.1"), (c.DeviceId, c.DeviceName, c.Host));
        Assert.Equal(("B2", "书房", "10.0.0.2"), (c.MultiRoomDeviceId, c.MultiRoomDeviceName, c.MultiRoomHost));
        Assert.True(c.GroupSplitChannels);
        Assert.Equal(-5, c.GroupVolumeOffsets!["B2"]);
        Assert.True(c.NightMode);
        Assert.Equal(40, c.Volume); // still the same speaker: only capped
        Assert.Equal(85, c.ArrivalToRenderMs);
    }

    [Fact]
    public void Homes_picker_shows_the_profile_applied_last_or_the_placeholder()
    {
        List<UsageProfile> profiles = [new() { Id = "game", Name = "游戏和语音" }, new() { Id = "movie", Name = "夜间电影" }];
        Assert.Equal(-1, UsageProfiles.PickerIndex(profiles, null)); // never simply the first: one click on 应用 would switch to it
        Assert.Equal(1, UsageProfiles.PickerIndex(profiles, "movie"));
        Assert.Equal(-1, UsageProfiles.PickerIndex(profiles, "MOVIE"));
        Assert.Equal(-1, UsageProfiles.PickerIndex(profiles, "deleted"));
        Assert.Equal(-1, UsageProfiles.PickerIndex([], "movie"));

        var c = new AppConfig { UsageProfiles = profiles, LastUsageProfileId = "deleted" };
        UsageProfiles.Normalize(c);
        Assert.Null(c.LastUsageProfileId);
        c.LastUsageProfileId = "movie";
        UsageProfiles.Normalize(c);
        Assert.Equal("movie", c.LastUsageProfileId);
    }

    [Fact]
    public void A_name_is_taken_by_another_profile_whatever_its_case_and_spaces()
    {
        List<UsageProfile> profiles = [new() { Id = "game", Name = "Gaming" }, new() { Id = "movie", Name = "夜间电影" }];
        Assert.True(UsageProfiles.NameTaken(profiles, "  gaming ", null));
        Assert.True(UsageProfiles.NameTaken(profiles, "夜间电影", "game"));
        Assert.False(UsageProfiles.NameTaken(profiles, "GAMING", "game")); // the profile itself: renamed or overwritten
        Assert.False(UsageProfiles.NameTaken(profiles, "Music", null));
    }

    /// <summary>Every setting is a decision: restored by a profile (UsageSettings), or left out on purpose, with why.</summary>
    private static readonly Dictionary<string, string> NotInProfiles = new()
    {
        [nameof(AppConfig.Volume)] = "the speaker's loudness: a profile only caps it",
        [nameof(AppConfig.AutoConnect)] = "startup",
        [nameof(AppConfig.ManualDevices)] = "the receiver address book is shared by every profile",
        [nameof(AppConfig.ReceiverSettings)] = "per-receiver capabilities and compatibility preferences",
        [nameof(AppConfig.RaopPasswordsProtected)] = "device-bound encrypted credentials",
        [nameof(AppConfig.MeasuredAvOffsetMs)] = "calibration",
        [nameof(AppConfig.SyncCalibrations)] = "calibration, kept per speaker and capture path",
        [nameof(AppConfig.UsageProfiles)] = "the profiles themselves",
        [nameof(AppConfig.Stems)] = "optional experimental module path and stem mixes; enabling is session-only",
        [nameof(AppConfig.Cinema)] = "physical surround layout and room-specific acoustic alignment; file playback is session-only",
        [nameof(AppConfig.LastUsageProfileId)] = "which profile was applied last",
        [nameof(AppConfig.ArrivalToRenderMs)] = "measured by the speaker at every connection",
        [nameof(AppConfig.FifoTargetMs)] = "tuned for this PC",
        [nameof(AppConfig.VideoDelayExtraMs)] = "calibration",
        [nameof(AppConfig.LocalApiPort)] = "the browser extension's port",
        [nameof(AppConfig.Language)] = "the interface",
        [nameof(AppConfig.Theme)] = "the interface",
        [nameof(AppConfig.ForwardVolumeKeys)] = "only read from old files",
        [nameof(AppConfig.VlcPasswordProtected)] = "a credential",
        [nameof(AppConfig.Hotkeys)] = "the same keys whatever the use",
        [nameof(AppConfig.RoutingNoticeAccepted)] = "asked once",
        [nameof(AppConfig.AutoUpdateCheck)] = "updates",
        [nameof(AppConfig.UpdateChannel)] = "updates",
        [nameof(AppConfig.UpdateSkipped)] = "updates",
        [nameof(AppConfig.UpdateLastCheck)] = "updates",
        [nameof(AppConfig.UpdateNotified)] = "updates",
        [nameof(AppConfig.LastOwnSession)] = "a record of this PC's last session",
        [nameof(AppConfig.HomeTheaterTakeover)] = "how the household's Apple TV and HomePod share, the same whatever the use",
        [nameof(AppConfig.NightDisconnect)] = "the household's night, like the auto-connect at startup",
        [nameof(AppConfig.NightDisconnectFrom)] = "the household's night",
        [nameof(AppConfig.NightDisconnectUntil)] = "the household's night",
        [nameof(AppConfig.NightDisconnectIdleMinutes)] = "the household's night",
        [nameof(AppConfig.AppSendVolumesOff)] = "where an app's own HomePod volume starts when turned on again; the levels in use are AppSendVolumes",
    };

    [Fact]
    public void Every_setting_is_restored_by_profiles_or_deliberately_left_out()
    {
        var settable = typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true }).ToDictionary(p => p.Name, p => p.PropertyType);
        var captured = typeof(UsageSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var undecided = settable.Keys.Where(n => !NotInProfiles.ContainsKey(n) && captured.All(p => p.Name != n)).ToList();
        Assert.True(undecided.Count == 0, "AppConfig settings neither in UsageSettings nor in NotInProfiles (decide whether a " +
                                          "profile restores them): " + string.Join(", ", undecided));
        Assert.Empty(captured.Select(p => p.Name).Intersect(NotInProfiles.Keys));
        Assert.Empty(NotInProfiles.Keys.Except(settable.Keys)); // no exclusion of a setting that is gone
        foreach (var p in captured)
        {
            Assert.True(settable.TryGetValue(p.Name, out var type), $"UsageSettings.{p.Name} is no AppConfig setting");
            Assert.Equal(type, p.PropertyType);
        }
    }

    [Fact]
    public void Capture_copies_every_choice_of_a_profile()
    {
        var cfg = new AppConfig();
        var captured = typeof(UsageSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var p in captured)
        {
            var setting = typeof(AppConfig).GetProperty(p.Name)!;
            if (Sample(p.PropertyType, setting.GetValue(cfg), p.Name) is { } value) setting.SetValue(cfg, value);
        }
        cfg.AppRoutes["game"] = AudioRoute.Local;
        cfg.AppSendVolumes["game"] = 40;
        cfg.GroupVolumeOffsets = new() { ["B2"] = -5 };
        cfg.Effects.ReverbEnabled = true;

        var saved = UsageProfiles.Capture(cfg);
        foreach (var p in captured)
        {
            object? expected = typeof(AppConfig).GetProperty(p.Name)!.GetValue(cfg), actual = p.GetValue(saved);
            if (expected is null or string or ValueType) Assert.Equal(expected, actual);
            else Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual)); // collections, effects
        }
    }

    /// <summary>A value other than the current one for a plain setting; null for collections and the effects.</summary>
    private static object? Sample(Type type, object? current, string name)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "set-" + name;
        if (t == typeof(int)) return current is 37 ? 38 : 37;
        if (t == typeof(bool)) return current is not true;
        if (t.IsEnum) return Enum.GetValues(t).Cast<object>().First(v => !v.Equals(current));
        return null;
    }

    [Fact]
    public void Profiles_survive_config_file_round_trip()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HomePodCast-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "config.json");
            var c = new AppConfig { DeviceId = "pod", CaptureDeviceId = "endpoint", NightMode = true };
            c.UsageProfiles.Add(new() { Name = "Night movie", Settings = UsageProfiles.Capture(c) });
            c.LastUsageProfileId = c.UsageProfiles[0].Id;
            c.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(c.LastUsageProfileId, loaded.LastUsageProfileId);
            UsageProfiles.Apply(loaded, Assert.Single(loaded.UsageProfiles).Settings);
            Assert.Equal("pod", loaded.DeviceId);
            Assert.Equal("endpoint", loaded.CaptureDeviceId);
            Assert.True(loaded.NightMode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
