namespace HomePodCast;

public sealed partial class StreamController
{
    /// <summary>Do not start a calibration tone while an older volume command can still raise a required speaker.</summary>
    internal async Task ConfirmGroupMeasurementVolumeAsync(CancellationToken ct)
    {
        if (MeasurementCapPercent is not { } cap || cap > 25) throw new InvalidOperationException("No measurement volume ceiling");
        var group = _groupRunner?.Current ?? throw new InvalidOperationException("No connected group");
        while (Volatile.Read(ref _volumeWorker) != 0 || Volatile.Read(ref _volumeDirty) != 0)
            await Task.Delay(5, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_groupRunner?.Current, group)) throw new OperationCanceledException("Group changed during volume publication");
        await group.ConfirmMeasurementVolumeAsync(ct).ConfigureAwait(false);
        if (!ReferenceEquals(_groupRunner?.Current, group) || MeasurementCapPercent is not { } current || current > 25)
            throw new OperationCanceledException("Group changed during volume publication");
    }
}
