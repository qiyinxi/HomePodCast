using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>A named output selection, independent of capture, effects, credentials and startup.</summary>
public sealed class OutputGroupPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public OutputGroupSettings Settings { get; set; } = new();
}

public sealed class OutputGroupSettings
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Host { get; set; }
    public bool GroupSplitChannels { get; set; }
    public bool GroupSwapChannels { get; set; }
    public bool GroupPairLeaderOnly { get; set; }
    public Dictionary<string, int>? GroupVolumeOffsets { get; set; }
    public List<GroupSpeakerSettings>? GroupSpeakers { get; set; }
    public List<string>? GroupCaptureDeviceIds { get; set; }
    public Dictionary<string, SpeakerPosition>? GroupCapturePositions { get; set; }
    public List<LocalOutputOptions> GroupLocalOutputs { get; set; } = [];
    public List<string>? GroupCaptureLocalDeviceIds { get; set; }
    public List<DlnaOutputSettings> GroupDlnaOutputs { get; set; } = [];
    public List<string>? GroupCaptureDlnaUdns { get; set; }
    public Dictionary<string, int> GroupCaptureDelayTrims { get; set; } = [];
    public Dictionary<string, bool> GroupRequiredOutputs { get; set; } = [];
    public int? GroupDelayMs { get; set; }
    public string? MultiRoomDeviceId { get; set; }
    public string? MultiRoomDeviceName { get; set; }
    public string? MultiRoomHost { get; set; }
}

/// <summary>Windows endpoint identities retain their punctuation; they are not AirPlay MAC addresses.</summary>
public static class LocalGroupSettings
{
    public static string Key(string deviceId) => deviceId.Trim().ToUpperInvariant();

    public static List<LocalOutputOptions> Copy(IEnumerable<LocalOutputOptions>? outputs) => (outputs ?? [])
        .Where(o => o != null && ValidId(o.DeviceId)).DistinctBy(o => Key(o.DeviceId)).Take(8)
        .Select(o => o with
        {
            DeviceId = o.DeviceId.Trim(), Name = string.IsNullOrWhiteSpace(o.Name) ? o.DeviceId : o.Name,
            ExtraLatencyMs = Math.Clamp(o.ExtraLatencyMs, 0, 10000), TrimMs = Math.Clamp(o.TrimMs, -5000, 5000),
            VolumeOffset = Math.Clamp(o.VolumeOffset, -100, 100),
            Channels = Enum.IsDefined(o.Channels) ? o.Channels : ChannelMode.Stereo,
        }).ToList();

    public static IReadOnlyList<LocalOutputOptions> Selected(AppConfig cfg)
    {
        var selected = (cfg.GroupCaptureLocalDeviceIds ?? []).Where(ValidId).Select(Key).ToHashSet();
        return Copy(cfg.GroupLocalOutputs).Where(o => selected.Contains(Key(o.DeviceId))).ToArray();
    }

    internal static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 2048 && !id.Any(char.IsControl);
}

public static class OutputGroups
{
    public const int MaxGroups = 32, MaxNameLength = 64;

    public static OutputGroupSettings Capture(AppConfig c) => new()
    {
        DeviceId = c.DeviceId, DeviceName = c.DeviceName, Host = c.Host,
        GroupSplitChannels = c.GroupSplitChannels, GroupSwapChannels = c.GroupSwapChannels,
        GroupPairLeaderOnly = c.GroupPairLeaderOnly,
        GroupVolumeOffsets = c.GroupVolumeOffsets == null ? null : new(c.GroupVolumeOffsets),
        GroupSpeakers = c.GroupSpeakers == null ? null : GroupSettings.Copy(c.GroupSpeakers),
        GroupCaptureDeviceIds = c.GroupCaptureDeviceIds == null ? null : new(c.GroupCaptureDeviceIds),
        GroupCapturePositions = c.GroupCapturePositions == null ? null : new(c.GroupCapturePositions),
        GroupLocalOutputs = LocalGroupSettings.Copy(c.GroupLocalOutputs),
        GroupCaptureLocalDeviceIds = c.GroupCaptureLocalDeviceIds == null ? null : new(c.GroupCaptureLocalDeviceIds),
        GroupDlnaOutputs = DlnaGroupSettings.Copy(c.GroupDlnaOutputs),
        GroupCaptureDlnaUdns = c.GroupCaptureDlnaUdns == null ? null : new(c.GroupCaptureDlnaUdns),
        GroupCaptureDelayTrims = new(c.GroupCaptureDelayTrims ?? []), GroupRequiredOutputs = new(c.GroupRequiredOutputs ?? []),
        GroupDelayMs = c.GroupDelayMs,
        MultiRoomDeviceId = c.MultiRoomDeviceId, MultiRoomDeviceName = c.MultiRoomDeviceName, MultiRoomHost = c.MultiRoomHost,
    };

