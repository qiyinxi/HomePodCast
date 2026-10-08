using System.Runtime.InteropServices;
using System.Text.Json;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// Capture device ("routing by sound card"): the setting, which endpoint is captured or why nothing is, when the
/// capture re-opens, the mic-monitor guard, and the device-notification callback's COM layout.
/// </summary>
public class CaptureDeviceTests
{
    private const string Cable = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
    private const string Headphones = "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
    private const string Hdmi = "{0.0.0.00000000}.{99999999-8888-7777-6666-555555555555}";

    // ---------------------------------------------------------------- config

    [Fact]
    public void Default_is_to_follow_the_windows_default_output()
    {
        var cfg = new AppConfig();
        Assert.Null(cfg.CaptureDeviceId);
        Assert.Null(cfg.CaptureDeviceName);
        Assert.Null(new AppRouting(cfg, () => { }).CaptureDeviceId);
    }

    [Fact]
    public void Old_configs_without_the_setting_follow_the_default()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>("""{ "DeviceId": "AA:BB", "LatencyMs": 120, "RouteDefault": "HomePod" }""")!;
        Assert.Null(cfg.CaptureDeviceId);
        Assert.True(CaptureEndpoint.FollowsDefault(cfg.CaptureDeviceId));
    }

    [Fact]
    public void Chosen_device_survives_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-capdev-{Guid.NewGuid():N}", "config.json");
        try
        {
            new AppConfig { CaptureDeviceId = Cable, CaptureDeviceName = "CABLE Input (VB-Audio Virtual Cable)" }.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(Cable, loaded.CaptureDeviceId);
            Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", loaded.CaptureDeviceName);
            Assert.Equal(Cable, new AppRouting(loaded, () => { }).CaptureDeviceId);

            loaded.CaptureDeviceId = null;
            loaded.CaptureDeviceName = null;
            loaded.Save(path);
            Assert.Null(AppConfig.Load(path).CaptureDeviceId);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Choosing_a_device_saves_it_and_tells_the_capture_once()
    {
        var cfg = new AppConfig();
        int saves = 0, changes = 0;
        var routing = new AppRouting(cfg, () => saves++);
        routing.Changed += () => changes++;

        routing.SetCaptureDevice(Cable, "CABLE Input");
        Assert.Equal((Cable, "CABLE Input"), (cfg.CaptureDeviceId, cfg.CaptureDeviceName));
        Assert.Equal(Cable, routing.CaptureDeviceId);
        Assert.Equal((1, 1), (saves, changes));

        routing.SetCaptureDevice(Cable.ToUpperInvariant(), "CABLE Input"); // the same endpoint
        Assert.Equal((1, 1), (saves, changes));

        routing.SetCaptureDevice(null, "ignored");
        Assert.Null(cfg.CaptureDeviceId);
        Assert.Null(cfg.CaptureDeviceName);
        Assert.Null(routing.CaptureDeviceId);
        Assert.Equal((2, 2), (saves, changes));

        routing.SetCaptureDevice("  ", null); // blank = follow the default: no change
        Assert.Equal((2, 2), (saves, changes));
    }

    // ---------------------------------------------------------------- which endpoint

    [Fact]
    public void Following_the_default_captures_the_default_or_reports_that_there_is_none()
    {
        Assert.Equal(new CaptureTarget(CaptureState.Capturing, Headphones), CaptureEndpoint.Resolve(null, Headphones, chosenActive: false));
        Assert.Equal(new CaptureTarget(CaptureState.Capturing, Headphones), CaptureEndpoint.Resolve("", Headphones, chosenActive: false));
        Assert.Equal(new CaptureTarget(CaptureState.NoOutputDevice, null), CaptureEndpoint.Resolve(null, null, chosenActive: false));
    }

    [Fact]
    public void A_chosen_device_is_captured_while_it_is_active()
    {
        Assert.Equal(new CaptureTarget(CaptureState.Capturing, Cable), CaptureEndpoint.Resolve(Cable, Headphones, chosenActive: true));
        Assert.Equal(new CaptureTarget(CaptureState.Capturing, Cable), CaptureEndpoint.Resolve(Cable, null, chosenActive: true));
    }

    [Fact]
    public void A_missing_chosen_device_never_falls_back_to_the_default_output()
    {
        // The default would be the headset with voice chat on it: capture nothing and say so instead.
        var target = CaptureEndpoint.Resolve(Cable, Headphones, chosenActive: false);
        Assert.Equal(CaptureState.ChosenDeviceMissing, target.State);
        Assert.Null(target.DeviceId);
        Assert.Equal(CaptureState.ChosenDeviceMissing, CaptureEndpoint.Resolve(Cable, null, chosenActive: false).State);
    }

    // ---------------------------------------------------------------- when to re-open

    [Fact]
    public void Following_the_default_switches_with_it()
    {
        Assert.True(CaptureEndpoint.ShouldReopen(null, Headphones, Hdmi, openedActive: true));
        Assert.False(CaptureEndpoint.ShouldReopen(null, Headphones, Headphones, openedActive: true));
        Assert.False(CaptureEndpoint.ShouldReopen(null, Headphones, Headphones.ToUpperInvariant(), openedActive: true));
        // No default right now: the opened stream's invalidation ends it, as before.
        Assert.False(CaptureEndpoint.ShouldReopen(null, Headphones, null, openedActive: false));
    }

    [Fact]
    public void A_chosen_device_ignores_default_output_changes()
    {
        Assert.False(CaptureEndpoint.ShouldReopen(Cable, Cable, Hdmi, openedActive: true));
        Assert.False(CaptureEndpoint.ShouldReopen(Cable, Cable, null, openedActive: true));
    }

    [Fact]
    public void A_chosen_device_that_goes_away_or_another_choice_reopens()
    {
        Assert.True(CaptureEndpoint.ShouldReopen(Cable, Cable, Headphones, openedActive: false)); // unplugged
        Assert.True(CaptureEndpoint.ShouldReopen(Hdmi, Cable, Headphones, openedActive: true));   // another device chosen
        Assert.True(CaptureEndpoint.ShouldReopen(Cable, Headphones, Headphones, openedActive: true)); // was following the default
        Assert.True(CaptureEndpoint.ShouldReopen(null, Cable, Headphones, openedActive: true));   // back to the default
        // Back to the default while the chosen device is the default anyway: keep it, no glitch.
        Assert.False(CaptureEndpoint.ShouldReopen(null, Cable, Cable, openedActive: true));
        // Choosing the device that is already captured (it is the default): keep it.
        Assert.False(CaptureEndpoint.ShouldReopen(Headphones, Headphones, Headphones, openedActive: true));
    }

    [Fact]
    public void Same_choice_compares_settings()
    {
        Assert.True(CaptureEndpoint.SameChoice(null, ""));
        Assert.True(CaptureEndpoint.SameChoice(Cable, Cable.ToUpperInvariant()));
        Assert.False(CaptureEndpoint.SameChoice(null, Cable));
        Assert.False(CaptureEndpoint.SameChoice(Cable, Hdmi));
        Assert.False(CaptureEndpoint.Same(null, null));
    }

    // ---------------------------------------------------------------- mic monitor

    [Fact]
    public void Captured_endpoint_is_the_chosen_device_even_while_missing_else_the_default()
    {
        Assert.Equal(Headphones, CaptureEndpoint.Captured(null, Headphones));
        Assert.Null(CaptureEndpoint.Captured(null, null));
        Assert.Equal(Cable, CaptureEndpoint.Captured(Cable, Headphones));
        Assert.Equal(Cable, CaptureEndpoint.Captured(Cable, null));
    }

    [Fact]
    public void Monitor_on_the_captured_output_collides()
    {
        // Following the default output, the monitor on the default output: it would be captured.
        Assert.True(CaptureEndpoint.MonitorCollides(null, CaptureEndpoint.Captured(null, Headphones), Headphones));
        Assert.True(CaptureEndpoint.MonitorCollides(Headphones, CaptureEndpoint.Captured(null, Headphones), Headphones));
        // The virtual card captured, the monitor chosen on it.
        Assert.True(CaptureEndpoint.MonitorCollides(Cable, CaptureEndpoint.Captured(Cable, Headphones), Headphones));
        // The virtual card captured, the monitor following a default that is the virtual card.
        Assert.True(CaptureEndpoint.MonitorCollides(null, CaptureEndpoint.Captured(Cable, Cable), Cable));
    }

    [Fact]
    public void Monitor_elsewhere_or_nothing_captured_is_fine()
    {
        // Routing by sound card: games on the virtual card, the monitor on the headphones (the default).
        Assert.False(CaptureEndpoint.MonitorCollides(null, CaptureEndpoint.Captured(Cable, Headphones), Headphones));
        Assert.False(CaptureEndpoint.MonitorCollides(Headphones, CaptureEndpoint.Captured(Cable, Headphones), Headphones));
        // Following the default output (HDMI), the monitor on the headphones.
        Assert.False(CaptureEndpoint.MonitorCollides(Headphones, CaptureEndpoint.Captured(null, Hdmi), Hdmi));
        // Not streaming: nothing is captured.
        Assert.False(CaptureEndpoint.MonitorCollides(null, null, Headphones));
        Assert.False(CaptureEndpoint.MonitorCollides(null, null, null));
    }

    // ---------------------------------------------------------------- volume keys

    [Fact]
    public void A_missing_capture_device_leaves_the_volume_keys_with_windows()
    {
        // Opened: the default output, or the chosen capture device.
        Assert.True(VolumeKeyForwarder.OutputPresent(opened: true, null, defaultOutputExists: true));
        Assert.True(VolumeKeyForwarder.OutputPresent(opened: true, Cable, defaultOutputExists: false));
        // The chosen device is missing: nothing is watched (never the default instead), but the keys are not taken over.
        Assert.True(VolumeKeyForwarder.OutputPresent(opened: false, Cable, defaultOutputExists: true));
        // No output device at all: as before, the keys go to the HomePod while streaming.
        Assert.False(VolumeKeyForwarder.OutputPresent(opened: false, null, defaultOutputExists: false));
        Assert.False(VolumeKeyForwarder.OutputPresent(opened: false, Cable, defaultOutputExists: false));
        Assert.Equal(VolumeKeyRoute.Follow, VolumeKeyRules.RouteFor(VolumeKeyMode.FollowWindows, streaming: true,
            VolumeKeyForwarder.OutputPresent(opened: false, Cable, defaultOutputExists: true)));
    }

    // ---------------------------------------------------------------- device notifications (COM)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StateChangedFn(IntPtr self, IntPtr deviceId, int state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DefaultChangedFn(IntPtr self, int flow, int role, IntPtr deviceId);

    [Fact]
    public void Notification_callback_matches_the_windows_sdk_layout()
    {
        // mmdeviceapi.h: MIDL_INTERFACE("7991EEC9-7E89-4D85-8390-6C703CEC60C0") IMMNotificationClient
        var iid = new Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
        Assert.Equal(iid, typeof(IMMNotificationClient).GUID);
        Assert.Equal(20, Marshal.SizeOf<PropertyKeyValue>());

        int fired = 0;
        var notifier = new AudioDeviceWatcher.Notifier(() => fired++);
        IntPtr unknown = Marshal.GetIUnknownForObject(notifier);
        IntPtr id = Marshal.StringToCoTaskMemUni(Cable);
        try
        {
            // What audiosrv does: QueryInterface for the IID, then call through the vtable
            // (IUnknown 0-2, OnDeviceStateChanged 3, OnDeviceAdded 4, OnDeviceRemoved 5, OnDefaultDeviceChanged 6).
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out IntPtr itf));
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(itf);
                var stateChanged = Marshal.GetDelegateForFunctionPointer<StateChangedFn>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                var defaultChanged = Marshal.GetDelegateForFunctionPointer<DefaultChangedFn>(Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));

                Assert.Equal(0, stateChanged(itf, id, 4)); // DEVICE_STATE_NOTPRESENT
                Assert.Equal(1, fired);
                Assert.Equal(0, defaultChanged(itf, 0, 0, id)); // eRender, eConsole
                Assert.Equal(2, fired);
                Assert.Equal(0, defaultChanged(itf, 0, 2, id)); // eRender, eCommunications: not what is captured
                Assert.Equal(0, defaultChanged(itf, 1, 0, id)); // eCapture (microphones)
                Assert.Equal(0, defaultChanged(itf, 0, 0, IntPtr.Zero)); // no default output any more
                Assert.Equal(3, fired);
            }
            finally
            {
                Marshal.Release(itf);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(id);
            Marshal.Release(unknown);
        }
    }
}
