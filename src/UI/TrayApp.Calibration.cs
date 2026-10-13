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
    public AvCalibrationContext? CurrentCalibrationContext => CalibrationContextFor(Config, Controller.Capture, Routing.CaptureDeviceId);

    /// <summary>
    /// The calibration key of what is captured now: the selected speaker or group, the endpoint actually open, whether it
    /// follows the default output, and per-app routing (a capture path that adds latency). Null while nothing usable is
    /// captured: no output device, the chosen device missing, or an open endpoint that is not the chosen one (the
    /// capture has not switched yet).
    /// </summary>
    /// <param name="captureDeviceId">The chosen capture device as the routing reads it (null: the default output).</param>
    internal static AvCalibrationContext? CalibrationContextFor(AppConfig cfg, Audio.ICaptureSource? capture, string? captureDeviceId) =>
        capture is { } c && !c.NoOutputDevice && !c.CaptureDeviceMissing
        && (Audio.CaptureEndpoint.FollowsDefault(captureDeviceId) || Audio.CaptureEndpoint.Same(captureDeviceId, c.EndpointId))
            ? AvCalibrationContext.For(cfg, c.EndpointId, c.ExtraLatencyMs > 0) : null;

    public bool HasCurrentCalibration => AvCalibrations.Get(Config, CurrentCalibrationContext) != null;

    public bool TryGetCalibrationSnapshot(out AvCalibrationSnapshot snapshot)
    {
        if (SnapshotFor(Controller.State, CurrentCalibrationContext, Controller.EffectiveLatencyMs,
                Controller.Capture?.ExtraLatencyMs ?? 0, Controller.Fifo.TargetMs) is { } now)
        {
            snapshot = now;
            return true;
        }
        snapshot = default;
        return false;
    }

    /// <summary>The path a sync test measures on: only while streaming with a usable capture (null otherwise).</summary>
    internal static AvCalibrationSnapshot? SnapshotFor(StreamState state, AvCalibrationContext? context, int effectiveLatencyMs,
        int captureExtraMs, double fifoTargetMs) =>
        state == StreamState.Streaming && context is { } c ? new(c, effectiveLatencyMs, captureExtraMs, fifoTargetMs) : null;

    public bool IsCalibrationSnapshotCurrent(AvCalibrationSnapshot snapshot) =>
        snapshot.SamePathAs(TryGetCalibrationSnapshot(out var current) ? current : null);

    /// <summary>All consumers (home, API, browser and local players) use this saved residual immediately.</summary>
    public SyncCalibrationOutcome ApplySyncCalibration(AvCalibrationSnapshot snapshot, int measuredOffsetMs)
    {
        var outcome = StoreSyncCalibration(Config, snapshot, TryGetCalibrationSnapshot(out var current) ? current : null,
            measuredOffsetMs, out int estimate);
        if (outcome == SyncCalibrationOutcome.PathChanged) return outcome;
        if (outcome == SyncCalibrationOutcome.OutOfRange)
        {
            Log.Warn($"sync calibration not applied: {measuredOffsetMs} ms measured against a {estimate} ms estimate");
            return outcome;
        }
        Config.MeasuredAvOffsetMs = measuredOffsetMs;
        Config.Save();
        Log.Info($"sync calibration applied: residual {measuredOffsetMs - estimate:+0;-0;0} ms for the selected capture path");
        SyncCalibrationChanged();
        return SyncCalibrationOutcome.Applied;
    }

    /// <summary>
    /// 应用本次校准 for a test that began on <paramref name="taken"/>, with the path as it is now (<paramref name="now"/>; null:
    /// not streaming). Nothing is saved when the two differ (the connection, capture path, latency or FIFO target changed
    /// during the test) or when the value is no correction; otherwise the residual against that path's estimate is stored.
    /// </summary>
    /// <param name="estimate">The estimate the measurement was compared with (0 when the path changed).</param>
    internal static SyncCalibrationOutcome StoreSyncCalibration(AppConfig cfg, AvCalibrationSnapshot taken, AvCalibrationSnapshot? now,
        int measuredOffsetMs, out int estimate)
    {
        estimate = 0;
        if (!taken.SamePathAs(now)) return SyncCalibrationOutcome.PathChanged;
        estimate = LatencyTuner.EstimatedSoundLagMs(taken.EffectiveLatencyMs, cfg, taken.CaptureExtraMs, taken.FifoTargetMs);
        return AvCalibrations.Apply(cfg, taken.Context, measuredOffsetMs, estimate)
            ? SyncCalibrationOutcome.Applied
            : SyncCalibrationOutcome.OutOfRange;
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
