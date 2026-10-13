using System.Globalization;

namespace HomePodCast.Net;

/// <summary>
/// The AirPlay groups a speaker's TXT record (an mDNS answer, or the "txtAirPlay" inside its GET /info) says it is in.
/// gid / igl / gcgl / pgid / pgcgl are named in the open AirPlay spec and OwnTone; psgid / psgsz / psgtp and pgm / pgmid
/// are documented nowhere public: what they mean here is read from the user's scan of 2026-10-12, an Apple TV 4K (3rd
/// generation, tvOS 27.0) with a HomePod (2nd generation) set up as its home-theater speaker:
/// <code>
/// Apple TV  igl=1 gcgl=1 gid=AAD5… psgid=AAD5… psgsz=2 psgtp=1 pgm=2 pgmid=0 flags=0x644
/// HomePod   igl=0 gcgl=1 gid=AAD5… pgid=AAD5… pgcgl=1 psgid=AAD5… psgsz=2 psgtp=1 pgm=2 pgmid=1 flags=0xa0c04
/// </code>
/// Both carry the Apple TV's group id as their persistent group (psg*: a group of two, type 1); the Apple TV leads it
/// (igl=1, member 0), the HomePod is member 1 whose parent group is the Apple TV's, and it holds the HomePod's
/// "receiver session is active" flag (bit 17) the whole time, with bit 11 (relay) set. An idle standalone HomePod has
/// no psgid and leads its own group (igl=1); a HomePod an iPhone plays to advertises the iPhone's group as gid / pgid
/// (OwnTone #1413).
/// </summary>
/// <param name="GroupId">gid: the group the speaker is in now.</param>
/// <param name="IsGroupLeader">igl: it leads that group.</param>
/// <param name="GroupContainsLeader">gcgl.</param>
/// <param name="ParentGroupId">pgid: the group above it (a home-theater HomePod: its Apple TV's gid).</param>
/// <param name="ParentContainsLeader">pgcgl.</param>
/// <param name="Pgm">pgm (2 on both devices of the home theater; meaning unknown).</param>
/// <param name="PgmId">pgmid (0 on the Apple TV, 1 on its HomePod: apparently the member's index).</param>
/// <param name="PersistentGroupId">psgid: a group that outlives sessions (the home theater: the Apple TV's gid).</param>
/// <param name="PersistentGroupSize">psgsz: how many devices that group has (2: the Apple TV and one HomePod).</param>
/// <param name="PersistentGroupType">psgtp (1 for the home theater; other values not seen yet).</param>
/// <param name="SupportsRelay">flags bit 11, DeviceSupportsRelay: set on the HomePod while its Apple TV holds it.</param>
public sealed record GroupMembership(
    string? GroupId,
    bool? IsGroupLeader,
    bool? GroupContainsLeader,
    string? ParentGroupId,
    bool? ParentContainsLeader,
    int? Pgm,
    int? PgmId,
    string? PersistentGroupId,
    int? PersistentGroupSize,
    int? PersistentGroupType,
    bool SupportsRelay)
{
    public static readonly GroupMembership None = FromTxt(null);

    public static GroupMembership FromTxt(IReadOnlyDictionary<string, string>? txt)
    {
        string? Text(string key) => txt?.GetValueOrDefault(key) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        bool? Flag(string key) => Text(key) switch
        {
            "1" => true,
            "0" => false,
            { } v when bool.TryParse(v, out var b) => b,
            _ => null,
        };
        int? Number(string key) => int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new GroupMembership(Text("gid"), Flag("igl"), Flag("gcgl"), Text("pgid"), Flag("pgcgl"), Number("pgm"), Number("pgmid"),
            Text("psgid"), Number("psgsz"), Number("psgtp"), ((StatusFlags.FromTxt(txt) ?? 0) & StereoPairs.FlagSupportsRelay) != 0);
    }

    /// <summary>The group the speaker plays in now: its parent group when it has one, else its own.</summary>
    public string? CurrentGroupId => ParentGroupId ?? GroupId;

    /// <summary>"gid=… igl=1 …": every group key, for the log and `scan`.</summary>
    public string Describe()
    {
        static string B(bool? b) => b switch { true => "1", false => "0", null => "-" };
        static string N(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "-";
        return $"gid={GroupId ?? "-"} igl={B(IsGroupLeader)} gcgl={B(GroupContainsLeader)} pgid={ParentGroupId ?? "-"} " +
               $"pgcgl={B(ParentContainsLeader)} pgm={N(Pgm)} pgmid={N(PgmId)} psgid={PersistentGroupId ?? "-"} psgsz={N(PersistentGroupSize)} " +
               $"psgtp={N(PersistentGroupType)} relay={SupportsRelay}";
    }
}

/// <summary>
/// An Apple TV with HomePods as its home-theater speakers (see <see cref="GroupMembership"/> for what that looks like).
/// Such an Apple TV keeps a session with its HomePod all the time, idle or not, so the HomePod's busy flag alone can't
/// tell it apart from an iPhone playing to it; the group fields can.
/// <list type="bullet">
/// <item>A speaker of the home theater (<see cref="OwnerOf(GroupMembership, IReadOnlyList{AirPlayDevice})"/>): its psgid is
///   the Apple TV's group (gid or psgid). That lasts as long as the home-theater setting, whoever plays to the HomePod now.</item>
/// <item>Held by it now (<see cref="HolderOf(GroupMembership, IReadOnlyList{AirPlayDevice})"/>): besides, the Apple TV leads
///   its group (igl=1) and the HomePod plays in
///   it (its pgid, or gid, is the Apple TV's gid; igl=0). Another sender playing to the HomePod moves it into that
///   sender's group, so a HomePod an iPhone took over is not "held by the Apple TV" even though it stays a member.</item>
/// </list>
/// Not advertised anywhere as far as is known: whether the Apple TV is playing anything. Its own flags (0x644 in the scan
/// above, read while nothing played) have no bit 17. The HomePod's 0xa0c04 differs from what our own session showed
/// (0x1A0404: bit 20 too, no bit 11), so bit 20 might mark audio actually flowing, but no scan while the Apple TV plays
/// has been seen yet (docs/device-test-checklist.zh-CN.md asks for one). An iPhone playing through the Apple TV may show
/// up as another group (then the HomePod is not "held by the Apple TV" and is never taken over automatically); the Apple
/// TV playing its own apps can't be told apart from an idle one.
/// </summary>
public static class HomeTheater
{
    public static bool IsAppleTv(AirPlayDevice d) => IsAppleTvModel(d.Model);

    public static bool IsAppleTvModel(string? model) => model?.StartsWith("AppleTV", StringComparison.Ordinal) == true;

    /// <summary>The Apple TV (in <paramref name="devices"/>) whose home theater this speaker belongs to; null for none.</summary>
    public static AirPlayDevice? OwnerOf(AirPlayDevice speaker, IReadOnlyList<AirPlayDevice> devices) =>
        IsAppleTv(speaker) ? null : OwnerOf(GroupMembership.FromTxt(speaker.Txt), devices);

    public static AirPlayDevice? OwnerOf(GroupMembership speaker, IReadOnlyList<AirPlayDevice> devices)
    {
        if (speaker.PersistentGroupId is not { } group) return null;
        return devices.Where(IsAppleTv).FirstOrDefault(tv =>
        {
            var g = GroupMembership.FromTxt(tv.Txt);
            return Same(g.GroupId, group) || Same(g.PersistentGroupId, group);
        });
    }

    /// <summary>
    /// The home-theater Apple TV whose group this speaker plays in now (the one holding its busy flag), or null: no home
    /// theater, the Apple TV not found or not leading, or another sender's group.
    /// </summary>
    public static AirPlayDevice? HolderOf(AirPlayDevice speaker, IReadOnlyList<AirPlayDevice> devices) =>
        IsAppleTv(speaker) ? null : HolderOf(GroupMembership.FromTxt(speaker.Txt), devices);

    public static AirPlayDevice? HolderOf(GroupMembership speaker, IReadOnlyList<AirPlayDevice> devices)
    {
        if (speaker.IsGroupLeader == true || OwnerOf(speaker, devices) is not { } tv) return null;
        var g = GroupMembership.FromTxt(tv.Txt);
        return g.IsGroupLeader == true && g.GroupId != null && Same(speaker.CurrentGroupId, g.GroupId) ? tv : null;
    }

    /// <summary>The Apple TV leading this HomePod's home theater (the same as <see cref="OwnerOf(AirPlayDevice, IReadOnlyList{AirPlayDevice})"/>).</summary>
    public static AirPlayDevice? LeaderOf(AirPlayDevice homepod, IReadOnlyList<AirPlayDevice> devices) => OwnerOf(homepod, devices);

    /// <summary>The speakers (in <paramref name="devices"/>, as found: pairs not merged) of an Apple TV's home theater.</summary>
    public static List<AirPlayDevice> SpeakersOf(AirPlayDevice appleTv, IReadOnlyList<AirPlayDevice> devices) =>
        devices.Where(d => !IsAppleTv(d) && OwnerOf(d, devices) is { } tv && Key(tv.DeviceId) == Key(appleTv.DeviceId))
            .DistinctBy(d => Key(d.DeviceId))
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>
    /// The speakers' names for the device list ("卧室"; a stereo pair by its room name; several joined with " / ", as the
    /// busy speakers are); null when none was found.
    /// </summary>
    public static string? SpeakerNames(AirPlayDevice appleTv, IReadOnlyList<AirPlayDevice> devices)
    {
        var names = SpeakersOf(appleTv, devices)
            .Select(d => StereoPairs.Tsid(d) is { } tsid && StereoPairs.Members(devices, tsid) is { Count: 2 } pair ? StereoPairs.PairName(pair) : d.Name)
            .Distinct()
            .ToList();
        return names.Count == 0 ? null : string.Join(" / ", names);
    }

    /// <summary>Diagnostic lines for `scan`: every home theater found, its speakers and their group keys.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<AirPlayDevice> devices)
    {
        foreach (var tv in devices.Where(IsAppleTv))
        {
            var speakers = SpeakersOf(tv, devices);
            if (speakers.Count == 0) continue;
            yield return $"home theater: {tv.Name} [{tv.DeviceId}] {tv.Model} {GroupMembership.FromTxt(tv.Txt).Describe()} flags={tv.Txt.GetValueOrDefault("flags") ?? "-"}";
            foreach (var s in speakers)
            {
                var flags = StatusFlags.FromTxt(s.Txt);
                yield return $"    {s.Name} [{s.DeviceId}] {s.Model} {GroupMembership.FromTxt(s.Txt).Describe()} " +
                             $"flags={(flags is { } f ? StatusFlags.Describe(f) : "-")} held by it now={HolderOf(s, devices) != null}";
            }
        }
    }

    internal static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string Key(string deviceId) => StreamController.Normalize(deviceId).ToUpperInvariant();
}

/// <summary>What 设置 → 音箱信息 says about a speaker's group (<see cref="Of"/>).</summary>
public enum GroupRoleKind
{
    None,

    /// <summary>A HomePod of an Apple TV's home theater; <see cref="GroupRole.Name"/> is the Apple TV's (null: not found).</summary>
    HomeTheaterSpeaker,

    /// <summary>An Apple TV with home-theater speakers; <see cref="GroupRole.Name"/> names them (null: none found).</summary>
    HomeTheaterLeader,

    /// <summary>One HomePod of a stereo pair; <see cref="GroupRole.Name"/> is the pair's room name.</summary>
    StereoPair,
}

public sealed record GroupRole(GroupRoleKind Kind, string? Name)
{
    public static readonly GroupRole None = new(GroupRoleKind.None, null);

    /// <param name="model">The speaker's model (TXT or /info).</param>
    /// <param name="txt">Its TXT record (from /info when read, else the scan's).</param>
    /// <param name="devices">The last scan, as found (pairs not merged).</param>
    /// <summary>
    /// The psgtp of the one home theater seen (an Apple TV and a HomePod). Without the Apple TV in the scan, only a
    /// persistent group of this type counts as a home theater: what else may use psgid (a stereo pair on a newer
    /// HomePod system?) is not known.
    /// </summary>
    internal const int HomeTheaterGroupType = 1;

    /// <param name="model">The speaker's model (TXT or /info).</param>
    /// <param name="txt">Its TXT record (from /info when read, else the scan's).</param>
    /// <param name="devices">The last scan, as found (pairs not merged).</param>
    public static GroupRole Of(string? model, IReadOnlyDictionary<string, string>? txt, IReadOnlyList<AirPlayDevice> devices)
    {
        if (txt == null) return None;
        var g = GroupMembership.FromTxt(txt);
        string? tsid = txt.GetValueOrDefault("tsid") is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
        if (HomeTheater.IsAppleTvModel(model ?? txt.GetValueOrDefault("model")))
        {
            string? id = txt.GetValueOrDefault("deviceid");
            var self = devices.FirstOrDefault(d => HomeTheater.IsAppleTv(d) &&
                (id != null && HomeTheater.Key(d.DeviceId) == HomeTheater.Key(id) || HomeTheater.Same(GroupMembership.FromTxt(d.Txt).GroupId, g.GroupId)));
            string? names = self == null ? null : HomeTheater.SpeakerNames(self, devices);
            bool leads = g.IsGroupLeader == true && g.PersistentGroupType == HomeTheaterGroupType && g.PersistentGroupSize is null or > 1;
            return names != null || leads ? new GroupRole(GroupRoleKind.HomeTheaterLeader, names) : None;
        }
        if (HomeTheater.OwnerOf(g, devices) is { } owner) return new GroupRole(GroupRoleKind.HomeTheaterSpeaker, owner.Name);
        if (tsid == null && g.PersistentGroupType == HomeTheaterGroupType && g.IsGroupLeader != true)
            return new GroupRole(GroupRoleKind.HomeTheaterSpeaker, null);
        if (tsid != null)
        {
            var members = StereoPairs.Members(devices, tsid);
            if (members.Count != 2 && !txt.ContainsKey("gpn")) return None;
            string? name = txt.GetValueOrDefault("gpn") is { } gpn && !string.IsNullOrWhiteSpace(gpn) ? gpn.Trim()
                : members.Count == 2 ? StereoPairs.PairName(members) : null;
            return new GroupRole(GroupRoleKind.StereoPair, name);
        }
        return None;
    }
}
