using System.Text.Json.Serialization;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

[JsonConverter(typeof(JsonStringEnumConverter<SpeakerPosition>))]
public enum SpeakerPosition { Stereo, Left, Right, Center, Subwoofer, RearLeft, RearRight, SideLeft, SideRight }

/// <summary>One saved output row, shared by live capture and the local-file player.</summary>
public sealed class GroupSpeakerSettings
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Host { get; set; }
    public SpeakerPosition Position { get; set; }
    /// <summary>Extra waiting time: positive values play later.</summary>
    public int DelayMs { get; set; }
    public int VolumeOffset { get; set; }
}

public static class GroupSettings
{
    public static bool IsStereoPosition(SpeakerPosition position) => position is SpeakerPosition.Stereo or SpeakerPosition.Left or SpeakerPosition.Right;

    public static List<GroupSpeakerSettings> Copy(IEnumerable<GroupSpeakerSettings> speakers) => speakers.Select(s => new GroupSpeakerSettings
    {
        DeviceId = s.DeviceId, Name = s.Name, Host = s.Host, Position = s.Position, DelayMs = s.DelayMs, VolumeOffset = s.VolumeOffset,
    }).ToList();

    /// <summary>Import the old group and cinema layouts once. Pair channel order needs its first fresh scan.</summary>
    public static void Normalize(AppConfig cfg, IReadOnlyList<AirPlayDevice>? found = null)
    {
        if (cfg.GroupSpeakers == null)
        {
            cfg.GroupSpeakers = [];
            if (cfg.DeviceId is { } id && !StereoPairs.IsPairId(id))
                cfg.GroupSpeakers.Add(new() { DeviceId = id, Name = cfg.DeviceName ?? id, Host = cfg.Host });
            if (cfg.MultiRoomDeviceId is { } extra && !StereoPairs.IsPairId(extra) && cfg.GroupSpeakers.All(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(extra)))
                cfg.GroupSpeakers.Add(new() { DeviceId = extra, Name = cfg.MultiRoomDeviceName ?? extra, Host = cfg.MultiRoomHost });
            foreach (var old in cfg.Cinema?.Speakers ?? [])
            {
                var row = cfg.GroupSpeakers.FirstOrDefault(s => GroupPlan.Key(s.DeviceId) == GroupPlan.Key(old.DeviceId));
                if (row == null) cfg.GroupSpeakers.Add(row = new() { DeviceId = old.DeviceId, Name = old.Name, Host = old.Host });
                row.Position = FromCinema(old.Role); row.DelayMs = old.DelayMs;
            }
            foreach (var row in cfg.GroupSpeakers)
                row.VolumeOffset = cfg.GroupVolumeOffsets?.GetValueOrDefault(GroupPlan.Key(row.DeviceId)) ?? 0;
        }
        if (cfg.DeviceId is { } selected && !StereoPairs.IsPairId(selected) && cfg.GroupSpeakers.All(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(selected)))
            cfg.GroupSpeakers.Insert(0, new() { DeviceId = selected, Name = cfg.DeviceName ?? selected, Host = cfg.Host });
        if (StereoPairs.TryGetTsid(cfg.DeviceId, out var tsid) && found != null)
        {
            var members = StereoPairs.Members(found, tsid);
            if (members.Count == 2)
                foreach (var device in members)
                    if (cfg.GroupSpeakers.All(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(device.DeviceId)))
                        cfg.GroupSpeakers.Add(new() { DeviceId = device.DeviceId, Name = device.Name, Host = device.Address.ToString(),
                            VolumeOffset = cfg.GroupVolumeOffsets?.GetValueOrDefault(GroupPlan.Key(device.DeviceId)) ?? 0 });
        }
        var legacy = StereoPairs.TryGetTsid(cfg.DeviceId, out var pair)
            ? found == null ? [] : StereoPairs.Members(found, pair).Select(d => d.DeviceId).ToList()
            : new[] { cfg.DeviceId, cfg.MultiRoomDeviceId }.Where(id => id != null).Cast<string>().DistinctBy(GroupPlan.Key).ToList();
        if (cfg.GroupCapturePositions == null && legacy.Count > 0 && (!StereoPairs.IsPairId(cfg.DeviceId) || legacy.Count == 2))
            cfg.GroupCapturePositions = legacy.Select((id, i) => (id, position: cfg.GroupSplitChannels && legacy.Count == 2
                ? (i == 0) != cfg.GroupSwapChannels ? SpeakerPosition.Left : SpeakerPosition.Right : SpeakerPosition.Stereo))
                .ToDictionary(x => GroupPlan.Key(x.id), x => x.position);
        if (cfg.GroupSplitChannels && legacy.Count == 2)
        {
            for (int i = 0; i < 2; i++)
                if (cfg.GroupSpeakers.FirstOrDefault(s => GroupPlan.Key(s.DeviceId) == GroupPlan.Key(legacy[i])) is { } row)
                    if (!(cfg.Cinema?.Speakers ?? []).Any(s => GroupPlan.Key(s.DeviceId) == GroupPlan.Key(row.DeviceId)))
                        row.Position = (i == 0) != cfg.GroupSwapChannels ? SpeakerPosition.Left : SpeakerPosition.Right;
        }
        cfg.GroupSpeakers = cfg.GroupSpeakers.Where(s => !string.IsNullOrWhiteSpace(s.DeviceId) && !StereoPairs.IsPairId(s.DeviceId))
            .DistinctBy(s => GroupPlan.Key(s.DeviceId)).Take(8).ToList();
        foreach (var row in cfg.GroupSpeakers)
        {
            if (!Enum.IsDefined(row.Position)) row.Position = SpeakerPosition.Stereo;
            row.DelayMs = Math.Clamp(row.DelayMs, 0, 500); row.VolumeOffset = Math.Clamp(row.VolumeOffset, -100, 100);
        }
    }

    public static SpeakerPosition FromCinema(CinemaRole role) => role switch
    {
        CinemaRole.FrontLeft => SpeakerPosition.Left, CinemaRole.FrontRight => SpeakerPosition.Right,
        CinemaRole.Center => SpeakerPosition.Center, CinemaRole.Subwoofer => SpeakerPosition.Subwoofer,
        CinemaRole.RearLeft => SpeakerPosition.RearLeft, CinemaRole.RearRight => SpeakerPosition.RearRight,
        CinemaRole.SideLeft => SpeakerPosition.SideLeft, CinemaRole.SideRight => SpeakerPosition.SideRight, _ => SpeakerPosition.Stereo,
    };

    public static CinemaRole ToCinema(SpeakerPosition position) => position switch
    {
        SpeakerPosition.Left => CinemaRole.FrontLeft, SpeakerPosition.Right => CinemaRole.FrontRight,
        SpeakerPosition.Center => CinemaRole.Center, SpeakerPosition.Subwoofer => CinemaRole.Subwoofer,
        SpeakerPosition.RearLeft => CinemaRole.RearLeft, SpeakerPosition.RearRight => CinemaRole.RearRight,
        SpeakerPosition.SideLeft => CinemaRole.SideLeft, SpeakerPosition.SideRight => CinemaRole.SideRight, _ => CinemaRole.Stereo,
    };
}
