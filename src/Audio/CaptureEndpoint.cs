namespace HomePodCast.Audio;

/// <summary>What the capture can do with the configured output device right now.</summary>
public enum CaptureState
{
    /// <summary>A device is open (or about to be) and its loopback goes to the speaker.</summary>
    Capturing,
    /// <summary>Following the default output, and Windows has none at all.</summary>
    NoOutputDevice,
    /// <summary>A device was chosen and it is unplugged, disabled or uninstalled. Nothing is captured.</summary>
    ChosenDeviceMissing,
}

/// <summary>The endpoint to loopback (null when there is none to capture) and why.</summary>
public readonly record struct CaptureTarget(CaptureState State, string? DeviceId);

/// <summary>
/// "Routing by sound card": which output device is captured (AppConfig.CaptureDeviceId; null = follow the Windows
/// default output, as before). Apps meant for the HomePod are set (Windows' per-app output device, or the app's own
/// setting) to a device nobody listens to (a virtual card, an HDMI monitor without speakers) and that device is chosen
/// here; everything else, e.g. voice chat on headphones, never reaches the speaker. It is plain endpoint loopback, so
/// it costs nothing over capturing the default output.
/// <para>
/// A chosen device that is gone is never replaced by the default output: that could push private audio (voice chat
/// on the headset) to a living-room speaker. The capture then delivers nothing and says so.
/// </para>
/// <para>
/// Per-app routing (process loopback) works on the captured device instead of the default output: its rules apply
/// to the apps playing on that device (those are the sessions planned, captured and, for "HomePod", silenced there),
/// so apps on other devices are never sent either way. "Everything else" means everything else on the captured
/// device. With no rules the whole captured device is taken in one piece, exactly like the default output was.
/// </para>
/// Pure decisions, unit-tested; RoutedCapture, SessionRouter and the UI do the device I/O.
/// </summary>
public static class CaptureEndpoint
{
    /// <summary>No device chosen: capture whatever Windows' default output is (and follow it when it changes).</summary>
    public static bool FollowsDefault(string? chosenId) => string.IsNullOrEmpty(chosenId);

    /// <summary>Endpoint ids from different APIs may differ in case; they are otherwise exact.</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same setting: both follow the default, or both name the same device.</summary>
    public static bool SameChoice(string? a, string? b) => FollowsDefault(a) ? FollowsDefault(b) : Same(a, b);

    /// <summary>
    /// Which endpoint to open. <paramref name="defaultId"/> is the current default output (null = none);
    /// <paramref name="chosenActive"/> whether the chosen device exists and is active. Never falls back to the default
    /// for a chosen device.
    /// </summary>
    public static CaptureTarget Resolve(string? chosenId, string? defaultId, bool chosenActive)
    {
        if (FollowsDefault(chosenId))
            return string.IsNullOrEmpty(defaultId) ? new(CaptureState.NoOutputDevice, null) : new(CaptureState.Capturing, defaultId);
        return chosenActive ? new(CaptureState.Capturing, chosenId) : new(CaptureState.ChosenDeviceMissing, null);
    }

    /// <summary>
    /// While <paramref name="openedId"/> is captured: whether to let it go and resolve again. Following the default:
    /// when the default output moved elsewhere (no default at all is left to the device invalidation). A chosen device:
    /// when another one (or the default) was chosen, or it stopped being active; default-device changes don't matter.
    /// </summary>
    public static bool ShouldReopen(string? chosenId, string openedId, string? defaultId, bool openedActive)
    {
        if (FollowsDefault(chosenId)) return !string.IsNullOrEmpty(defaultId) && !Same(defaultId, openedId);
        return !Same(chosenId, openedId) || !openedActive;
    }

    /// <summary>
    /// The endpoint whose sound goes to the speaker: the chosen device (also while it is missing: it is what will be
    /// captured once it is back), or the default output. Null when there is none.
    /// </summary>
    public static string? Captured(string? chosenId, string? defaultId) =>
        FollowsDefault(chosenId) ? (string.IsNullOrEmpty(defaultId) ? null : defaultId) : chosenId;

    /// <summary>
    /// The mic monitor would play on the endpoint being captured, so its sound would go to the speaker as well: twice
    /// (with 本机监听 + HomePod) or against the user's choice (本机监听 only). <paramref name="monitorDeviceId"/> null =
    /// the default output; <paramref name="capturedId"/> null = nothing is being captured.
    /// </summary>
    public static bool MonitorCollides(string? monitorDeviceId, string? capturedId, string? defaultId) =>
        Same(string.IsNullOrEmpty(monitorDeviceId) ? defaultId : monitorDeviceId, capturedId);
}
