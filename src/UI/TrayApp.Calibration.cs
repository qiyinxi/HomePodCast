namespace HomePodCast.UI;

/// <summary>A stable test path, including the FIFO target in use when the measurement begins.</summary>
internal readonly record struct AvCalibrationSnapshot(AvCalibrationContext Context, int EffectiveLatencyMs,
    int CaptureExtraMs, double FifoTargetMs)
{
    /// <summary>
    /// <paramref name="now"/> is still the path this snapshot was taken on: the same speaker (or group), capture endpoint
    /// and routing mode, effective latency, capture extra and FIFO target (it only grows after a dropout). Null: no path now.
    /// </summary>
    public bool SamePathAs(AvCalibrationSnapshot? now) =>
        now is { } current && Context.IsValid && current.Context.Key == Context.Key
        && current.EffectiveLatencyMs == EffectiveLatencyMs && current.CaptureExtraMs == CaptureExtraMs
        && Math.Abs(current.FifoTargetMs - FifoTargetMs) < 0.001;
}

/// <summary>What became of 应用本次校准.</summary>
internal enum SyncCalibrationOutcome
{
    Applied,

    /// <summary>No click was seen leaving the PC for the speaker (the capture path doesn't carry the test sound).</summary>
    NoTestSound,

    /// <summary>The connection, capture path, latency or FIFO target changed during the test.</summary>
    PathChanged,

    /// <summary>The value is too far from the estimate to be a correction (AvCalibration.IsValid).</summary>
    OutOfRange,
}

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
        snapshot.SamePathAs(TryGetCalibrationSnapshot(out var current) ? current : null);

    /// <summary>All consumers (home, API, browser and local players) use this saved residual immediately.</summary>
    public SyncCalibrationOutcome ApplySyncCalibration(AvCalibrationSnapshot snapshot, int measuredOffsetMs)
    {
        if (!IsCalibrationSnapshotCurrent(snapshot)) return SyncCalibrationOutcome.PathChanged;
        int estimate = LatencyTuner.EstimatedSoundLagMs(snapshot.EffectiveLatencyMs, Config, snapshot.CaptureExtraMs,
            snapshot.FifoTargetMs);
        if (!AvCalibrations.Apply(Config, snapshot.Context, measuredOffsetMs, estimate))
        {
            Log.Warn($"sync calibration not applied: {measuredOffsetMs} ms measured against a {estimate} ms estimate");
            return SyncCalibrationOutcome.OutOfRange;
        }
        Config.MeasuredAvOffsetMs = measuredOffsetMs;
        Config.Save();
        Log.Info($"sync calibration applied: residual {measuredOffsetMs - estimate:+0;-0;0} ms for the selected capture path");
        SyncCalibrationChanged();
        return SyncCalibrationOutcome.Applied;
    }

    /// <summary>What 应用本次校准 says when nothing was saved, naming what happened; null once applied.</summary>
    internal static string? SyncCalibrationMessage(SyncCalibrationOutcome outcome) => outcome switch
    {
        SyncCalibrationOutcome.NoTestSound => L.T("没有检测到送往音箱的测试音，请确认音源和分流设置后重新测试。"),
        SyncCalibrationOutcome.PathChanged => L.T("测试期间连接、音源或延迟发生了变化，这次的结果没有保存，请重新测试。"),
        SyncCalibrationOutcome.OutOfRange => L.T("这次的结果和估算相差太大，没有保存，请重新测试。"),
        _ => null,
    };

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
