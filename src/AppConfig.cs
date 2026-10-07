using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HomePodCast;

public sealed class AppConfig
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Host { get; set; }
    public int LatencyMs { get; set; } = 100;
    public double? Volume { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int? MeasuredAvOffsetMs { get; set; }

    [JsonIgnore]
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomePodCast");

    private static string FilePath => Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

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
        return new AppConfig();
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
