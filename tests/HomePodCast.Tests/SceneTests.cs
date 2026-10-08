using System.Text.Json;

namespace HomePodCast.Tests;

public class SceneTests
{
    [Theory]
    [InlineData(Scene.Recommended, 120)]
    [InlineData(Scene.Game, 105)]
    [InlineData(Scene.Music, 500)]
    [InlineData(Scene.Movie, 200)]
    [InlineData(Scene.Custom, 137)]
    public void Each_scene_maps_to_its_latency(Scene scene, int ms)
    {
        Assert.Equal(ms, Scenes.LatencyMs(scene, customMs: 137));
    }

    [Fact]
    public void Scene_latencies_still_go_through_the_safety_floor()
    {
        using var c = new StreamController { ArrivalToRenderMs = 85 };
        Assert.Equal(105, c.SafeLatency(Scenes.LatencyMs(Scene.Game, 0)));   // 85 + 15 = 100 < 105
        c.ArrivalToRenderMs = 95;
        Assert.Equal(110, c.SafeLatency(Scenes.LatencyMs(Scene.Game, 0)));   // floor wins: max(105, 95 + 15)
        Assert.Equal(200, c.SafeLatency(Scenes.LatencyMs(Scene.Movie, 0)));
    }

    [Fact]
    public void Hotkey_cycles_recommended_game_music_movie_custom()
    {
        Assert.Equal(Scene.Game, Scenes.Next(Scene.Recommended));
        Assert.Equal(Scene.Music, Scenes.Next(Scene.Game));
        Assert.Equal(Scene.Movie, Scenes.Next(Scene.Music));
        Assert.Equal(Scene.Custom, Scenes.Next(Scene.Movie));
        Assert.Equal(Scene.Recommended, Scenes.Next(Scene.Custom));
    }

    [Fact]
    public void Loaded_config_with_a_preset_gets_the_preset_latency()
    {
        var cfg = new AppConfig { Scene = Scene.Music, LatencyMs = 105, CustomLatencyMs = 140 };
        Assert.True(Scenes.Reconcile(cfg));
        Assert.Equal(500, cfg.LatencyMs);
        Assert.Equal(140, Scenes.CustomMs(cfg));
        Assert.False(Scenes.Reconcile(cfg));

        var custom = new AppConfig { Scene = Scene.Custom, LatencyMs = 300 };
        Assert.False(Scenes.Reconcile(custom)); // 300 dragged by hand stays 自定义
        Assert.Equal(300, Scenes.CustomMs(custom));

        var bad = new AppConfig { Scene = (Scene)42, LatencyMs = 120 };
        Scenes.Reconcile(bad);
        Assert.Equal(Scene.Custom, bad.Scene);
    }

    [Fact]
    public void Config_from_an_older_version_keeps_its_own_latency()
    {
        // 0.1 configs have no "Scene": they stay 自定义 at the saved latency instead of jumping to 推荐.
        var old = JsonSerializer.Deserialize<AppConfig>("""{ "LatencyMs": 105 }""")!;
        Assert.Equal(Scene.Custom, old.Scene);
        Assert.False(Scenes.Reconcile(old));
        Assert.Equal(105, old.LatencyMs);
    }

    [Fact]
    public void The_old_12_ms_fifo_default_is_moved_to_16_but_other_values_stay()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """{ "FifoTargetMs": 12 }""");
            Assert.Equal(16, AppConfig.Load(path).FifoTargetMs);
            File.WriteAllText(path, """{ "FifoTargetMs": 25 }""");
            Assert.Equal(25, AppConfig.Load(path).FifoTargetMs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_fresh_install_starts_on_the_recommended_scene()
    {
        var cfg = AppConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"));
        Assert.Equal(Scene.Recommended, cfg.Scene);
        Assert.False(Scenes.Reconcile(cfg));
        Assert.Equal(Scenes.RecommendedMs, cfg.LatencyMs);
    }

    [Fact]
    public void Defaults_keep_the_old_behaviour()
    {
        var cfg = new AppConfig();
        Assert.Equal(Scene.Custom, cfg.Scene);
        Assert.Equal(120, cfg.LatencyMs);
        Assert.Equal(100, cfg.VolumeCapPercent);
        Assert.False(cfg.NightMode);
        Assert.True(cfg.ForwardVolumeKeys);
    }

    [Fact]
    public void Config_stores_scene_and_hotkeys_by_name()
    {
        var cfg = new AppConfig { Scene = Scene.Movie };
        cfg.Hotkeys[HotkeyAction.Mute] = "";
        var json = JsonSerializer.Serialize(cfg);
        Assert.Contains("\"Scene\":\"Movie\"", json);
        Assert.Contains("\"Mute\":\"\"", json);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal(Scene.Movie, back.Scene);
        Assert.Null(Hotkey.FromConfig(back, HotkeyAction.Mute));                       // switched off
        Assert.Equal("Ctrl+Alt+PageUp", Hotkey.FromConfig(back, HotkeyAction.VolumeUp)?.ToString()); // default
    }
}
