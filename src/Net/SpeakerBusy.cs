using System.Net;

namespace HomePodCast.Net;

/// <summary>A speaker to check before connecting: its device id, a name for when /info can't be read, its address.</summary>
public sealed record BusyTarget(string DeviceId, string Name, IPAddress? Address);

/// <summary>
/// The "receiver session is active" flag of every speaker a connection would use: Busy if any member is, else Unknown
/// if any member could not be read, else Free. <see cref="BusyNames"/> are the busy members' own names; <see cref="Detail"/>
/// is for the log.
/// </summary>
public sealed record BusyReading(SpeakerBusy State, IReadOnlyList<string> BusyNames, string Detail)
{
    public sealed record Member(string Name, SpeakerBusy State, string Detail);

    public static BusyReading Combine(IReadOnlyList<Member> members)
    {
        var busy = members.Where(m => m.State == SpeakerBusy.Busy).Select(m => m.Name).ToList();
        var state = busy.Count > 0 ? SpeakerBusy.Busy
            : members.Count == 0 || members.Any(m => m.State == SpeakerBusy.Unknown) ? SpeakerBusy.Unknown
            : SpeakerBusy.Free;
        return new BusyReading(state, busy, members.Count == 0 ? "no speakers to read" : string.Join("; ", members.Select(m => m.Detail)));
    }

    /// <summary>One member from its /info read.</summary>
    public static Member Of(BusyTarget target, SpeakerInfoEntry entry)
    {
        string name = entry.Info?.GetValueOrDefault("name") as string is { Length: > 0 } n ? n : target.Name;
        string where = $"{name} {target.Address?.ToString() ?? "-"}";
        if (entry.State != SpeakerInfoState.Ready || entry.Info == null)
            return new Member(name, SpeakerBusy.Unknown, $"{where} unreadable ({entry.Error ?? "no answer"})");
        var flags = StatusFlags.FromInfo(entry.Info) ?? StatusFlags.FromTxt(TxtRecord.FromInfo(entry.Info));
        if (flags is not { } f) return new Member(name, SpeakerBusy.Unknown, $"{where} has no status flags");
        bool active = StatusFlags.SessionActive(f);
        return new Member(name, active ? SpeakerBusy.Busy : SpeakerBusy.Free,
            $"{where} {SpeakerInfoCache.DescribeFlags(entry.Info)} → {(active ? "busy" : "free")}");
    }
}

/// <summary>Reads the flag of the speakers a connection would use (tests replace it: no network).</summary>
public interface ISpeakerBusyReader
{
    Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct);
}

/// <summary>Reads the flag from each speaker's GET /info, all at once, through the cache (so 设置 shows what was read).</summary>
public sealed class InfoBusyReader(SpeakerInfoCache cache) : ISpeakerBusyReader
{
    public async Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct)
    {
        var entries = await Task.WhenAll(targets.Select(t => cache.RefreshAsync(t.DeviceId, t.Name, t.Address, log: false))).WaitAsync(ct);
        return BusyReading.Combine(targets.Zip(entries, BusyReading.Of).ToList());
    }
}
