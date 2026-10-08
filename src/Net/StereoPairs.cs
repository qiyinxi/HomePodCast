using System.Globalization;

namespace HomePodCast.Net;

/// <summary>
/// What one HomePod's TXT record says about its place in a stereo pair. Read from TXT records that users
/// posted for real pairs (OwnTone issues #704, #1291, #1413) and the flag names in the open AirPlay spec:
/// <list type="bullet">
/// <item>both members advertise the same "tsid" (tight-sync id) and "gpn" (the pair's room name);</item>
/// <item>flags bit 13 (TightSyncIsGroupLeader) is set on exactly one of them, the tight-sync leader.
///   "igl"/"gcgl" don't tell them apart (both 1 when idle, both 0 while a sender plays);</item>
/// <item>bit 14 (TightSyncBuddyNotReachable) is set when the other member is gone;</item>
/// <item>while idle, "gid" may read "&lt;tsid&gt;+&lt;n&gt;+&lt;member uuid&gt;" with n = 0 or 1 (HomePod OS 15.4+).
///   In the one public sample the 0 member was the one its owner named "left" (and also the leader),
///   so n is used only as an ordering hint; nothing advertises the channel for certain.</item>
/// </list>
/// </summary>
public sealed record PairRole(
    AirPlayDevice Device,
    bool TightSyncLeader,
    bool BuddyNotReachable,
    bool SilentPrimary,
    bool SupportsRelay,
    bool SessionActive,
    int? GidIndex)
{
    public static PairRole Of(AirPlayDevice d)
    {
        long flags = StereoPairs.Flags(d);
        return new PairRole(d,
            (flags & StereoPairs.FlagTightSyncIsGroupLeader) != 0,
            (flags & StereoPairs.FlagTightSyncBuddyNotReachable) != 0,
            (flags & StereoPairs.FlagSilentPrimary) != 0,
            (flags & StereoPairs.FlagSupportsRelay) != 0,
            (flags & StereoPairs.FlagReceiverSessionIsActive) != 0,
            StereoPairs.GidIndex(d));
    }
}

/// <summary>
/// Experimental stereo-pair discovery. The two HomePods of a pair advertise separately over mDNS; both
/// carry the same tight-sync id (TXT "tsid") and the pair's room name (TXT "gpn"). For the device list
/// they are merged into one entry whose DeviceId is "pair:&lt;tsid&gt;". A standalone HomePod (no tsid,
/// or a tsid nobody else shares) is left exactly as it is.
/// </summary>
public static class StereoPairs
{
    public const string IdPrefix = "pair:";

