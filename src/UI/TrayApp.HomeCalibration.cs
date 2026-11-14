using HomePodCast.Audio;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private int _homeCalibrationRunning;

    internal bool CanCalibrateHomeOutput => Controller.State == StreamState.Streaming
        && Mixer == null && !Routing.Active && Controller.GroupSource == null && Config.DeviceId != null;

    internal async Task MeasureHomeOutputSync(IWin32Window owner)
    {
        if (Interlocked.Exchange(ref _homeCalibrationRunning, 1) != 0) return;
        if (!CanCalibrateHomeOutput)
        {
            Interlocked.Exchange(ref _homeCalibrationRunning, 0);
            MessageBox.Show(owner, L.T("请先连接一台 HomePod，并使用首页的 Live 电脑音频播放。"), L.T("测量同步"));
            return;
        }

        var identity = Controller.AudioSessionIdentity;
        bool originalPcMute = Config.MutePcWhileStreaming;
        bool originalSpeakerMute = Controller.Muted;
        int originalLatency = Controller.EffectiveLatencyMs;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var cap = LimitVolumeForMeasurement();
        try
        {
            // First hear the calibration chirp only on the PC. The second pass mutes the PC's render endpoint;
            // WASAPI loopback remains upstream of the endpoint volume and carries that chirp to the HomePod.
            SetSpeakerMuted(true);
            SetMutePcWhileStreaming(false);
            await Task.Delay(500, cancel.Token);
            var local = await HomeAcousticCalibrator.MeasureOutputAsync(null, cancel.Token);
            EnsureHomeCalibrationCurrent(identity);

            SetSpeakerMuted(false);
            SetMutePcWhileStreaming(true);
            await Task.Delay(500, cancel.Token);
            var remote = await HomeAcousticCalibrator.MeasureOutputAsync(null, cancel.Token);
            EnsureHomeCalibrationCurrent(identity);

            double difference = remote.DelayMs - local.DelayMs;
            int floor = Controller.SafeLatency(0);
            int target = Math.Clamp((int)Math.Round(originalLatency - difference), floor, LatencyTuner.MaxMs);
            Log.Info($"home mic sync calibration: PC arrival {local.DelayMs:F1} ms, HomePod arrival {remote.DelayMs:F1} ms; " +
                $"requested latency {originalLatency} → {target} ms (safe floor {floor} ms)");
            if (target == originalLatency)
                MessageBox.Show(owner, L.F("测得本机与 HomePod 相差约 {0} 毫秒；当前延迟已到安全范围，无法再向前调整。", Math.Abs((int)Math.Round(difference))), L.T("测量同步"));
            else
                MessageBox.Show(owner, L.F("测量完成：本机与 HomePod 相差约 {0} 毫秒，已调整 HomePod 延迟。", Math.Abs((int)Math.Round(difference))), L.T("测量同步"));

            if (owner is not Control { IsDisposed: true } && Controller.AudioSessionIdentity == identity && target != originalLatency)
                _form.ApplyCalibrationLatency(target);
        }
        catch (Exception ex)
        {
            Log.Warn($"home microphone sync calibration failed: {ex.Message}");
            if (owner is not Control { IsDisposed: true })
                MessageBox.Show(owner, ex is CinemaException ? ex.Message : L.T("测量没有完成，请确认麦克风可用并重试。"), L.T("测量同步"));
        }
        finally
        {
            if (!Controller.Muted.Equals(originalSpeakerMute)) SetSpeakerMuted(originalSpeakerMute);
            if (Config.MutePcWhileStreaming != originalPcMute) SetMutePcWhileStreaming(originalPcMute);
            Interlocked.Exchange(ref _homeCalibrationRunning, 0);
        }
    }

    private void EnsureHomeCalibrationCurrent(object? identity)
    {
        if (!CanCalibrateHomeOutput || !ReferenceEquals(Controller.AudioSessionIdentity, identity))
            throw new OperationCanceledException(L.T("测量期间连接或音源发生变化，请重新测量。"));
    }
}
