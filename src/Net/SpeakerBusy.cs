using System.Net;

namespace HomePodCast.Net;

/// <summary>A speaker to check before connecting: its device id, a name for when /info can't be read, its address.</summary>
public sealed record BusyTarget(string DeviceId, string Name, IPAddress? Address);

/// <summary>
/// The "receiver session is active" flag of every speaker a connection would use: Busy if any member is, else Unknown
/// if any member could not be read, else Free. <see cref="BusyNames"/> are the busy members' own names; <see cref="Detail"/>
/// is for the log (names marked by <see cref="Log.Name"/>).
/// </summary>
public sealed record BusyReading(SpeakerBusy State, IReadOnlyList<string> BusyNames, string Detail)
{
    /// <summary>
    /// Who holds the busy speakers: <see cref="BusyHolder.HomeTheater"/> when every busy member is a HomePod held by its
    /// own home-theater Apple TV (the same one), else <see cref="BusyHolder.OtherSender"/>; None while nothing is busy.
    /// </summary>
    public BusyHolder Holder { get; init; }

    /// <summary>That Apple TV (<see cref="BusyHolder.HomeTheater"/> only).</summary>
    public AirPlayDevice? HomeTheater { get; init; }

    /// <param name="HomeTheater">A busy member's home-theater Apple TV holding it now (<see cref="Net.HomeTheater.HolderOf(GroupMembership, IReadOnlyList{AirPlayDevice})"/>).</param>
    public sealed record Member(string Name, SpeakerBusy State, string Detail, AirPlayDevice? HomeTheater = null);

    public static BusyReading Combine(IReadOnlyList<Member> members)
    {
        var busyMembers = members.Where(m => m.State == SpeakerBusy.Busy).ToList();
        var state = busyMembers.Count > 0 ? SpeakerBusy.Busy
            : members.Count == 0 || members.Any(m => m.State == SpeakerBusy.Unknown) ? SpeakerBusy.Unknown
            : SpeakerBusy.Free;
        var tvs = busyMembers.Select(m => m.HomeTheater).ToList();
        var tv = tvs.Count > 0 && tvs.All(t => t != null) && tvs.Select(t => Net.HomeTheater.Key(t!.DeviceId)).Distinct().Count() == 1 ? tvs[0] : null;
        return new BusyReading(state, busyMembers.Select(m => m.Name).ToList(),
            members.Count == 0 ? "no speakers to read" : string.Join("; ", members.Select(m => m.Detail)))
        {
            Holder = busyMembers.Count == 0 ? BusyHolder.None : tv != null ? BusyHolder.HomeTheater : BusyHolder.OtherSender,
            HomeTheater = tv,
        };
    }

    /// <summary>One member from its /info read.</summary>
    /// <param name="scan">
    /// The last scan, as found (pairs not merged): the Apple TVs a busy HomePod may be the home-theater speaker of. Empty:
    /// no home theater is recognized.
    /// </param>
    public static Member Of(BusyTarget target, SpeakerInfoEntry entry, IReadOnlyList<AirPlayDevice>? scan = null)
    {
        string name = entry.Info?.GetValueOrDefault("name") as string is { Length: > 0 } n ? n : target.Name;
        string where = $"{Log.Name(name)} {target.Address?.ToString() ?? "-"}";
        if (entry.State != SpeakerInfoState.Ready || entry.Info == null)
            return new Member(name, SpeakerBusy.Unknown, $"{where} unreadable ({entry.Error ?? "no answer"})");
        var txt = TxtRecord.FromInfo(entry.Info);
        var flags = StatusFlags.FromInfo(entry.Info) ?? StatusFlags.FromTxt(txt);
        if (flags is not { } f) return new Member(name, SpeakerBusy.Unknown, $"{where} has no status flags");
        bool active = StatusFlags.SessionActive(f);
        string detail = $"{where} {SpeakerInfoCache.DescribeFlags(entry.Info)} → {(active ? "busy" : "free")}";
        if (!active) return new Member(name, SpeakerBusy.Free, detail);

        // Who holds it: the group fields of the TXT inside /info (fresh), else of the last scan.
        string? model = entry.Info.GetValueOrDefault("model") as string;
        if (Net.HomeTheater.IsAppleTvModel(model) || scan is not { Count: > 0 }) return new Member(name, SpeakerBusy.Busy, detail);
        string key = Net.HomeTheater.Key(target.DeviceId);
        var group = GroupMembership.FromTxt(txt ?? scan.FirstOrDefault(d => Net.HomeTheater.Key(d.DeviceId) == key)?.Txt);
        var tv = Net.HomeTheater.HolderOf(group, scan);
        if (tv != null) detail += $", held by its home-theater Apple TV {Log.Name(tv.Name)} ({group.Describe()})";
        else if (group.PersistentGroupId != null) detail += $", a home-theater speaker in another group ({group.Describe()})";
        return new Member(name, SpeakerBusy.Busy, detail, tv);
    }
}

/// <summary>Who holds a busy speaker (<see cref="BusyReading.Holder"/>).</summary>
public enum BusyHolder
{
    /// <summary>Nothing is busy.</summary>
    None,

    /// <summary>Another sender (an iPhone, a Mac, an unknown one), or not every busy speaker is held by a home theater.</summary>
    OtherSender,

    /// <summary>Every busy speaker is a HomePod held by its own home-theater Apple TV (the group leader).</summary>
    HomeTheater,
}

/// <summary>Reads the flag of the speakers a connection would use (tests replace it: no network).</summary>
public interface ISpeakerBusyReader
{
    Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct);
}

/// <summary>Reads the flag from each speaker's GET /info, all at once, through the cache (so 设置 shows what was read).</summary>
/// <param name="scan">The last scan (pairs not merged), to recognize a HomePod its home-theater Apple TV holds.</param>
public sealed class InfoBusyReader(SpeakerInfoCache cache, Func<IReadOnlyList<AirPlayDevice>>? scan = null) : ISpeakerBusyReader
{
    public async Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct)
    {
        var entries = await Task.WhenAll(targets.Select(t => cache.RefreshAsync(t.DeviceId, t.Name, t.Address, log: false))).WaitAsync(ct);
        var devices = scan?.Invoke() ?? [];
        return BusyReading.Combine(targets.Zip(entries, (t, e) => BusyReading.Of(t, e, devices)).ToList());
    }
}