    // TXT "flags" status bits, numbered as in https://openairplay.github.io/airplay-spec/status_flags.html
    // (the same numbers OwnTone's airplay.c uses). Bit 13 differs between the members of every real pair posted.
    internal const long FlagSupportsRelay = 1 << 11;
    internal const long FlagSilentPrimary = 1 << 12;
    internal const long FlagTightSyncIsGroupLeader = 1 << 13;
    internal const long FlagTightSyncBuddyNotReachable = 1 << 14;
    internal const long FlagReceiverSessionIsActive = 1 << 17;

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
    /// The online HomePods advertising this tsid, in a stable order: by the "gid" index when the two
    /// members advertise different ones (see <see cref="PairRole"/>), else by device id. Which side a
    /// speaker plays is not advertised for certain, so the first one only counts as left for splitting
    /// channels on the PC, and the user can swap sides.
    /// </summary>
    public static List<AirPlayDevice> Members(IReadOnlyList<AirPlayDevice> devices, string tsid)
    {
        var members = devices.Where(d => IsHomePod(d) && string.Equals(Tsid(d), tsid.Trim(), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(d => Key(d.DeviceId))
            .OrderBy(d => Key(d.DeviceId), StringComparer.Ordinal)
            .ToList();
        var indices = members.Select(GidIndex).ToList();
        bool indexed = members.Count == 2 && indices.All(i => i != null) && indices[0] != indices[1];
        return indexed ? members.OrderBy(d => GidIndex(d)).ToList() : members;
    }

    /// <summary>The tight-sync leader (flags bit 13) of a pair, or null unless exactly one member claims it.</summary>
    public static AirPlayDevice? Leader(IReadOnlyList<AirPlayDevice> members)
    {
        var leaders = members.Where(m => PairRole.Of(m).TightSyncLeader).ToList();
        return leaders.Count == 1 ? leaders[0] : null;
    }

    /// <summary>The n of an idle pair member's "gid" = "&lt;tsid&gt;+&lt;n&gt;+&lt;uuid&gt;"; null for any other form.</summary>
    public static int? GidIndex(AirPlayDevice d)
    {
        var parts = (d.Txt.GetValueOrDefault("gid") ?? "").Split('+');
        return parts.Length == 3 && Tsid(d) is { } tsid && string.Equals(parts[0].Trim(), tsid, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

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
        if (Leader(members) is { } leader) txt["leader"] = leader.DeviceId;
        return new AirPlayDevice(name, PairId(tsid), members[0].Address, members[0].Port, L.T("立体声对 · 实验性"), txt);
    }

    /// <summary>
    /// Diagnostic lines for `scan`: each tsid group, its leader and member order, and per member the TXT keys
    /// and flag bits a pair is known or suspected to differ in. With <paramref name="allHomePods"/> (scan --txt)
    /// HomePods outside any tsid group get a line too, so a pair can be compared with how it looked before pairing.
    /// </summary>
    public static IEnumerable<string> Describe(IReadOnlyList<AirPlayDevice> devices, bool allHomePods = false)
    {
        foreach (var g in devices.Where(d => Tsid(d) != null).GroupBy(d => Tsid(d)!, StringComparer.OrdinalIgnoreCase))
        {
            var members = Members(devices, g.Key);
            if (members.Count == 2)
            {
                var leader = Leader(members);
                yield return $"tsid {g.Key}: stereo pair \"{PairName(members)}\" -> {PairId(g.Key)}, " +
                             $"leader {(leader == null ? "unknown (bit 13 not set on exactly one member)" : leader.Name)}, " +
                             $"order {string.Join(", ", members.Select(m => m.Name))} " +
                             $"({(members.All(m => GidIndex(m) != null) ? "by gid index" : "by device id")})";
            }
            else
            {
                yield return $"tsid {g.Key}: not a pair";
            }
            foreach (var d in g) yield return "    " + DescribeMember(d);
        }
        if (!allHomePods) yield break;
        foreach (var d in devices.Where(d => IsHomePod(d) && Tsid(d) == null))
            yield return "no tsid: " + DescribeMember(d);
    }

    private static string DescribeMember(AirPlayDevice d)
    {
        var r = PairRole.Of(d);
        string Txt(string key) => d.Txt.GetValueOrDefault(key) ?? "-";
        return $"{d.Name} {d.Address} [{d.DeviceId}] {d.Model} flags={Txt("flags")} " +
               $"(bit13 tightSyncLeader={r.TightSyncLeader}, bit14 buddyNotReachable={r.BuddyNotReachable}, " +
               $"bit12 silentPrimary={r.SilentPrimary}, bit11 relay={r.SupportsRelay}, bit17 sessionActive={r.SessionActive}) " +
               $"igl={Txt("igl")} gcgl={Txt("gcgl")} gid={Txt("gid")} gidIndex={r.GidIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
               $"pgid={Txt("pgid")} pgcgl={Txt("pgcgl")} gpn={Txt("gpn")} tsm={Txt("tsm")} psi={Txt("psi")} osvers={Txt("osvers")}";
    }

    internal static long Flags(AirPlayDevice d)
    {
        var s = d.Txt.GetValueOrDefault("flags") ?? "";
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string Key(string deviceId) => deviceId.Replace(":", "").Replace("-", "").ToUpperInvariant();
}
