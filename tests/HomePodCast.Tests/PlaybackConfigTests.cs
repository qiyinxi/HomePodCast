namespace HomePodCast.Tests;

/// <summary>TrayApp.PlaybackConfig is read on other threads too (PlayerSync's poll, the local API).</summary>
public class PlaybackConfigTests
{
    [Fact]
    public void The_playback_config_never_writes_into_the_apps_own()
    {
        var config = TestApp.Config();
        config.CaptureDeviceId = "{old}";
        var app = TestApp.Create(config);
        // A usage profile's capture device, between UsageProfiles.Apply and Routing.Reload; PlayerSync's poll and the
        // local API read the playback config on their own threads meanwhile.
        config.CaptureDeviceId = "{new}";

        Assert.Same(config, app.PlaybackConfig);

        Assert.Equal("{new}", config.CaptureDeviceId);
        app.Routing.Reload();
        Assert.Equal("{new}", app.Routing.CaptureDeviceId);
    }

    [Fact]
    public void A_capture_device_is_read_as_the_routing_reads_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"hpc-playback-{Guid.NewGuid():N}"), path = Path.Combine(folder, "config.json");
        try
        {
            new AppConfig { CaptureDeviceId = "  " }.Save(path);
            Assert.Null(AppConfig.Load(path).CaptureDeviceId); // follows the default output, like Audio.AppRouting
            new AppConfig { CaptureDeviceId = " {cable} " }.Save(path);
            Assert.Equal("{cable}", AppConfig.Load(path).CaptureDeviceId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
