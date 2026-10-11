using System.Text.Json;
using HomePodCast.Audio;

namespace HomePodCast;

/// <summary>A named snapshot of playback choices. Credentials, startup and microphone activation are never captured.</summary>
public sealed class UsageProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public UsageSettings Settings { get; set; } = new();
}

public sealed class UsageSettings
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Host { get; set; }
    public Scene Scene { get; set; }
    public int LatencyMs { get; set; } = Scenes.RecommendedMs;
    public int? CustomLatencyMs { get; set; }
    public int VolumeCapPercent { get; set; } = 100;
    public bool NightMode { get; set; }
    public VolumeKeyMode VolumeKeys { get; set; } = VolumeKeyMode.FollowWindows;
    public bool MoviePlayerSync { get; set; } = true;
    public string? CaptureDeviceId { get; set; }
    public string? CaptureDeviceName { get; set; }
    public AudioRoute RouteDefault { get; set; } = AudioRoute.HomePod;
    public Dictionary<string, AudioRoute> AppRoutes { get; set; } = [];
    public Dictionary<string, int> AppSendVolumes { get; set; } = [];
    public EffectsSettings Effects { get; set; } = new();
    public bool GroupSplitChannels { get; set; }
    public bool GroupSwapChannels { get; set; }
    public bool GroupPairLeaderOnly { get; set; }
    public Dictionary<string, int>? GroupVolumeOffsets { get; set; }
    public string? MultiRoomDeviceId { get; set; }
    public string? MultiRoomDeviceName { get; set; }
    public string? MultiRoomHost { get; set; }
}

public static class UsageProfiles
{
    public const int MaxProfiles = 32;
    public const int MaxNameLength = 64;

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static UsageSettings Capture(AppConfig c) => new()
    {
        DeviceId = c.DeviceId, DeviceName = c.DeviceName, Host = c.Host,
        Scene = c.Scene, LatencyMs = c.LatencyMs, CustomLatencyMs = c.CustomLatencyMs,
        VolumeCapPercent = c.VolumeCapPercent, NightMode = c.NightMode, VolumeKeys = c.VolumeKeys,
        MoviePlayerSync = c.MoviePlayerSync, CaptureDeviceId = c.CaptureDeviceId, CaptureDeviceName = c.CaptureDeviceName,
        RouteDefault = c.RouteDefault, AppRoutes = new(c.AppRoutes), AppSendVolumes = new(c.AppSendVolumes),
        Effects = Clone(c.Effects), GroupSplitChannels = c.GroupSplitChannels, GroupSwapChannels = c.GroupSwapChannels,
        GroupPairLeaderOnly = c.GroupPairLeaderOnly,
        GroupVolumeOffsets = c.GroupVolumeOffsets == null ? null : new(c.GroupVolumeOffsets),
        MultiRoomDeviceId = c.MultiRoomDeviceId, MultiRoomDeviceName = c.MultiRoomDeviceName, MultiRoomHost = c.MultiRoomHost,
    };

    /// <summary>Copy only playback choices, keeping the effects object owned by MicEffects alive.</summary>
    public static void Apply(AppConfig c, UsageSettings saved)
    {
        var s = Clone(saved);
        Normalize(s);
        bool changedSpeaker = !string.Equals(c.DeviceId, s.DeviceId, StringComparison.OrdinalIgnoreCase);
        c.DeviceId = s.DeviceId; c.DeviceName = s.DeviceName; c.Host = s.Host;
        c.Scene = s.Scene; c.LatencyMs = s.LatencyMs; c.CustomLatencyMs = s.CustomLatencyMs;
        c.VolumeCapPercent = s.VolumeCapPercent; c.NightMode = s.NightMode; c.VolumeKeys = s.VolumeKeys;
        c.MoviePlayerSync = s.MoviePlayerSync; c.CaptureDeviceId = s.CaptureDeviceId; c.CaptureDeviceName = s.CaptureDeviceName;
        c.RouteDefault = s.RouteDefault; c.AppRoutes = s.AppRoutes; c.AppSendVolumes = s.AppSendVolumes;
        c.GroupSplitChannels = s.GroupSplitChannels; c.GroupSwapChannels = s.GroupSwapChannels;
        c.GroupPairLeaderOnly = s.GroupPairLeaderOnly; c.GroupVolumeOffsets = s.GroupVolumeOffsets;
        c.MultiRoomDeviceId = s.MultiRoomDeviceId; c.MultiRoomDeviceName = s.MultiRoomDeviceName; c.MultiRoomHost = s.MultiRoomHost;
        // Never raise the saved loudness, and never carry a different speaker's volume/processing time across.
        if (changedSpeaker) { c.Volume = null; c.ArrivalToRenderMs = null; }
        else if (c.Volume is { } v) c.Volume = Math.Min(v, s.VolumeCapPercent);
        CopyEffects(c.Effects, s.Effects);
        Scenes.Reconcile(c);
    }

    private static void CopyEffects(EffectsSettings target, EffectsSettings source)
    {
        target.MicDeviceId = source.MicDeviceId; target.MicGainDb = source.MicGainDb;
        target.GateEnabled = source.GateEnabled; target.GateThresholdDb = source.GateThresholdDb;
        target.Destination = source.Destination; target.MonitorDeviceId = source.MonitorDeviceId;
        target.ReverbEnabled = source.ReverbEnabled; target.ReverbRoomSize = source.ReverbRoomSize;
        target.ReverbDamping = source.ReverbDamping; target.ReverbMix = source.ReverbMix;
        target.ReverbPreDelayMs = source.ReverbPreDelayMs; target.MicEq = source.MicEq; target.OutputEq = source.OutputEq;
    }

    public static void Normalize(AppConfig c)
    {
        c.UsageProfiles ??= [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        c.UsageProfiles = c.UsageProfiles.Where(p => p != null && p.Settings != null &&
            !string.IsNullOrWhiteSpace(p.Id) && ids.Add(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
            .Take(MaxProfiles).ToList();
        foreach (var p in c.UsageProfiles)
        {
            p.Name = p.Name.Trim()[..Math.Min(p.Name.Trim().Length, MaxNameLength)];
            Normalize(p.Settings);
        }
    }

    public static void Normalize(UsageSettings s)
    {
        if (!Enum.IsDefined(s.Scene)) s.Scene = Scene.Custom;
        s.LatencyMs = Math.Clamp(s.LatencyMs, 100, 500);
        if (s.CustomLatencyMs is { } ms) s.CustomLatencyMs = Math.Clamp(ms, 100, 500);
        s.VolumeCapPercent = VolumeLimit.NormalizeCap(s.VolumeCapPercent);
        if (!Enum.IsDefined(s.VolumeKeys)) s.VolumeKeys = VolumeKeyMode.FollowWindows;
        if (!Enum.IsDefined(s.RouteDefault)) s.RouteDefault = AudioRoute.HomePod;
        s.AppRoutes = (s.AppRoutes ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && Enum.IsDefined(kv.Value))
            .GroupBy(kv => RouteRules.KeyFor(kv.Key)).ToDictionary(g => g.Key, g => g.Last().Value);
        s.AppSendVolumes = (s.AppSendVolumes ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .GroupBy(kv => RouteRules.KeyFor(kv.Key)).ToDictionary(g => g.Key, g => Math.Clamp(g.Last().Value, 0, 100));
        s.Effects ??= new();
        s.Effects.MicEq ??= new(); s.Effects.OutputEq ??= new();
    }
}
