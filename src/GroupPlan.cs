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
    /// <summary>Stereo pair only: one session, to the tight-sync leader (experiment, off by default).</summary>
    public bool PairLeaderOnly { get; init; }

    /// <summary>Send every member's session SETUP the same "groupUUID" (on by default; `group --no-group-uuid` for tests).</summary>
    public bool SharedGroupUuid { get; init; } = true;

    public static GroupPlan? FromConfig(AppConfig cfg)
    {
        if (cfg.DeviceId is not { } id) return null;
        var offsets = new Dictionary<string, int>(cfg.GroupVolumeOffsets ?? new());
        if (StereoPairs.TryGetTsid(id, out var tsid))
            return new GroupPlan(GroupKind.StereoPair, cfg.DeviceName ?? tsid, tsid, [],
                cfg.GroupSplitChannels, cfg.GroupSwapChannels, offsets) { PairLeaderOnly = cfg.GroupPairLeaderOnly };
        if (cfg.MultiRoomDeviceId is { } extra && !StereoPairs.IsPairId(extra) && Key(extra) != Key(id))
            return new GroupPlan(GroupKind.MultiRoom, $"{cfg.DeviceName ?? id} + {cfg.MultiRoomDeviceName ?? extra}", null,
                [new GroupMemberRef(id, cfg.DeviceName ?? id, cfg.Host), new GroupMemberRef(extra, cfg.MultiRoomDeviceName ?? extra, cfg.MultiRoomHost)],
                cfg.GroupSplitChannels, cfg.GroupSwapChannels, offsets);
        return null;
    }

    private bool LeaderOnly => Kind == GroupKind.StereoPair && PairLeaderOnly;

    /// <summary>
    /// Channels for the speaker at this position (members in their stable order). By default every speaker
    /// gets full stereo (a stereo pair picks its own side from it); with split channels the first is left and
    /// the second right, unless swapped. The leader alone always gets full stereo.
    /// </summary>
    public ChannelMode ChannelsFor(int index) =>
        !SplitChannels || LeaderOnly || index > 1 ? ChannelMode.Stereo
        : (index == 0) != SwapChannels ? ChannelMode.LeftOnly : ChannelMode.RightOnly;

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
                throw new AirPlayException(L.F("立体声对「{0}」需要两只音箱都在线（现在找到 {1} 只）", Name, found.Count));
            return found;
        }
        if (found.Count == 0) throw new AirPlayException(L.T("找不到音箱（是否通电、和电脑在同一网络？）"));
        if (StereoPairs.Leader(found) is { } leader) return [leader];
        Log.Warn($"pair {PairTsid}: no single member has the tight-sync leader bit; using {found[0].Name}");
        return [found[0]];
    }

    /// <summary>Session SETUP keys every member of one connection gets: a fresh groupUUID shared by all of them.</summary>
    public IReadOnlyDictionary<string, object?>? SessionExtras(Guid groupUuid) =>
        SharedGroupUuid ? new Dictionary<string, object?> { ["groupUUID"] = groupUuid.ToString().ToUpperInvariant() } : null;

    public int VolumeOffsetFor(string deviceId) => VolumeOffsets.GetValueOrDefault(Key(deviceId));

    /// <summary>Config key for a device: its id without separators, upper case.</summary>
    public static string Key(string deviceId) => StreamController.Normalize(deviceId).ToUpperInvariant();
}
