using HomePodCast.Audio;

namespace HomePodCast.UI;

// Which output device is captured ("routing by sound card", Audio.CaptureEndpoint), device notifications, and
// keeping the mic monitor off the captured device.
internal sealed partial class TrayApp
{
    private int _devicesPending;

    /// <summary>Called once from the constructor, after the main window exists.</summary>
    private void InitCapture()
    {
        AudioDeviceWatcher.Changed += OnAudioDevicesChanged;
        UpdateMonitorGuard();
    }

    private void DisposeCaptureWatch()
    {
        AudioDeviceWatcher.Changed -= OnAudioDevicesChanged;
        AudioDeviceWatcher.Stop();
    }

    /// <summary>
    /// Capture this output device (null = follow the Windows default output). Takes effect while streaming: the
    /// capture switches devices, the speaker stays connected.
    /// </summary>
    public void SetCaptureDevice(string? id, string? name)
    {
        Routing.SetCaptureDevice(id, name); // the capture and the volume-key watcher pick it up from Routing
        UpdateMonitorGuard();
        _form.UpdateState();
        _form.ShowVolumeKeys();
    }

    /// <summary>Windows notification thread: coalesce a burst (plugging a device sends several) into one UI update.</summary>
    private void OnAudioDevicesChanged()
    {
        if (Interlocked.Exchange(ref _devicesPending, 1) != 0) return;
        _ui.Post(_ =>
        {
            Volatile.Write(ref _devicesPending, 0);
            if (_quitting) return;
            UpdateMonitorGuard();
            _form.AudioDevicesChanged();
        }, null);
    }

    /// <summary>
    /// 本机监听 must not play on the device being captured (its sound would go to the HomePod too). Re-evaluated when
    /// streaming starts or stops, the capture device setting changes, or the default output changes.
    /// </summary>
    private void UpdateMonitorGuard()
    {
        if (_fx == null) return;
        string? defaultId = AudioEndpoints.DefaultOutputId();
        // From "connecting" on: the capture starts right after, and nothing should leak in its first moments.
        bool capturing = Controller.State != StreamState.Idle || Controller.Capture != null;
        _fx.SetCaptured(capturing ? CaptureEndpoint.Captured(Routing.CaptureDeviceId, defaultId) : null, defaultId);
    }
}