    public static OutputGroupSettings Clone(OutputGroupSettings s) => new()
    {
        DeviceId = s.DeviceId, DeviceName = s.DeviceName, Host = s.Host,
        GroupSplitChannels = s.GroupSplitChannels, GroupSwapChannels = s.GroupSwapChannels,
        GroupPairLeaderOnly = s.GroupPairLeaderOnly,
        GroupVolumeOffsets = s.GroupVolumeOffsets == null ? null : new(s.GroupVolumeOffsets),
        GroupSpeakers = s.GroupSpeakers == null ? null : GroupSettings.Copy(s.GroupSpeakers.Where(row => row != null)),
        GroupCaptureDeviceIds = s.GroupCaptureDeviceIds == null ? null : new(s.GroupCaptureDeviceIds),
        GroupCapturePositions = s.GroupCapturePositions == null ? null : new(s.GroupCapturePositions),
        GroupLocalOutputs = LocalGroupSettings.Copy(s.GroupLocalOutputs),
        GroupCaptureLocalDeviceIds = s.GroupCaptureLocalDeviceIds == null ? null : new(s.GroupCaptureLocalDeviceIds),
        GroupDlnaOutputs = DlnaGroupSettings.Copy(s.GroupDlnaOutputs),
        GroupCaptureDlnaUdns = s.GroupCaptureDlnaUdns == null ? null : new(s.GroupCaptureDlnaUdns),
        GroupCaptureDelayTrims = new(s.GroupCaptureDelayTrims ?? []), GroupRequiredOutputs = new(s.GroupRequiredOutputs ?? []),
        GroupDelayMs = s.GroupDelayMs,
        MultiRoomDeviceId = s.MultiRoomDeviceId, MultiRoomDeviceName = s.MultiRoomDeviceName, MultiRoomHost = s.MultiRoomHost,
    };

    /// <summary>Apply output choices only. Stored addresses are never used to reconnect a different device after DHCP changes.</summary>
    public static void Apply(AppConfig c, OutputGroupSettings settings)
    {
        var s = Clone(settings);
        Normalize(s);
        if (s.GroupCaptureDeviceIds is { Count: > 0 } ids && !StereoPairs.IsPairId(s.DeviceId))
        {
            var first = ids[0];
            var row = s.GroupSpeakers?.FirstOrDefault(r => GroupPlan.Key(r.DeviceId) == GroupPlan.Key(first));
            if (s.DeviceId == null || GroupPlan.Key(s.DeviceId) != GroupPlan.Key(first)) s.DeviceName = row?.Name ?? first;
            s.DeviceId = first;
        }
        if (s.GroupCaptureDeviceIds is { Count: 0 }) s.DeviceId = s.DeviceName = null;
        string? host = UsageProfiles.FreshHost(s.DeviceId, c, []), extraHost = UsageProfiles.FreshHost(s.MultiRoomDeviceId, c, []);
        var rows = GroupSettings.Copy((c.GroupSpeakers ?? []).Where(r => r != null && !string.IsNullOrWhiteSpace(r.DeviceId)));
        foreach (var saved in s.GroupSpeakers ?? [])
        {
            var current = rows.FirstOrDefault(r => GroupPlan.Key(r.DeviceId) == GroupPlan.Key(saved.DeviceId));
            if (current == null)
            {
                current = GroupSettings.Copy([saved])[0];
                rows.Add(current);
            }
            // The file player's positions and acoustic delays remain its own layout. Live channels/trims are separate.
            current.Name = saved.Name;
            current.Host = UsageProfiles.FreshHost(saved.DeviceId, c, []);
            current.VolumeOffset = saved.VolumeOffset;
        }
        c.DeviceId = s.DeviceId; c.DeviceName = s.DeviceName; c.Host = host;
        c.GroupSplitChannels = s.GroupSplitChannels; c.GroupSwapChannels = s.GroupSwapChannels;
        c.GroupPairLeaderOnly = s.GroupPairLeaderOnly; c.GroupVolumeOffsets = s.GroupVolumeOffsets;
        c.GroupSpeakers = rows; c.GroupCaptureDeviceIds = s.GroupCaptureDeviceIds; c.GroupCapturePositions = s.GroupCapturePositions;
        c.GroupLocalOutputs = MergeLocal(c.GroupLocalOutputs, s.GroupLocalOutputs);
        c.GroupCaptureLocalDeviceIds = s.GroupCaptureLocalDeviceIds;
        c.GroupDlnaOutputs = MergeDlna(c.GroupDlnaOutputs, s.GroupDlnaOutputs);
        c.GroupCaptureDlnaUdns = s.GroupCaptureDlnaUdns;
        c.GroupCaptureDelayTrims = s.GroupCaptureDelayTrims; c.GroupRequiredOutputs = s.GroupRequiredOutputs;
        c.GroupDelayMs = s.GroupDelayMs;
        c.MultiRoomDeviceId = s.MultiRoomDeviceId; c.MultiRoomDeviceName = s.MultiRoomDeviceName; c.MultiRoomHost = extraHost;
    }

