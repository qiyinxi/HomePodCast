using HomePodCast.Audio;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// Whether applying a profile reconnects, and how (TrayApp.ConnectionSignature, TrayApp.ProfileConnectTrigger): only a
/// change of what the connection is made with reconnects; the same speaker while live is our own reconnect, another one
/// waits for a busy speaker like any automatic connect. Pure: no speaker, no TrayApp instance.
/// </summary>
public class ProfileConnectionTests
{
    private static AppConfig Live() => new() { DeviceId = "AA:BB:CC:DD:EE:01", DeviceName = "卧室", Host = "10.0.0.1", LatencyMs = 120 };

    [Fact]
    public void The_same_speaker_while_live_is_our_own_reconnect() =>
        Assert.Equal(ConnectTrigger.Reconnect, TrayApp.ProfileConnectTrigger(sameSelection: true, live: true));

    [Theory]
    [InlineData(false, true)]  // another speaker while ours plays: never taken over without a click
    [InlineData(false, false)]
    [InlineData(true, false)]  // nothing live (waiting for a busy speaker, or the retries ended)
    public void Otherwise_it_connects_like_picking_a_speaker_and_waits_for_a_busy_one(bool sameSelection, bool live) =>
        Assert.Equal(ConnectTrigger.Automatic, TrayApp.ProfileConnectTrigger(sameSelection, live));

    [Fact]
    public void Effects_routing_capture_device_night_mode_and_cap_apply_live_without_a_reconnect()
    {
        var cfg = Live();
        string before = TrayApp.ConnectionSignature(cfg);
        cfg.NightMode = true;
        cfg.VolumeCapPercent = 40;
        cfg.Volume = 30;
        cfg.CaptureDeviceId = "{0.0.0.00000000}.{headphones}";
        cfg.RouteDefault = AudioRoute.Both;
        cfg.AppRoutes["game"] = AudioRoute.Local;
        cfg.AppSendVolumes["game"] = 50;
        cfg.Effects.ReverbEnabled = true;
        cfg.Effects.OutputEq.Gains[0] = 6;
        cfg.VolumeKeys = VolumeKeyMode.Off;
        cfg.MoviePlayerSync = false;
        cfg.Scene = Scene.Recommended; // the same 120 ms
        cfg.DeviceName = "Bedroom";
        cfg.Host = "10.0.0.9";
        Assert.Equal(before, TrayApp.ConnectionSignature(cfg));
    }

    [Fact]
    public void The_speaker_latency_fifo_target_and_group_layout_change_the_connection()
    {
        string before = TrayApp.ConnectionSignature(Live());
        Assert.NotEqual(before, Changed(c => c.DeviceId = "AA:BB:CC:DD:EE:02"));
        Assert.NotEqual(before, Changed(c => c.LatencyMs = 200));
        Assert.NotEqual(before, Changed(c => c.FifoTargetMs = 24));
        Assert.NotEqual(before, Changed(c => c.MultiRoomDeviceId = "AA:BB:CC:DD:EE:03"));
        Assert.Equal(before, Changed(c => c.DeviceId = "aa-bb-cc-dd-ee-01")); // the same speaker however its id is written

        string grouped = Changed(c => c.MultiRoomDeviceId = "AA:BB:CC:DD:EE:03");
        Assert.NotEqual(grouped, Changed(c => { c.MultiRoomDeviceId = "AA:BB:CC:DD:EE:03"; c.GroupSplitChannels = true; }));
        Assert.NotEqual(grouped, Changed(c => { c.MultiRoomDeviceId = "AA:BB:CC:DD:EE:03"; c.GroupVolumeOffsets = new() { ["AABBCCDDEE03"] = -5 }; }));
    }

    [Fact]
    public void Applying_a_profile_that_differs_only_in_effects_keeps_the_connection()
    {
        var cfg = Live();
        var profile = UsageProfiles.Capture(cfg);
        profile.NightMode = true;
        profile.VolumeCapPercent = 50;
        profile.Effects.ReverbEnabled = true;
        profile.Host = "10.0.0.77"; // a stale address in the file changes nothing
        string before = TrayApp.ConnectionSignature(cfg);
        UsageProfiles.Apply(cfg, profile);
        Assert.Equal(before, TrayApp.ConnectionSignature(cfg));
        Assert.Equal("10.0.0.1", cfg.Host);
        Assert.True(cfg.NightMode);
    }

    [Fact]
    public void Applying_a_profile_for_another_latency_or_speaker_changes_the_connection()
    {
        var cfg = Live();
        string before = TrayApp.ConnectionSignature(cfg);
        UsageProfiles.Apply(cfg, new UsageSettings { DeviceId = "AA:BB:CC:DD:EE:01", Scene = Scene.Game });
        Assert.NotEqual(before, TrayApp.ConnectionSignature(cfg)); // 105 ms: negotiated at SETUP

        cfg = Live();
        UsageProfiles.Apply(cfg, new UsageSettings { DeviceId = "AA:BB:CC:DD:EE:02", LatencyMs = 120 });
        Assert.NotEqual(before, TrayApp.ConnectionSignature(cfg));
        Assert.NotEqual(OwnSession.KeyOf(Live()), OwnSession.KeyOf(cfg)); // so: another speaker, an automatic connect
    }

    private static string Changed(Action<AppConfig> change)
    {
        var cfg = Live();
        change(cfg);
        return TrayApp.ConnectionSignature(cfg);
    }
}
