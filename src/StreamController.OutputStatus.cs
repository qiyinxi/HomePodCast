using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

internal sealed record GroupOutputStatus(string Id, string Name, OutputKind Kind, OutputState State, string? Error,
    bool Required, bool NeedsUserAction, bool Retrying, int? LatencyMs, ReceiverProtocol? Protocol = null,
    bool UsesPtp = false, double DriftPpm = 0, bool HasClockEstimate = false);

public sealed partial class StreamController
{
    private GroupPlan? _groupStatusPlan;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Exception> _groupOutputFailures = new();

    /// <summary>A snapshot for the group UI, including outputs whose transport is still preparing.</summary>
    internal IReadOnlyList<GroupOutputStatus> GroupOutputStatuses
    {
        get
        {
            var plan = Volatile.Read(ref _groupStatusPlan);
            if (plan == null) return [];
            var group = _groupRunner?.Current;
            var state = State == StreamState.Idle ? OutputState.Stopped : OutputState.Preparing;
            var rows = plan.Members.Select(m => new GroupOutputStatus(m.DeviceId, m.Name,
                GroupUsesRaop(GroupDevice(m.DeviceId)) ? OutputKind.Raop : OutputKind.AirPlay2, state, null,
                GroupUsesRaop(GroupDevice(m.DeviceId)) ? plan.IsRequiredOutput(m.DeviceId)
                    : plan.Kind == GroupKind.StereoPair || !plan.RequiredOutputs.Any(p => GroupPlan.Key(p.Key) == GroupPlan.Key(m.DeviceId) && !p.Value),
                false, false, null)).ToList();
            rows.AddRange(plan.LocalOutputs.Select(o => new GroupOutputStatus(o.DeviceId, o.Name, OutputKind.Local,
                state, null, o.Required, false, false, o.ExtraLatencyMs)));
            rows.AddRange(plan.DlnaOutputs.Select(o => new GroupOutputStatus("dlna:" + o.Udn, o.Name, OutputKind.Dlna,
                state, null, o.Required, false, false, o.EstimatedLatencyMs)));
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Kind is OutputKind.AirPlay2 or OutputKind.Raop
                    && _groupOutputFailures.TryGetValue(GroupPlan.Key(rows[i].Id), out var error))
                    rows[i] = rows[i] with { State = OutputState.Lost, Error = error.Message,
                        NeedsUserAction = ReceiverGroupFailures.RequiresUserAction(error), Retrying = State == StreamState.Retrying };
            void Put(GroupOutputStatus row)
            {
                string Key(GroupOutputStatus output) => output.Kind switch
                {
                    OutputKind.Local => "local:" + LocalGroupSettings.Key(output.Id),
                    OutputKind.Dlna => "dlna:" + DlnaGroupSettings.Key(output.Id),
                    _ => "airplay:" + GroupPlan.Key(output.Id),
                };
                int index = rows.FindIndex(r => Key(r) == Key(row));
                if (index < 0) rows.Add(row); else rows[index] = row;
            }
            foreach (var s in group?.ReceiverOutputs ?? Volatile.Read(ref _preparingReceiverOutputs)?.Status ?? [])
                Put(new(s.Id, s.Name, s.Protocol == ReceiverProtocol.Raop ? OutputKind.Raop : OutputKind.AirPlay2,
                    s.State, s.Error, s.Required, s.NeedsUserAction, s.Retrying, s.LatencyMs, s.Protocol, s.UsesPtp));
            if (group == null) return rows;
            for (int i = 0; i < group.Setups.Count; i++)
            {
                var setup = group.Setups[i]; var member = group.Members[i];
                Put(new(setup.Id ?? setup.Label, rows.FirstOrDefault(r => r.Id == setup.Id)?.Name ?? setup.Label,
                    OutputKind.AirPlay2, member?.LostReason != null ? OutputState.Lost : group.State, member?.LostReason,
                    true, false, false, member == null ? null : (int)Math.Round(member.LatencyFrames * 1000.0 / 44100),
                    ReceiverProtocol.AirPlay2Realtime));
            }
            foreach (var s in group.LocalOutputs)
            {
                var option = plan.LocalOutputs.FirstOrDefault(o => o.DeviceId == s.Id);
                Put(new(s.Id, s.Name, OutputKind.Local, s.State, s.Error, option?.Required ?? false, false,
                    s.State == OutputState.Lost, option?.ExtraLatencyMs));
            }
            foreach (var s in group.RaopOutputs)
                Put(new(s.Id, s.Name, OutputKind.Raop, s.State, s.Error, s.Required, s.NeedsUserAction, s.Retrying,
                    CompatibilityLatency?.Invoke(s.Id) ?? 2000, ReceiverProtocol.Raop));
            foreach (var s in group.DlnaOutputs)
                Put(new(s.Id, s.Name, OutputKind.Dlna, s.State, s.Error, s.Required, s.NeedsUserAction, s.Retrying,
                    s.EstimatedLatencyMs, DriftPpm: s.DriftPpm, HasClockEstimate: s.HasClockEstimate));
            return rows;
        }
    }
}