    /// <summary>Save or replace a detached preset; this does not apply or select it.</summary>
    public static OutputGroupPreset Save(AppConfig c, string name, OutputGroupSettings settings, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var snapshot = Clone(settings);
        Normalize(snapshot);
        var preset = new OutputGroupPreset { Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Name = name.Trim()[..Math.Min(name.Trim().Length, MaxNameLength)], Settings = snapshot };
        c.SavedOutputGroups ??= [];
        int index = c.SavedOutputGroups.FindIndex(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            c.SavedOutputGroups[index] = preset;
            // Saving stays inert. A changed active preset must become selectable again on Home, even
            // when it is the only preset and selecting the same combo index would raise no event.
            if (string.Equals(c.SelectedOutputGroupId, preset.Id, StringComparison.OrdinalIgnoreCase)
                && JsonSerializer.Serialize(Capture(c)) != JsonSerializer.Serialize(snapshot))
                c.SelectedOutputGroupId = null;
        }
        else
        {
            if (c.SavedOutputGroups.Count >= MaxGroups) throw new InvalidOperationException("Too many output groups");
            c.SavedOutputGroups.Add(preset);
        }
        return preset;
    }

    public static bool Remove(AppConfig c, string id)
    {
        int removed = c.SavedOutputGroups?.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? 0;
        if (string.Equals(c.SelectedOutputGroupId, id, StringComparison.OrdinalIgnoreCase)) c.SelectedOutputGroupId = null;
        return removed > 0;
    }

