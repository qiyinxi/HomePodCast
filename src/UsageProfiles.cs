using System.Net;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

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

    /// <summary>The address when the profile was saved: kept in the file, never applied (<see cref="UsageProfiles.FreshHost"/>).</summary>
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
    public List<GroupSpeakerSettings>? GroupSpeakers { get; set; }
    public List<string>? GroupCaptureDeviceIds { get; set; }
    public Dictionary<string, SpeakerPosition>? GroupCapturePositions { get; set; }
    public List<DlnaOutputSettings>? GroupDlnaOutputs { get; set; }
    public List<string>? GroupCaptureDlnaUdns { get; set; }
    public List<LocalOutputOptions>? GroupLocalOutputs { get; set; }
    public List<string>? GroupCaptureLocalDeviceIds { get; set; }
    public Dictionary<string, int> GroupCaptureDelayTrims { get; set; } = [];
    public Dictionary<string, bool> GroupRequiredOutputs { get; set; } = [];
    public int? GroupDelayMs { get; set; }
    public string? MultiRoomDeviceId { get; set; }
    public string? MultiRoomDeviceName { get; set; }

    /// <summary>Like <see cref="Host"/>: kept in the file, never applied.</summary>
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
        GroupSpeakers = c.GroupSpeakers == null ? null : GroupSettings.Copy(c.GroupSpeakers),
        GroupCaptureDeviceIds = c.GroupCaptureDeviceIds == null ? null : new(c.GroupCaptureDeviceIds),
        GroupCapturePositions = c.GroupCapturePositions == null ? null : new(c.GroupCapturePositions),
        GroupDlnaOutputs = DlnaGroupSettings.Copy(c.GroupDlnaOutputs),
        GroupCaptureDlnaUdns = c.GroupCaptureDlnaUdns == null ? null : new(c.GroupCaptureDlnaUdns),
        GroupLocalOutputs = LocalGroupSettings.Copy(c.GroupLocalOutputs),
        GroupCaptureLocalDeviceIds = c.GroupCaptureLocalDeviceIds == null ? null : new(c.GroupCaptureLocalDeviceIds),
        GroupCaptureDelayTrims = new(c.GroupCaptureDelayTrims ?? []), GroupRequiredOutputs = new(c.GroupRequiredOutputs ?? []),
        GroupDelayMs = c.GroupDelayMs,
        MultiRoomDeviceId = c.MultiRoomDeviceId, MultiRoomDeviceName = c.MultiRoomDeviceName, MultiRoomHost = c.MultiRoomHost,
    };

    /// <summary>Copy only playback choices, keeping the effects object owned by MicEffects alive.</summary>
    /// <param name="scan">The last scan as found (TrayApp.LastScan): where a speaker that is not selected now was last seen.</param>
    public static void Apply(AppConfig c, UsageSettings saved, IReadOnlyList<AirPlayDevice>? scan = null)
    {
        var s = Clone(saved);
        Normalize(s);
        // A profile saved before any speaker was chosen says nothing about speakers: the selection and its group layout stay.
        if (s.DeviceId != null || s.GroupCaptureDeviceIds != null || s.GroupCaptureDlnaUdns != null || s.GroupCaptureLocalDeviceIds != null)
        {
            bool changedSpeaker = !SameDevice(c.DeviceId, s.DeviceId);
            // Resolved from the config as it is now, before anything is overwritten.
            string? host = FreshHost(s.DeviceId, c, scan ?? []), extraHost = FreshHost(s.MultiRoomDeviceId, c, scan ?? []);
            c.DeviceId = s.DeviceId; c.DeviceName = s.DeviceName; c.Host = host;
            c.GroupSplitChannels = s.GroupSplitChannels; c.GroupSwapChannels = s.GroupSwapChannels;
            c.GroupPairLeaderOnly = s.GroupPairLeaderOnly; c.GroupVolumeOffsets = s.GroupVolumeOffsets;
            c.GroupSpeakers = s.GroupSpeakers;
            c.GroupCaptureDeviceIds = s.GroupCaptureDeviceIds;
            c.GroupCapturePositions = s.GroupCapturePositions;
            if (s.GroupDlnaOutputs != null) c.GroupDlnaOutputs = DlnaGroupSettings.Copy(s.GroupDlnaOutputs);
            c.GroupCaptureDlnaUdns = s.GroupCaptureDlnaUdns;
            if (s.GroupLocalOutputs != null) c.GroupLocalOutputs = LocalGroupSettings.Copy(s.GroupLocalOutputs);
            c.GroupCaptureLocalDeviceIds = s.GroupCaptureLocalDeviceIds;
            c.GroupCaptureDelayTrims = s.GroupCaptureDelayTrims; c.GroupRequiredOutputs = s.GroupRequiredOutputs;
            c.GroupDelayMs = s.GroupDelayMs;
            c.MultiRoomDeviceId = s.MultiRoomDeviceId; c.MultiRoomDeviceName = s.MultiRoomDeviceName; c.MultiRoomHost = extraHost;
            if (c.GroupSpeakers != null)
                foreach (var row in c.GroupSpeakers) row.Host = FreshHost(row.DeviceId, c, scan ?? []);
            GroupSettings.Normalize(c);
            // Another speaker plays at its own volume, as when it is picked in the list (its processing time is kept the
            // same way: the next connection measures it again).
            if (changedSpeaker) c.Volume = null;
        }
        c.Scene = s.Scene; c.LatencyMs = s.LatencyMs; c.CustomLatencyMs = s.CustomLatencyMs;
        c.VolumeCapPercent = s.VolumeCapPercent; c.NightMode = s.NightMode; c.VolumeKeys = s.VolumeKeys;
        c.MoviePlayerSync = s.MoviePlayerSync; c.CaptureDeviceId = s.CaptureDeviceId; c.CaptureDeviceName = s.CaptureDeviceName;
        c.RouteDefault = s.RouteDefault; c.AppRoutes = s.AppRoutes; c.AppSendVolumes = s.AppSendVolumes;
        // Never raise the saved loudness.
        if (c.Volume is { } v) c.Volume = Math.Min(v, s.VolumeCapPercent);
        CopyEffects(c.Effects, s.Effects);
        Scenes.Reconcile(c);
    }

    /// <summary>
    /// The address a connection to <paramref name="deviceId"/> starts from. Never the one saved in a profile: after a DHCP
    /// reshuffle it may belong to another device, and a connect takes any address that answers on port 7000. The current
    /// one while the device is selected now (as the main or the multi-room speaker: kept up to date by every connection),
    /// else where the last scan saw it, else null (mDNS finds it when connecting).
    /// </summary>
    public static string? FreshHost(string? deviceId, AppConfig current, IReadOnlyList<AirPlayDevice> scan)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        if (SameDevice(current.DeviceId, deviceId) && Usable(current.Host)) return current.Host;
        if (SameDevice(current.MultiRoomDeviceId, deviceId) && Usable(current.MultiRoomHost)) return current.MultiRoomHost;
        return scan.FirstOrDefault(d => SameDevice(d.DeviceId, deviceId) && Usable(d.Address))?.Address.ToString();
    }

    private static bool SameDevice(string? a, string? b) => a != null && b != null && GroupPlan.Key(a) == GroupPlan.Key(b);

    private static bool Usable(string? host) => IPAddress.TryParse(host, out var address) && Usable(address);

    private static bool Usable(IPAddress address) =>
        !address.Equals(IPAddress.None) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// 首页's picker: the profile applied last while it still exists, else nothing (the placeholder). Never simply the
    /// first one, which a single click on 应用 would switch everything to.
    /// </summary>
    public static int PickerIndex(IReadOnlyList<UsageProfile> profiles, string? lastAppliedId)
    {
        if (lastAppliedId == null) return -1;
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Id == lastAppliedId) return i;
        return -1;
    }

    /// <summary>Another profile than <paramref name="exceptId"/> already has this name (trimmed, ignoring case).</summary>
    public static bool NameTaken(IEnumerable<UsageProfile> profiles, string name, string? exceptId) =>
        profiles.Any(p => p.Id != exceptId && string.Equals(p.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));

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
        OutputGroups.Normalize(c);
        c.GroupDlnaOutputs = DlnaGroupSettings.Copy(c.GroupDlnaOutputs);
        if (c.GroupCaptureDlnaUdns != null)
            c.GroupCaptureDlnaUdns = c.GroupCaptureDlnaUdns.Where(u => !string.IsNullOrWhiteSpace(u))
                .DistinctBy(DlnaGroupSettings.Key).Take(8).ToList();
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
        if (PickerIndex(c.UsageProfiles, c.LastUsageProfileId) < 0) c.LastUsageProfileId = null;
    }

    public static void Normalize(UsageSettings s)
    {
        if (s.GroupLocalOutputs != null) s.GroupLocalOutputs = LocalGroupSettings.Copy(s.GroupLocalOutputs);
        s.GroupCaptureLocalDeviceIds = OutputGroups.Selection(s.GroupCaptureLocalDeviceIds, LocalGroupSettings.Key);
        s.GroupCaptureDelayTrims = (s.GroupCaptureDelayTrims ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .GroupBy(p => GroupPlan.Key(p.Key)).ToDictionary(g => g.Key, g => Math.Clamp(g.Last().Value, -5000, 5000));
        s.GroupRequiredOutputs = (s.GroupRequiredOutputs ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .GroupBy(p => OutputGroups.RequiredKey(p.Key)).ToDictionary(g => g.Key, g => g.Last().Value);
        s.GroupDelayMs = s.GroupDelayMs is { } delay ? Math.Clamp(delay, 100, 30000) : null;
        if (s.GroupDlnaOutputs != null) s.GroupDlnaOutputs = DlnaGroupSettings.Copy(s.GroupDlnaOutputs);
        if (s.GroupCaptureDlnaUdns != null)
            s.GroupCaptureDlnaUdns = s.GroupCaptureDlnaUdns.Where(u => !string.IsNullOrWhiteSpace(u))
                .DistinctBy(DlnaGroupSettings.Key).Take(8).ToList();
        if (!Enum.IsDefined(s.Scene)) s.Scene = Scene.Custom;
        // The slider's own range only: a hand-tuned value below 100 ms stays. The speaker's floor (its processing time plus
        // the margin) is applied when connecting, as for a value set on 首页.
        s.LatencyMs = Math.Clamp(s.LatencyMs, 0, UI.LatencyTuner.MaxMs);
        if (s.CustomLatencyMs is { } ms) s.CustomLatencyMs = Math.Clamp(ms, 0, UI.LatencyTuner.MaxMs);
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
