using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private bool _groupAlignmentTestOpen;
    internal bool CanTestOutputGroup => Controller.State == StreamState.Streaming
        && Controller.GroupOutputStatuses.Count(s => s.State == OutputState.Streaming) >= 2;

    internal async Task RunGroupAlignmentTest(IWin32Window owner)
    {
        if (_groupAlignmentTestOpen) return;
        if (!CanTestOutputGroup || !TryGetCalibrationSnapshot(out var snapshot))
            throw new OperationCanceledException("The test group is no longer connected");
        _groupAlignmentTestOpen = true;
        using var mixerRoute = Mixer?.BeginCalibrationRoute(SelectedMixerKey);
        using var cap = LimitVolumeForMeasurement();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var identity = Controller.AudioSessionIdentity;
            await Controller.ConfirmGroupMeasurementVolumeAsync(wait.Token);
            if (owner is Control { IsDisposed: true }) return;
            if (!IsCalibrationSnapshotCurrent(snapshot) || !ReferenceEquals(Controller.AudioSessionIdentity, identity))
                throw new OperationCanceledException("The test group changed");
            using var route = Routing.BeginSyncTestRoute();
            using var test = new SyncTestForm(Controller.EffectiveLatencyMs, snapshot.Context.CaptureEndpointId,
                isPathCurrent: () => IsCalibrationSnapshotCurrent(snapshot) && ReferenceEquals(Controller.AudioSessionIdentity, identity),
                measurementOnly: true);
            test.ShowDialog(owner); // relative trim is entered separately; never save a global A/V correction
            await test.StopPlaybackAsync(); // freeze the final click timestamp before calculating the drain deadline
            var plan = GroupPlan.FromConfig(Config);
            int tail = Math.Max(plan?.Members.Select(m => Math.Max(0, plan.DelayTrimFor(m.DeviceId))).DefaultIfEmpty().Max() ?? 0,
                Math.Max(plan?.LocalOutputs.Select(o => Math.Max(0, o.TrimMs)).DefaultIfEmpty().Max() ?? 0,
                    plan?.DlnaOutputs.Select(o => Math.Max(0, o.TrimMs)).DefaultIfEmpty().Max() ?? 0));
            long drained = test.LastClickTicks + MediaClock.FromMs(snapshot.EffectiveLatencyMs + snapshot.CaptureExtraMs + snapshot.FifoTargetMs + tail + 500);
            // Restore the user's volume only after queued test clicks have left this same group.
            while (test.LastClickTicks != 0 && ReferenceEquals(Controller.AudioSessionIdentity, identity) && MediaClock.Now < drained)
                await Task.Delay(50);
        }
        catch (Exception ex) { Log.Warn($"group alignment test not started: {ex.Message}"); throw; }
        finally { _groupAlignmentTestOpen = false; }
    }
}
