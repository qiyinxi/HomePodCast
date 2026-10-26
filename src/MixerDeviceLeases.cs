using HomePodCast.Net;

namespace HomePodCast;

public enum MixerLeaseError { None, Occupied, DuplicatePhysicalDevice, UnresolvedPair, UnconfirmedIdentity }
public sealed record MixerLeaseConflict(MixerLeaseError Error, string Device, string Owner);

/// <summary>Physical identities, never speaker names or IP addresses. Explicit aliases are confirmed saved identities.</summary>
public static class MixerDeviceIdentities
{
    public static string Canonical(string id)
    {
        int colon = id.IndexOf(':');
        if (colon < 0) return "airplay:" + GroupPlan.Key(id);
        string protocol = id[..colon].ToLowerInvariant(), value = id[(colon + 1)..];
        return protocol switch
        {
            "pair" => "pair:" + value.Trim().ToUpperInvariant(),
            "airplay" or "raop" => "airplay:" + GroupPlan.Key(value),
            "local" => "local:" + LocalGroupSettings.Key(value),
            "dlna" => "dlna:" + DlnaGroupSettings.Key(value),
            _ => "airplay:" + GroupPlan.Key(id),
        };
    }

    public static IReadOnlyList<string>? Outputs(ResolvedMixerDestination destination, IReadOnlyList<AirPlayDevice> scan)
    {
        if (destination.Config is not { } config) return [];
        var group = GroupPlan.FromConfig(config);
        var outputs = new List<string>();
        if (group?.PairTsid is { } tsid)
        {
            var members = StereoPairs.Members(scan, tsid);
            outputs.Add("pair:" + tsid);
            outputs.AddRange(members.Select(d => "airplay:" + d.DeviceId));
        }
        else if (group != null) outputs.AddRange(group.Members.Select(m => "airplay:" + m.DeviceId));
        else if (config.DeviceId != null) outputs.Add("airplay:" + config.DeviceId);
        if (group != null)
        {
            outputs.AddRange(group.LocalOutputs.Select(o => "local:" + o.DeviceId));
            outputs.AddRange(group.DlnaOutputs.Select(o => "dlna:" + o.Udn));
        }
        return outputs.Select(Canonical).ToArray();
    }
}

/// <summary>Shared with file playback and calibration. A lease spans connect, retries, takeover and complete teardown.</summary>
public sealed class MixerDeviceLeaseTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _owners = [];
    private sealed record Entry(string Owner, string[] Outputs, Dictionary<string, string> Identities);

    public MixerLeaseConflict? Validate(IReadOnlyDictionary<string, IReadOnlyList<string>> desired,
        IReadOnlyDictionary<string, string>? identities = null, IReadOnlySet<string>? replacing = null)
    { lock (_gate) return ValidateLocked(desired, identities ?? new Dictionary<string, string>(), replacing); }

    private MixerLeaseConflict? ValidateLocked(IReadOnlyDictionary<string, IReadOnlyList<string>> desired,
        IReadOnlyDictionary<string, string> identities, IReadOnlySet<string>? replacing)
    {
        var entries = _owners.Values.Where(e => replacing?.Contains(e.Owner) != true)
            .Concat(desired.Select(p => new Entry(p.Key, p.Value.Select(MixerDeviceIdentities.Canonical).ToArray(),
                Normalize(identities)))).ToArray();
        var used = new Dictionary<string, string>();
        var previous = new List<(string Owner, string Output, bool Confirmed)>();
        foreach (var e in entries)
        foreach (var output in e.Outputs)
        {
            bool confirmed = e.Identities.TryGetValue(output, out string? physical);
            string key = confirmed ? "physical:" + physical : output;
            if (used.TryGetValue(key, out string? owner))
                return new(owner == e.Owner ? MixerLeaseError.DuplicatePhysicalDevice : MixerLeaseError.Occupied, output, owner);
            string protocol = output.Split(':')[0];
            bool pairMarker = protocol == "pair" && e.Outputs.Count(o => o.StartsWith("airplay:")) == 2;
            foreach (var other in previous)
            {
                // Within an existing mixed group there is no second destination. Across destinations a user must
                // confirm unlike protocol identities instead of us guessing from DHCP addresses or room names.
                if (!pairMarker && other.Owner != e.Owner && other.Output.Split(':')[0] != protocol && (!confirmed || !other.Confirmed))
                {
                    if (protocol == "pair" || other.Output.StartsWith("pair:"))
                        return new(MixerLeaseError.UnresolvedPair, output, other.Owner);
                    return new(MixerLeaseError.UnconfirmedIdentity, output, other.Owner);
                }
            }
            used.Add(key, e.Owner); previous.Add((e.Owner, output, confirmed));
            if (pairMarker)
                previous.RemoveAt(previous.Count - 1);
        }
        return null;
    }

    public IDisposable Acquire(string owner, IReadOnlyList<string> outputs, IReadOnlyDictionary<string, string>? identities = null)
    {
        lock (_gate)
        {
            if (_owners.ContainsKey(owner)) throw new MixerLeaseException(new(MixerLeaseError.Occupied, outputs.FirstOrDefault() ?? "", owner));
            var desired = new Dictionary<string, IReadOnlyList<string>> { [owner] = outputs };
            var conflict = ValidateLocked(desired, identities ?? new Dictionary<string, string>(), null);
            if (conflict != null) throw new MixerLeaseException(conflict);
            var entry = new Entry(owner, outputs.Select(MixerDeviceIdentities.Canonical).ToArray(),
                Normalize(identities ?? new Dictionary<string, string>()));
            _owners.Add(owner, entry);
            return new Lease(() => { lock (_gate) _owners.Remove(owner); });
        }
    }

    public void Refresh(string owner, IReadOnlyList<string> outputs, IReadOnlyDictionary<string, string> identities)
    {
        lock (_gate)
        {
            if (!_owners.ContainsKey(owner)) throw new InvalidOperationException("No live lease");
            var conflict = ValidateLocked(new Dictionary<string, IReadOnlyList<string>> { [owner] = outputs }, identities, new HashSet<string> { owner });
            if (conflict != null) throw new MixerLeaseException(conflict);
            _owners[owner] = new(owner, outputs.Select(MixerDeviceIdentities.Canonical).ToArray(), Normalize(identities));
        }
    }

    public Dictionary<string, IDisposable> Reserve(IReadOnlyDictionary<string, IReadOnlyList<string>> desired,
        IReadOnlySet<string> held, IReadOnlyDictionary<string, string> identities)
    {
        lock (_gate)
        {
            var conflict = ValidateLocked(desired, identities, held);
            if (conflict != null) throw new MixerLeaseException(conflict);
            var result = new Dictionary<string, IDisposable>();
            foreach (var (owner, outputs) in desired)
            {
                if (held.Contains(owner)) continue;
                _owners.Add(owner, new(owner, outputs.Select(MixerDeviceIdentities.Canonical).ToArray(), Normalize(identities)));
                result.Add(owner, new Lease(() => { lock (_gate) _owners.Remove(owner); }));
            }
            return result;
        }
    }

    private static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string> identities) => identities
        .GroupBy(p => MixerDeviceIdentities.Canonical(p.Key)).ToDictionary(g => g.Key, g => g.Last().Value.Trim().ToUpperInvariant());

    public int Count { get { lock (_gate) return _owners.Count; } }
    private sealed class Lease(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }
}

public sealed class MixerLeaseException(MixerLeaseConflict conflict) : InvalidOperationException($"{conflict.Error}: {conflict.Device} / {conflict.Owner}")
{ public MixerLeaseConflict Conflict { get; } = conflict; }
