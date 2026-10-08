using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HomePodCast;

public sealed class AppConfig
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Host { get; set; }
    public int LatencyMs { get; set; } = 120;
    public double? Volume { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int? MeasuredAvOffsetMs { get; set; }
    public int? ArrivalToRenderMs { get; set; }

    /// <summary>Capture→sender FIFO target. Smaller = lower latency, but must cover capture jitter.</summary>
    public int FifoTargetMs { get; set; } = 12;

    /// <summary>
    /// How much later than the requested playout delay the HomePod is actually heard, relative to the
    /// picture (PC-side capture + speaker output). Phone-video measurement 2026-10-07: 120 ms → 156 ms.
    /// </summary>
    public int VideoDelayExtraMs { get; set; } = 36;

    public int LocalApiPort { get; set; } = Net.LocalApi.DefaultPort;

    // ---- Stereo pair / multi-speaker (experimental, see GroupPlan.cs) -------------------------------
    // DeviceId "pair:<tsid>" selects a stereo pair; MultiRoom* adds a second speaker to a single one.

    /// <summary>Split channels on the PC: the left speaker gets (L,L), the right one (R,R).</summary>
    public bool GroupSplitChannels { get; set; }

    /// <summary>Swap which speaker counts as left (pair members are ordered by device id).</summary>
    public bool GroupSwapChannels { get; set; }

    /// <summary>Per-speaker volume offset in percentage points, keyed by GroupPlan.Key(deviceId).</summary>
    public Dictionary<string, int>? GroupVolumeOffsets { get; set; }

    /// <summary>A second speaker that plays in sync with the selected one (multi-room).</summary>
    public string? MultiRoomDeviceId { get; set; }
    public string? MultiRoomDeviceName { get; set; }
    public string? MultiRoomHost { get; set; }

    // ---- UI language (L.cs)

    /// <summary>"auto" follows the Windows display language; or "zh-CN", "zh-TW", "en", "ja". Applied at startup.</summary>
    public string Language { get; set; } = L.Auto;

    /// <summary>"auto" follows the Windows light/dark setting; "light" or "dark" fix it (UI.Theme). Applied at once.</summary>
    public string Theme { get; set; } = "auto";

    // ---- Scenes, volume cap, night mode, hotkeys, volume-key forwarding

    /// <summary>Usage preset; a preset pins LatencyMs to its own value (Scenes.LatencyMs).</summary>
    public Scene Scene { get; set; } = Scene.Custom;

    /// <summary>The user's own latency, remembered while a preset is active and restored by 「自定义」.</summary>
    public int? CustomLatencyMs { get; set; }

    /// <summary>Ceiling for the HomePod's own volume, percent; 100 = no limit.</summary>
    public int VolumeCapPercent { get; set; } = 100;

    /// <summary>Night mode: dynamic-range compression of the audio before it is sent.</summary>
    public bool NightMode { get; set; }

    /// <summary>While streaming with Windows muted or at 0 %, the keyboard volume keys drive the HomePod.</summary>
    public bool ForwardVolumeKeys { get; set; } = true;

    /// <summary>Global hotkeys such as "Ctrl+Alt+PageUp"; a missing entry means the default, "" means off.</summary>
    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = new();

    // ---- 麦克风与音效 (UI.Pages.EffectsPage, Audio.MicEffects) --------------------------------------------------
    // Mic device, gain and noise gate; where the voice goes (HomePod / local monitor / both) and the monitor
    // device; reverb; the mic EQ and the EQ on the whole HomePod output. The mic's on/off is not saved.
    public Audio.EffectsSettings Effects { get; set; } = new();

    // ---- Per-app routing (process loopback, Windows 10 2004+) ----

    /// <summary>Destination of apps without their own rule. HomePod = capture the whole output (lowest latency).</summary>
    public Audio.AudioRoute RouteDefault { get; set; } = Audio.AudioRoute.HomePod;

    /// <summary>Per-app destinations, keyed by lower-case executable name without ".exe".</summary>
    public Dictionary<string, Audio.AudioRoute> AppRoutes { get; set; } = [];

    /// <summary>
    /// HOMEPODCAST_PROFILE (development/testing): its own config folder and single-instance name. A new
    /// profile starts with AutoConnect off and a random local API port, so a test copy never grabs the
    /// speaker or the real instance's port.
    /// </summary>
    [JsonIgnore]
    public static string? Profile =>
        Environment.GetEnvironmentVariable("HOMEPODCAST_PROFILE") is { Length: > 0 } p ? p : null;

    [JsonIgnore]
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Profile is null ? "HomePodCast" : "HomePodCast-" + Profile);

    private static string FilePath => Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep 卧室 readable
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Warn($"config unreadable, using defaults: {ex.Message}");
        }
        return Profile is null ? new AppConfig() : new AppConfig { AutoConnect = false, LocalApiPort = 0 };
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "HomePodCast";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