    public static void Normalize(AppConfig c)
    {
        var s = Capture(c);
        Normalize(s);
        c.GroupLocalOutputs = s.GroupLocalOutputs; c.GroupCaptureLocalDeviceIds = s.GroupCaptureLocalDeviceIds;
        c.GroupDlnaOutputs = s.GroupDlnaOutputs; c.GroupCaptureDlnaUdns = s.GroupCaptureDlnaUdns;
        c.GroupCaptureDelayTrims = s.GroupCaptureDelayTrims; c.GroupRequiredOutputs = s.GroupRequiredOutputs;
        c.GroupDelayMs = s.GroupDelayMs;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        c.SavedOutputGroups = (c.SavedOutputGroups ?? []).Where(p => p != null && p.Settings != null &&
            !string.IsNullOrWhiteSpace(p.Id) && ids.Add(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
            .Take(MaxGroups).Select(p => new OutputGroupPreset { Id = p.Id,
                Name = p.Name.Trim()[..Math.Min(p.Name.Trim().Length, MaxNameLength)], Settings = Clone(p.Settings) }).ToList();
        foreach (var p in c.SavedOutputGroups) Normalize(p.Settings);
        if (!c.SavedOutputGroups.Any(p => string.Equals(p.Id, c.SelectedOutputGroupId, StringComparison.OrdinalIgnoreCase)))
            c.SelectedOutputGroupId = null;
    }

    public static void Normalize(OutputGroupSettings s)
    {
        s.GroupLocalOutputs = LocalGroupSettings.Copy(s.GroupLocalOutputs);
        s.GroupDlnaOutputs = DlnaGroupSettings.Copy(s.GroupDlnaOutputs);
        s.GroupCaptureLocalDeviceIds = Selection(s.GroupCaptureLocalDeviceIds, LocalGroupSettings.Key);
        s.GroupCaptureDlnaUdns = Selection(s.GroupCaptureDlnaUdns, DlnaGroupSettings.Key);
        s.GroupCaptureDeviceIds = Selection(s.GroupCaptureDeviceIds, GroupPlan.Key);
        s.GroupCaptureDelayTrims = (s.GroupCaptureDelayTrims ?? []).Where(p => LocalGroupSettings.ValidId(p.Key))
            .GroupBy(p => GroupPlan.Key(p.Key)).ToDictionary(g => g.Key, g => Math.Clamp(g.Last().Value, -5000, 5000));
        s.GroupRequiredOutputs = (s.GroupRequiredOutputs ?? []).Where(p => LocalGroupSettings.ValidId(p.Key))
            .GroupBy(p => RequiredKey(p.Key)).ToDictionary(g => g.Key, g => g.Last().Value);
        s.GroupDelayMs = s.GroupDelayMs is { } delay ? Math.Clamp(delay, 100, 30000) : null;
        s.GroupVolumeOffsets = s.GroupVolumeOffsets?.Where(p => LocalGroupSettings.ValidId(p.Key))
            .GroupBy(p => GroupPlan.Key(p.Key)).ToDictionary(g => g.Key, g => Math.Clamp(g.Last().Value, -100, 100));
        s.GroupCapturePositions = s.GroupCapturePositions?.Where(p => LocalGroupSettings.ValidId(p.Key))
            .GroupBy(p => GroupPlan.Key(p.Key)).ToDictionary(g => g.Key,
                g => Enum.IsDefined(g.Last().Value) ? g.Last().Value : SpeakerPosition.Stereo);
        if (s.GroupSpeakers != null)
        {
            s.GroupSpeakers = GroupSettings.Copy(s.GroupSpeakers.Where(r => r != null && LocalGroupSettings.ValidId(r.DeviceId)
                && !StereoPairs.IsPairId(r.DeviceId)).DistinctBy(r => GroupPlan.Key(r.DeviceId)).Take(8));
            foreach (var row in s.GroupSpeakers)
            {
                row.DelayMs = Math.Clamp(row.DelayMs, 0, 500); row.VolumeOffset = Math.Clamp(row.VolumeOffset, -30, 30);
                if (!Enum.IsDefined(row.Position)) row.Position = SpeakerPosition.Stereo;
            }
        }
    }

    internal static List<string>? Selection(IEnumerable<string>? ids, Func<string, string> key) => ids == null ? null
        : ids.Where(LocalGroupSettings.ValidId).DistinctBy(key).Take(8).ToList();

    // Only bare AirPlay ids use the MAC normalization. Endpoint and DLNA punctuation is part of their identity.
    internal static string RequiredKey(string id) => id.StartsWith("dlna:", StringComparison.OrdinalIgnoreCase)
        ? "dlna:" + DlnaGroupSettings.Key(id[5..])
        : id.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ? "local:" + LocalGroupSettings.Key(id[6..])
        : GroupPlan.Key(id);

    private static List<LocalOutputOptions> MergeLocal(IEnumerable<LocalOutputOptions>? current, IEnumerable<LocalOutputOptions> saved) =>
        LocalGroupSettings.Copy(saved.Concat(LocalGroupSettings.Copy(current)));

    private static List<DlnaOutputSettings> MergeDlna(IEnumerable<DlnaOutputSettings>? current, IEnumerable<DlnaOutputSettings> saved) =>
        DlnaGroupSettings.Copy(saved.Concat(DlnaGroupSettings.Copy(current)));
}
