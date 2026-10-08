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
public sealed record GroupPlan(
    GroupKind Kind,
    string Name,
    string? PairTsid,
    IReadOnlyList<GroupMemberRef> Members,
    bool SplitChannels,
    bool SwapChannels,
    IReadOnlyDictionary<string, int> VolumeOffsets)
{
    public static GroupPlan? FromConfig(AppConfig cfg)
    {
        if (cfg.DeviceId is not { } id) return null;
        var offsets = new Dictionary<string, int>(cfg.GroupVolumeOffsets ?? new());
        if (StereoPairs.TryGetTsid(id, out var tsid))
            return new GroupPlan(GroupKind.StereoPair, cfg.DeviceName ?? tsid, tsid, [],
                cfg.GroupSplitChannels, cfg.GroupSwapChannels, offsets);
        if (cfg.MultiRoomDeviceId is { } extra && !StereoPairs.IsPairId(extra) && Key(extra) != Key(id))
            return new GroupPlan(GroupKind.MultiRoom, $"{cfg.DeviceName ?? id} + {cfg.MultiRoomDeviceName ?? extra}", null,
                [new GroupMemberRef(id, cfg.DeviceName ?? id, cfg.Host), new GroupMemberRef(extra, cfg.MultiRoomDeviceName ?? extra, cfg.MultiRoomHost)],
                cfg.GroupSplitChannels, cfg.GroupSwapChannels, offsets);
        return null;
    }

    /// <summary>
    /// Channels for the speaker at this position (members in their stable order). By default every speaker
    /// gets full stereo; with split channels the first is left and the second right, unless swapped.
    /// </summary>
    public ChannelMode ChannelsFor(int index) =>
        !SplitChannels || index > 1 ? ChannelMode.Stereo
        : (index == 0) != SwapChannels ? ChannelMode.LeftOnly : ChannelMode.RightOnly;

    public int VolumeOffsetFor(string deviceId) => VolumeOffsets.GetValueOrDefault(Key(deviceId));

    /// <summary>Config key for a device: its id without separators, upper case.</summary>
    public static string Key(string deviceId) => StreamController.Normalize(deviceId).ToUpperInvariant();
}
