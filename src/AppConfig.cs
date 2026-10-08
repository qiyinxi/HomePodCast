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
