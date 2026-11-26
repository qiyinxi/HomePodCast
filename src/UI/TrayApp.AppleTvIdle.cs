using HomePodCast.Net;

namespace HomePodCast.UI;

// AppleTvIdlePolicy (设置's 「Apple TV 无声时断开」, off unless turned on): a stream to an Apple TV is ended once nothing
// audible was sent for a while (its home-theater HomePods ignore "Hey Siri" for as long as the session is up), and
// connects again when the captured output plays something. Any connect or disconnect in between is the user's, and ends that.
internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _appleTvIdleTimer = new() { Interval = (int)AppleTvIdlePolicy.CheckInterval.TotalMilliseconds };
    private readonly Audio.OutputMeter _outputMeter = new();
    private readonly AppleTvIdleWatch _appleTvIdle = new();

    /// <summary>The idle release ended the stream, and connects it at the next sound.</summary>
    private bool _appleTvReleased => _appleTvIdle.Released;

    /// <summary>Called once from the constructor, after InitNightDisconnect (which adds the sound measurement).</summary>
    private void InitAppleTvIdle()
    {
        _appleTvIdleTimer.Tick += (_, _) => CheckAppleTvIdle();
        _appleTvIdleTimer.Start();
    }

    private void DisposeAppleTvIdle()
    {
        _appleTvIdleTimer.Dispose();
        _outputMeter.Dispose();
    }

    /// <summary>Every connect and disconnect that is not the idle release's own: it no longer reconnects.</summary>
    private void ResetAppleTvIdle() => _appleTvIdle.Reset();

    /// <summary>设置 → 「Apple TV 无声时断开」: saved, and the next check uses it (turned off while released, it connects again).</summary>
    public void SetAppleTvIdleRelease(bool on)
    {
        if (Config.AppleTvIdleRelease == on) return;
        Config.AppleTvIdleRelease = on;
        Config.Save();
        Log.Info($"Apple TV idle: turned {(on ? $"on ({AppleTvIdlePolicy.SecondsFor(Config)} s without sound)" : "off")}");
    }

    private bool SelectedIsAppleTv() =>
        Config.DeviceId is { } id && GroupPlan.FromConfig(Config) == null &&
        (Controller.ReceiverLookup?.Invoke(id) ?? LastScan.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id)))?.Kind == ReceiverKind.AppleTv;

    private void CheckAppleTvIdle()
    {
        if (_quitting || _cinemaSession?.Holding == true) return;
        long now = Environment.TickCount64;
        bool streaming = _wantConnected && Controller.State == StreamState.Streaming;
        var streamingFor = _appleTvIdle.Observe(now, streaming, Controller.State == StreamState.Idle);
        int seconds = AppleTvIdlePolicy.SecondsFor(Config);
        if (seconds <= 0 && !_appleTvReleased) return; // off, the default: nothing is measured
        // The level is only read while released: one COM call four times a second, and none while a stream is up.
        bool soundNow = _appleTvReleased && _outputMeter.Peak(Routing.CaptureDeviceId) is > Audio.SoundActivity.Threshold;
        var sinceSound = _soundActivity.SilentFor(now);
        switch (AppleTvIdlePolicy.Decide(seconds, SelectedIsAppleTv(), streaming, _appleTvReleased, streamingFor, sinceSound, soundNow))
        {
            case AppleTvIdleAction.Release:
                Log.Info($"Apple TV idle: no sound for {(sinceSound == TimeSpan.MaxValue ? "this whole session" : $"{sinceSound.TotalSeconds:F0} s")}, " +
                         $"stream up for {streamingFor.TotalSeconds:F0} s: ending the session so the HomePods hear Siri; connecting again at the next sound");
                _wantConnected = false;
                _appleTvIdle.Release();
                string status = AppleTvReleasedText();
                PostConnection(() => StopAllOutputs(status));
                break;
            case AppleTvIdleAction.Reconnect:
                Log.Info(soundNow ? "Apple TV idle: the PC plays something, connecting again" : "Apple TV idle: turned off, connecting again");
                _appleTvIdle.Reset(); // once: without a speaker selected, Connect only shows the window
                Connect(ConnectTrigger.Automatic); // a busy Apple TV is waited for, never taken over
                break;
        }
    }

    /// <summary>The status line (首页, the flyout, the tray icon's tooltip) while the Apple TV is released.</summary>
    internal static string AppleTvReleasedText() => L.T("没有声音，已暂时断开 Apple TV，有声音时自动连接");
}
