using HomePodCast.Net;

namespace HomePodCast;

public enum GroupKind { StereoPair, MultiRoom }

public sealed record GroupMemberRef(string DeviceId, string Name, string? Host);

/// <summary>
/// What to connect when the selection is more than one speaker (experimental): a stereo pair, found by
/// its tight-sync id when connecting, or the selected speaker plus a second one for multi-room.
/// FromConfig returns null for a plain single speaker, which then keeps using the original
/// single-session path untouched.
/// </summary>
/// <remarks>
/// A stereo pair is played the way it is publicly confirmed to work (OwnTone issues #1291 and #1413, NTP
/// and PTP): one full session per member, each sent the same full-stereo stream on one timeline, and the
/// pair itself plays its left and right side. With one session to one member only, that member alone played
/// (OwnTone #704, #1291), so no relay by the leader is assumed; PairLeaderOnly exists to test exactly that.
/// All members of one connection share one groupUUID, like the members of an iOS group, which re-advertise
/// the sender's group as their "gid".
/// </remarks>
public sealed record GroupPlan(
    GroupKind Kind,
    string Name,
    string? PairTsid,
    IReadOnlyList<GroupMemberRef> Members,
    bool SplitChannels,
    bool SwapChannels,
    IReadOnlyDictionary<string, int> VolumeOffsets)
{
    /// <summary>Explicit local endpoints, empty for all existing selections. The full editor follows in P5.</summary>
    public IReadOnlyList<Audio.LocalOutputOptions> LocalOutputs { get; init; } = [];
    public IReadOnlyList<DlnaOutputSettings> DlnaOutputs { get; init; } = [];
    public int? GroupDelayMs { get; init; }
    public IReadOnlyDictionary<string, int> DelayTrims { get; init; } = new Dictionary<string, int>();
    /// <summary>RAOP outputs are optional unless explicitly required; AirPlay 2 members always remain required.</summary>
    public IReadOnlyDictionary<string, bool> RequiredOutputs { get; init; } = new Dictionary<string, bool>();

    /// <summary>Stereo pair only: one session, to the tight-sync leader (experiment, off by default).</summary>
    public bool PairLeaderOnly { get; init; }

    /// <summary>Send every member's session SETUP the same "groupUUID" (on by default; `group --no-group-uuid` for tests).</summary>
    public bool SharedGroupUuid { get; init; } = true;

    public IReadOnlyDictionary<string, GroupSpeakerSettings> SpeakerSettings { get; init; } = new Dictionary<string, GroupSpeakerSettings>();
    private bool UseSpeakerPositions { get; init; } = true;

    public static GroupPlan? FromConfig(AppConfig cfg)
    {
        var local = LocalGroupSettings.Selected(cfg);
        var dlna = DlnaGroupSettings.Selected(cfg);
        var airplay = cfg.GroupCaptureDeviceIds is { Count: 0 } ? null : FromAirPlayConfig(cfg);
        bool auxiliary = local.Count > 0 || dlna.Count > 0;
        if (!auxiliary && airplay == null && cfg.GroupCaptureDeviceIds is not { Count: 0 }) return null;
        if (airplay == null)
        {
            var bank = GroupSettings.Copy(cfg.GroupSpeakers ?? []);
            var selected = OutputGroups.Selection(cfg.GroupCaptureDeviceIds, Key);
            var members = selected == null
                ? !string.IsNullOrWhiteSpace(cfg.DeviceId) ? new[] { new GroupMemberRef(cfg.DeviceId, cfg.DeviceName ?? cfg.DeviceId, cfg.Host) } : []
                : selected.DistinctBy(Key).Select(id => bank.FirstOrDefault(s => Key(s.DeviceId) == Key(id)) is { } row
                    ? new GroupMemberRef(row.DeviceId, row.Name, Key(id) == Key(cfg.DeviceId ?? "") ? cfg.Host : row.Host)
                    : new GroupMemberRef(id, Key(id) == Key(cfg.DeviceId ?? "") ? cfg.DeviceName ?? id : id,
                        Key(id) == Key(cfg.DeviceId ?? "") ? cfg.Host : null)).ToArray();
            var offsets = new Dictionary<string, int>(cfg.GroupVolumeOffsets ?? new());
            if (selected != null) foreach (var row in bank) offsets[Key(row.DeviceId)] = row.VolumeOffset;
            airplay = new(GroupKind.MultiRoom, "", null, members, false, false, offsets)
            {
                SpeakerSettings = bank.Select(s => new GroupSpeakerSettings { DeviceId = s.DeviceId, Name = s.Name, Host = s.Host,
                    DelayMs = s.DelayMs, VolumeOffset = s.VolumeOffset,
                    Position = cfg.GroupCapturePositions?.GetValueOrDefault(Key(s.DeviceId)) ?? SpeakerPosition.Stereo })
                    .ToDictionary(s => Key(s.DeviceId)),
            };
        }
        string name = string.Join(" + ", new[] { airplay.Name }.Where(n => n.Length > 0)
            .Concat(airplay.Name.Length == 0 ? airplay.Members.Select(m => m.Name) : [])
            .Concat(local.Select(o => o.Name)).Concat(dlna.Select(d => d.Name)));
        var required = (cfg.GroupRequiredOutputs ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .GroupBy(p => OutputGroups.RequiredKey(p.Key)).ToDictionary(g => g.Key, g => g.Last().Value);
        return airplay with
        {
            Name = name,
            LocalOutputs = local.Select(o => required.TryGetValue(OutputGroups.RequiredKey("local:" + o.DeviceId), out bool flag)
                ? o with { Required = flag } : o).ToArray(),
            DlnaOutputs = dlna.Select(o => required.TryGetValue(OutputGroups.RequiredKey(o.Id), out bool flag)
                ? o with { Required = flag } : o).ToArray(),
            RequiredOutputs = required,
            DelayTrims = (cfg.GroupCaptureDelayTrims ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .GroupBy(p => Key(p.Key)).ToDictionary(g => g.Key, g => Math.Clamp(g.Last().Value, -5000, 5000)),
            GroupDelayMs = cfg.GroupDelayMs is { } delay ? Math.Clamp(delay, 100, 30000) : null,
        };
    }

    private static GroupPlan? FromAirPlayConfig(AppConfig cfg)
    {
        var selected = OutputGroups.Selection(cfg.GroupCaptureDeviceIds, Key);
        string? id = cfg.DeviceId;
        if (selected == null && string.IsNullOrWhiteSpace(id)) return null;
        var offsets = new Dictionary<string, int>(cfg.GroupVolumeOffsets ?? new());
        var settings = GroupSettings.Copy(cfg.GroupSpeakers ?? []).ToDictionary(s => Key(s.DeviceId));
        foreach (var (key, row) in settings) row.Position = cfg.GroupCapturePositions?.GetValueOrDefault(key) ?? SpeakerPosition.Stereo;
        if (cfg.GroupCaptureDeviceIds != null)
            foreach (var (key, row) in settings) offsets[key] = row.VolumeOffset;
        if (StereoPairs.TryGetTsid(id, out var tsid) && selected is not { Count: 0 })
            return new GroupPlan(GroupKind.StereoPair, cfg.DeviceName ?? tsid, tsid, [],
                cfg.GroupSplitChannels, cfg.GroupSwapChannels, offsets) { PairLeaderOnly = cfg.GroupPairLeaderOnly, SpeakerSettings = settings,
                    UseSpeakerPositions = cfg.GroupCaptureDeviceIds != null };
        var rows = selected?.Select(deviceId => settings.GetValueOrDefault(Key(deviceId)) ?? new GroupSpeakerSettings
            { DeviceId = deviceId, Name = id != null && Key(deviceId) == Key(id) ? cfg.DeviceName ?? deviceId : deviceId }).ToArray();
        if (rows is { Length: > 0 } && (rows.Length > 1 || id == null || Key(rows[0].DeviceId) != Key(id)))
            return new GroupPlan(GroupKind.MultiRoom, string.Join(" + ", rows.Select(s => s.Name)), null,
                rows.Select(s => new GroupMemberRef(s.DeviceId, s.Name, id != null && Key(s.DeviceId) == Key(id) ? cfg.Host : s.Host)).ToArray(),
                false, false, offsets) { SpeakerSettings = settings };
        if (cfg.GroupCaptureDeviceIds != null) return null;
        if (id == null) return null;
        if (cfg.MultiRoomDeviceId is { } extra && !StereoPairs.IsPairId(extra) && Key(extra) != Key(id))
            return new GroupPlan(GroupKind.MultiRoom, $"{cfg.DeviceName ?? id} + {cfg.MultiRoomDeviceName ?? extra}", null,
                [new GroupMemberRef(id, cfg.DeviceName ?? id, cfg.Host), new GroupMemberRef(extra, cfg.MultiRoomDeviceName ?? extra, cfg.MultiRoomHost)],
                cfg.GroupSplitChannels, cfg.GroupSwapChannels, offsets) { SpeakerSettings = settings,
                    UseSpeakerPositions = false };
        return null;
    }

    private bool LeaderOnly => Kind == GroupKind.StereoPair && PairLeaderOnly;

    /// <summary>
    /// Channels for the speaker at this position (members in their stable order). By default every speaker
    /// gets full stereo (a stereo pair picks its own side from it); with split channels the first is left and
    /// the second right, unless swapped. The leader alone always gets full stereo.
    /// </summary>
    public ChannelMode ChannelsFor(int index) =>
        UseSpeakerPositions && index < Members.Count && SpeakerSettings.ContainsKey(Key(Members[index].DeviceId)) ? ChannelsFor(index, Members[index].DeviceId)
        : !SplitChannels || LeaderOnly || index > 1 ? ChannelMode.Stereo
        : (index == 0) != SwapChannels ? ChannelMode.LeftOnly : ChannelMode.RightOnly;

    public ChannelMode ChannelsFor(int index, string deviceId) => LeaderOnly ? ChannelMode.Stereo
        : UseSpeakerPositions && SpeakerSettings.TryGetValue(Key(deviceId), out var row) ? row.Position switch
        {
            SpeakerPosition.Left => ChannelMode.LeftOnly, SpeakerPosition.Right => ChannelMode.RightOnly, _ => ChannelMode.Stereo,
        } : !SplitChannels || index > 1 ? ChannelMode.Stereo
        : (index == 0) != SwapChannels ? ChannelMode.LeftOnly : ChannelMode.RightOnly;

    public int DelayTrimFor(string deviceId) => DelayTrims.FirstOrDefault(p => Key(p.Key) == Key(deviceId)) is { Key: not null } live
        ? Math.Clamp(live.Value, -5000, 5000)
        : SpeakerSettings.TryGetValue(Key(deviceId), out var row) ? Math.Clamp(row.DelayMs, 0, 500) : 0;

    /// <summary>
    /// The pair members to open sessions to, in channel order, from a fresh scan: both of them (they must both
    /// be online), or with PairLeaderOnly the tight-sync leader alone — the first member if no single member
    /// claims the leader bit.
    /// </summary>
    public List<AirPlayDevice> PairTargets(IReadOnlyList<AirPlayDevice> devices)
    {
        if (Kind != GroupKind.StereoPair || PairTsid == null) throw new InvalidOperationException("not a stereo pair");
        var found = StereoPairs.Members(devices, PairTsid);
        if (!LeaderOnly)
        {
            if (found.Count != 2)
                throw new AirPlayException(L.F("立体声对「{0}」需要两只音箱都在线（现在找到 {1} 只）", Name, found.Count))
                {
                    LogMessage = $"stereo pair {Log.Name(Name)}: both speakers must be online (found {found.Count})",
                };
            return found;
        }
        if (found.Count == 0) throw new AirPlayException(L.T("找不到音箱：请确认它已通电、和电脑在同一网络"));
        if (StereoPairs.Leader(found) is { } leader) return [leader];
        Log.Warn($"pair {PairTsid}: no single member has the tight-sync leader bit; using {Log.Name(found[0].Name)}");
        return [found[0]];
    }

    /// <summary>Session SETUP keys every member of one connection gets: a fresh groupUUID shared by all of them.</summary>
    public IReadOnlyDictionary<string, object?>? SessionExtras(Guid groupUuid) =>
        SharedGroupUuid ? new Dictionary<string, object?> { ["groupUUID"] = groupUuid.ToString().ToUpperInvariant() } : null;

    public int VolumeOffsetFor(string deviceId) => VolumeOffsets.GetValueOrDefault(Key(deviceId));

    public bool IsRequiredOutput(string deviceId) => RequiredOutputs.Any(p => Key(p.Key) == Key(deviceId) && p.Value);

    /// <summary>Config key for a device: its id without separators, upper case.</summary>
    public static string Key(string deviceId) => StreamController.Normalize(deviceId).ToUpperInvariant();
}
