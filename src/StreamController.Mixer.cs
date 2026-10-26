namespace HomePodCast;

public sealed partial class StreamController
{
    internal Action<GroupPlan, IReadOnlyList<string>>? CheckMixerGroupLease { get; set; }
    internal Func<string, bool>? MixerLocalDuplicate { get; set; }
    internal void WaitForMixerDiscovery() => Set(StreamState.Retrying, L.T("找不到音箱：请确认它已通电、和电脑在同一网络"));
    /// <summary>Retain physical-device leases until the old loop has actually left, even after Stop's UI timeout.</summary>
    internal async Task StopFullyAsync(string? reason = null)
    {
        Task? completed;
        lock (_lock) completed = _loop;
        if (reason == null) Stop(); else Stop(reason);
        if (completed != null) try { await completed.ConfigureAwait(false); } catch { }
    }
}
