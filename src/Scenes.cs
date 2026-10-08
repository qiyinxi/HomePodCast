using System.Text.Json.Serialization;

namespace HomePodCast;

/// <summary>Usage presets. Each one is only a requested latency; the floor in StreamController.SafeLatency still applies.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Scene>))]
public enum Scene { Custom, Game, Music, Movie, Recommended }

public static class Scenes
{
    /// <summary>
    /// Recommended = the safe everyday value, and the scene of a fresh install (a config written before scenes
    /// existed stays Custom, so its own latency is kept). Game = the lowest that measured clean on the test
    /// HomePod over Wi-Fi. Music needs no picture, so it takes the most headroom. Movie works together with
    /// other software (the browser extension delays the picture, players shift their audio): ~80 ms more
    /// headroom than Recommended for long films, while the extension only has to hold ~14 frames at 60 fps.
    /// </summary>
    public const int RecommendedMs = 120, GameMs = 105, MusicMs = 500, MovieMs = 200;

    /// <summary>UI order, and the order the scene hotkey cycles through.</summary>
    public static readonly Scene[] All = [Scene.Recommended, Scene.Game, Scene.Music, Scene.Movie, Scene.Custom];

    /// <summary>Latency to request for a scene; Custom is the user's own value.</summary>
    public static int LatencyMs(Scene scene, int customMs) => scene switch
    {
        Scene.Recommended => RecommendedMs,
        Scene.Game => GameMs,
        Scene.Music => MusicMs,
        Scene.Movie => MovieMs,
        _ => customMs,
    };

    public static Scene Next(Scene scene) => All[(Array.IndexOf(All, scene) + 1) % All.Length];

    public static string Name(Scene scene) => scene switch
    {
        Scene.Recommended => L.T("推荐"),
        Scene.Game => L.T("游戏"),
        Scene.Music => L.T("音乐"),
        Scene.Movie => L.T("影视"),
        _ => L.T("自定义"),
    };

    /// <summary>The user's own latency (kept up to date while in Custom, remembered while a preset is active).</summary>
    public static int CustomMs(AppConfig cfg) => cfg.CustomLatencyMs ?? cfg.LatencyMs;

    /// <summary>
    /// Brings a loaded config in line: a preset always means its own latency (the app may have quit inside
    /// the reconnect debounce, or the file was edited). Returns true if anything changed.
    /// </summary>
    public static bool Reconcile(AppConfig cfg)
    {
        if (!Enum.IsDefined(cfg.Scene)) cfg.Scene = Scene.Custom;
        if (cfg.Scene == Scene.Custom) return false;
        int ms = LatencyMs(cfg.Scene, cfg.LatencyMs);
        if (cfg.LatencyMs == ms) return false;
        cfg.LatencyMs = ms;
        return true;
    }
}
