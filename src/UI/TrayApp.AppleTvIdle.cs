using HomePodCast.Net;

namespace HomePodCast.UI;

// AppleTvIdlePolicy: a stream to an Apple TV is ended once nothing audible was sent for a while (its home-theater HomePods
// ignore "Hey Siri" for as long as the session is up), and connects again when the captured output plays something. Any
// connect or disconnect in between is the user's, and ends that.
internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _appleTvIdleTimer = new() { Interval = (int)AppleTvIdlePolicy.CheckInterval.TotalMilliseconds };
    private readonly Audio.OutputMeter _outputMeter = new();

    /// <summary>The idle release ended the stream, and connects it at the next sound.</summary>
    private bool _appleTvReleased;

    /// <summary>When the last connect was asked for (<see cref="Environment.TickCount64"/>).</summary>
    private long _connectAskedMs = Environment.TickCount64;

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

    /// <summary>Every connect and disconnect that is not the idle release's own: it no longer reconnects, and a new session gets its full time.</summary>
    private void ResetAppleTvIdle()
    {
        _appleTvReleased = false;
        _connectAskedMs = Environment.TickCount64;
    }

    private bool SelectedIsAppleTv() =>
        Config.DeviceId is { } id && GroupPlan.FromConfig(Config) == null &&
        (Controller.ReceiverLookup?.Invoke(id) ?? LastScan.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id)))?.Kind == ReceiverKind.AppleTv;

    private void CheckAppleTvIdle()
    {
        if (_quitting || _cinemaSession?.Holding == true) return;
        long now = Environment.TickCount64;
        bool streaming = _wantConnected && Controller.State == StreamState.Streaming;
        // The level is only read while released: one COM call four times a second, and none while a stream is up.
        bool soundNow = _appleTvReleased && _outputMeter.Peak(Routing.CaptureDeviceId) is > Audio.SoundActivity.Threshold;
        var sinceSound = _soundActivity.SilentFor(now);
        switch (AppleTvIdlePolicy.Decide(Config.AppleTvIdleReleaseSeconds, SelectedIsAppleTv(), streaming, _appleTvReleased,
                    TimeSpan.FromMilliseconds(now - _connectAskedMs), sinceSound, soundNow))
        {
            case AppleTvIdleAction.Release:
                Log.Info($"Apple TV idle: no sound for {(sinceSound == TimeSpan.MaxValue ? "this whole session" : $"{sinceSound.TotalSeconds:F0} s")}, " +
                         "ending the session so the HomePods hear Siri; connecting again at the next sound");
                _wantConnected = false;
                _appleTvReleased = true;
                string status = AppleTvReleasedText();
                PostConnection(() => StopAllOutputs(status));
                break;
            case AppleTvIdleAction.Reconnect:
                Log.Info(soundNow ? "Apple TV idle: the PC plays something, connecting again" : "Apple TV idle: turned off, connecting again");
                _appleTvReleased = false; // once: without a speaker selected, Connect only shows the window
                Connect(ConnectTrigger.Automatic); // a busy Apple TV is waited for, never taken over
                break;
        }
    }

    /// <summary>The status line (首页, the flyout, the tray icon's tooltip) while the Apple TV is released.</summary>
    internal static string AppleTvReleasedText() => L.T("没有声音，已暂时断开 Apple TV，有声音时自动连接");
}
