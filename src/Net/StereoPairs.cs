using System.Globalization;

namespace HomePodCast.Net;

/// <summary>
/// Experimental stereo-pair discovery. The two HomePods of a pair advertise separately over mDNS; both
/// carry the same tight-sync id (TXT "tsid") and the pair's room name (TXT "gpn"). For the device list
/// they are merged into one entry whose DeviceId is "pair:&lt;tsid&gt;". A standalone HomePod (no tsid,
/// or a tsid nobody else shares) is left exactly as it is.
/// </summary>
public static class StereoPairs
{
    public const string IdPrefix = "pair:";

    // TXT "flags" bits as documented by the open AirPlay 2 receiver projects; unverified on real pairs.
    private const long FlagTightSyncIsGroupLeader = 1 << 13;
    private const long FlagTightSyncBuddyNotReachable = 1 << 14;

    public static bool IsPairId(string? id) => id != null && id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    public static string PairId(string tsid) => IdPrefix + tsid.Trim().ToUpperInvariant();

    public static bool TryGetTsid(string? id, out string tsid)
    {
        tsid = IsPairId(id) ? id![IdPrefix.Length..] : "";
        return tsid.Length > 0;
    }

    public static string? Tsid(AirPlayDevice d) =>
        d.Txt.TryGetValue("tsid", out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;

    /// <summary>
    /// Pairs are made of HomePods only. This also keeps out e.g. an Apple TV that shares a home-theater
    /// tight-sync id with a HomePod, which must stay usable as a normal single speaker.
    /// </summary>
    public static bool IsHomePod(AirPlayDevice d) => d.Model.StartsWith("AudioAccessory", StringComparison.Ordinal);

    /// <summary>
    /// The device list with every complete pair (exactly two HomePods sharing a tsid) replaced by one entry.
    /// A pair with one member offline is not merged: connecting as a pair needs both.
    /// </summary>
    public static List<AirPlayDevice> Merge(IReadOnlyList<AirPlayDevice> devices)
    {
        var pairs = devices.Where(d => IsHomePod(d) && Tsid(d) != null)
            .GroupBy(d => Tsid(d)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => Members(devices, g.Key))
            .Where(m => m.Count == 2)
            .ToList();
        var paired = pairs.SelectMany(m => m).Select(d => Key(d.DeviceId)).ToHashSet();

        var result = devices.Where(d => !paired.Contains(Key(d.DeviceId))).ToList();
        result.AddRange(pairs.Select(Entry));
        return result.OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// The online HomePods advertising this tsid, ordered by device id. No TXT key is known to say which
    /// side of the pair a speaker plays (tsid, gid, gpn, igl, gcgl and flags don't), so this order is only
    /// made stable — the first one counts as left — and the user swaps sides if it's wrong.
    /// </summary>
    public static List<AirPlayDevice> Members(IReadOnlyList<AirPlayDevice> devices, string tsid) =>
        devices.Where(d => IsHomePod(d) && string.Equals(Tsid(d), tsid.Trim(), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(d => Key(d.DeviceId))
            .OrderBy(d => Key(d.DeviceId), StringComparer.Ordinal)
            .ToList();

    /// <summary>Room name of a pair: the members' "gpn", else the first member's own name.</summary>
    public static string PairName(IReadOnlyList<AirPlayDevice> members) =>
        members.Select(m => m.Txt.GetValueOrDefault("gpn")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
        ?? members[0].Name;

    private static AirPlayDevice Entry(List<AirPlayDevice> members)
    {
        string tsid = Tsid(members[0])!;
        string name = PairName(members);
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tsid"] = tsid,
            ["gpn"] = name,
            ["members"] = string.Join(",", members.Select(m => m.DeviceId)),
        };
        return new AirPlayDevice(name, PairId(tsid), members[0].Address, members[0].Port, L.T("立体声对 · 实验性"), txt);
    }

    /// <summary>Diagnostic lines for `scan`: each tsid group with the TXT keys a pair is expected to differ in.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<AirPlayDevice> devices)
    {
        foreach (var g in devices.Where(d => Tsid(d) != null).GroupBy(d => Tsid(d)!, StringComparer.OrdinalIgnoreCase))
        {
            var members = Members(devices, g.Key);
            yield return $"tsid {g.Key}: {(members.Count == 2 ? $"stereo pair \"{PairName(members)}\" -> {PairId(g.Key)}" : "not a pair")}";
            foreach (var d in g)
            {
                long flags = Flags(d);
                yield return $"    {d.Name} {d.Address} [{d.DeviceId}] {d.Model} gid={d.Txt.GetValueOrDefault("gid")} " +
                             $"gpn={d.Txt.GetValueOrDefault("gpn")} igl={d.Txt.GetValueOrDefault("igl")} gcgl={d.Txt.GetValueOrDefault("gcgl")} " +
                             $"flags={d.Txt.GetValueOrDefault("flags")} (bit13 tightSyncLeader={(flags & FlagTightSyncIsGroupLeader) != 0}, " +
                             $"bit14 buddyNotReachable={(flags & FlagTightSyncBuddyNotReachable) != 0})";
            }
        }
    }

    internal static long Flags(AirPlayDevice d)
    {
        var s = d.Txt.GetValueOrDefault("flags") ?? "";
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string Key(string deviceId) => deviceId.Replace(":", "").Replace("-", "").ToUpperInvariant();
}
