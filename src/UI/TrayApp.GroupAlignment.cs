using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private bool _groupAlignmentTestOpen;
    internal bool CanTestOutputGroup => Controller.State == StreamState.Streaming
        && Controller.GroupOutputStatuses.Count(s => s.State == OutputState.Streaming) >= 2;
    internal bool CanCalibrateGroupByMicrophone => CanTestOutputGroup && Controller.CanCalibrateGroupAcoustically;

    internal async Task RunGroupAcousticCalibrationFromHomeAsync()
    {
        if (Config.SelectedOutputGroupId == null || !CanCalibrateGroupByMicrophone) return;
        ShowMain(AppPage.Group);
        await Task.Yield();
        await _form.RunGroupAcousticCalibrationFromHomeAsync();
    }

    internal async Task<IReadOnlyList<GroupAlignmentOutput>> RunGroupAcousticCalibration(
        IWin32Window owner, IReadOnlyList<GroupAlignmentOutput> outputs, CancellationToken userCancellation,
        string? micId = null, IProgress<string>? progress = null)
    {
        if (_groupAlignmentTestOpen) throw new OperationCanceledException("Another group calibration is already open");
        if (!CanCalibrateGroupByMicrophone || outputs.Count < 2)
            throw new OperationCanceledException("The active group cannot be microphone-calibrated");
        _groupAlignmentTestOpen = true;
        var identity = Controller.AudioSessionIdentity;
        double originalVolume = Controller.Volume ?? Config.Volume ?? 20;
        bool originalMuted = Controller.Muted;
        using var mixerRoute = Mixer?.BeginCalibrationRoute(SelectedMixerKey);
        using var cap = LimitVolumeForMeasurement();
        using var volumeWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(userCancellation, timeout.Token);
        try
        {
            // Silence the program feed before moving every output to full volume. Only the isolated calibration chirp passes.
            Controller.SetGroupAcousticCalibrationActive(true);
            Controller.SetVolume(100, unmute: true);
            await Controller.ConfirmGroupMeasurementVolumeAsync(volumeWait.Token);
            if (owner is Control { IsDisposed: true } || !ReferenceEquals(Controller.AudioSessionIdentity, identity))
                throw new OperationCanceledException("The output group changed");
            var readings = await Controller.MeasureGroupAcoustically(outputs.Select(o => (o.Id, o.Name)).ToArray(),
                () => ReferenceEquals(Controller.AudioSessionIdentity, identity)
                    && Controller.State == StreamState.Streaming && Controller.CanCalibrateGroupAcoustically,
                cancellation.Token, micId, progress);
            int[] offsets = Audio.AcousticAlignment.RelativeOffsets(readings);
            var result = new GroupAlignmentOutput[outputs.Count];
            for (int i = 0; i < outputs.Count; i++)
            {
                result[i] = outputs[i] with { TrimMs = Math.Clamp(outputs[i].TrimMs + offsets[i], -5000, 5000) };
            }
            Log.Info("live group microphone calibration: " + string.Join(", ", outputs.Select((o, i) =>
                $"{Log.Name(o.Name)} arrival {readings[i].DelayMs:F1} ms (match {readings[i].Confidence:F2}) → {result[i].TrimMs:+#;-#;0} ms")));
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"live group microphone calibration failed: {ex.Message}");
            throw;
        }
        finally
        {
            Controller.EndGroupAcousticCalibration();
            if (ReferenceEquals(Controller.AudioSessionIdentity, identity))
            {
                Controller.SetVolume(originalVolume, unmute: false);
                Controller.SetMuted(originalMuted);
                Controller.SetGroupAcousticCalibrationActive(false);
            }
            _groupAlignmentTestOpen = false;
        }
    }

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
