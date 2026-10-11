namespace HomePodCast.UI;

/// <summary>A stable test path, including the FIFO target in use when the measurement begins.</summary>
internal readonly record struct AvCalibrationSnapshot(AvCalibrationContext Context, int EffectiveLatencyMs,
    int CaptureExtraMs, double FifoTargetMs);

internal sealed partial class TrayApp
{
    /// <summary>The exact open endpoint; an unavailable capture never reuses a default-device calibration.</summary>
    public AvCalibrationContext? CurrentCalibrationContext => Controller.Capture is { } capture
        && !capture.NoOutputDevice && !capture.CaptureDeviceMissing
        && (Audio.CaptureEndpoint.FollowsDefault(Routing.CaptureDeviceId)
            || Audio.CaptureEndpoint.Same(Routing.CaptureDeviceId, capture.EndpointId))
        ? AvCalibrationContext.For(Config, capture.EndpointId, capture.ExtraLatencyMs > 0) : null;

    public bool HasCurrentCalibration => AvCalibrations.Get(Config, CurrentCalibrationContext) != null;

    public bool TryGetCalibrationSnapshot(out AvCalibrationSnapshot snapshot)
    {
        if (Controller.State == StreamState.Streaming && CurrentCalibrationContext is { } context)
        {
            snapshot = new(context, Controller.EffectiveLatencyMs, Controller.Capture?.ExtraLatencyMs ?? 0,
                Controller.Fifo.TargetMs);
            return true;
        }
        snapshot = default;
        return false;
    }

    public bool IsCalibrationSnapshotCurrent(AvCalibrationSnapshot snapshot) =>
        TryGetCalibrationSnapshot(out var current) && current.Context.Key == snapshot.Context.Key
        && current.EffectiveLatencyMs == snapshot.EffectiveLatencyMs && current.CaptureExtraMs == snapshot.CaptureExtraMs
        && Math.Abs(current.FifoTargetMs - snapshot.FifoTargetMs) < 0.001;

    /// <summary>All consumers (home, API, browser and local players) use this saved residual immediately.</summary>
    public bool ApplySyncCalibration(AvCalibrationSnapshot snapshot, int measuredOffsetMs)
    {
        if (!IsCalibrationSnapshotCurrent(snapshot)) return false;
        int estimate = LatencyTuner.EstimatedSoundLagMs(snapshot.EffectiveLatencyMs, Config, snapshot.CaptureExtraMs,
            snapshot.FifoTargetMs);
        if (!AvCalibrations.Apply(Config, snapshot.Context, measuredOffsetMs, estimate)) return false;
        Config.MeasuredAvOffsetMs = measuredOffsetMs;
        Config.Save();
        Log.Info($"sync calibration applied: residual {measuredOffsetMs - estimate:+0;-0;0} ms for the selected capture path");
        SyncCalibrationChanged();
        return true;
    }

    public void ResetSyncCalibration() => ResetSyncCalibration(CurrentCalibrationContext);

    public void ResetSyncCalibration(AvCalibrationContext? context)
    {
        if (!AvCalibrations.Reset(Config, context)) return;
        Config.Save();
        Log.Info("sync calibration cleared for the selected capture path");
        SyncCalibrationChanged();
    }

    private void SyncCalibrationChanged()
    {
        _form.UpdateState();
        KickPlayers();
        RaiseStateChanged();
    }
}
