using System.Text.Json;
using HomePodCast.Audio;

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
    public void Applying_caps_never_increases_volume_and_other_speakers_drop_old_processing_time()
    {
        var c = new AppConfig { DeviceId = "one", Volume = 70, ArrivalToRenderMs = 85 };
        UsageProfiles.Apply(c, new UsageSettings { DeviceId = "one", VolumeCapPercent = 30, Scene = Scene.Game });
        Assert.Equal(30, c.Volume);
        Assert.Equal(Scenes.GameMs, c.LatencyMs);
        UsageProfiles.Apply(c, new UsageSettings { DeviceId = "two", VolumeCapPercent = 80 });
        Assert.Null(c.Volume);
        Assert.Null(c.ArrivalToRenderMs);
    }

    [Fact]
    public void Loading_profiles_normalizes_nulls_bad_values_duplicates_and_names()
    {
        var c = new AppConfig { UsageProfiles = [
            new() { Id = "id", Name = "  gaming  ", Settings = new() { VolumeCapPercent = 999, LatencyMs = -1,
                AppRoutes = null!, AppSendVolumes = new() { ["Game.EXE"] = 100, [""] = 3 }, Effects = null! } },
            new() { Id = "ID", Name = "duplicate" }, new() { Id = "x", Name = " " }, null! ] };
        UsageProfiles.Normalize(c);
        var p = Assert.Single(c.UsageProfiles);
        Assert.Equal("gaming", p.Name);
        Assert.Equal(100, p.Settings.VolumeCapPercent);
        Assert.Equal(100, p.Settings.LatencyMs);
        Assert.Equal(100, p.Settings.AppSendVolumes["game"]);
        Assert.NotNull(p.Settings.Effects);
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
            c.Save(path);
            var loaded = AppConfig.Load(path);
            UsageProfiles.Apply(loaded, Assert.Single(loaded.UsageProfiles).Settings);
            Assert.Equal("pod", loaded.DeviceId);
            Assert.Equal("endpoint", loaded.CaptureDeviceId);
            Assert.True(loaded.NightMode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
