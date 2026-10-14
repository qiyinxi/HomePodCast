using System.Net;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class ReceiverLatencySelectionTests
{
    private static AirPlayDevice Device(string model, bool modern = false) => new("Test", model, IPAddress.Loopback,
        7001, model, new Dictionary<string, string> { ["srcvers"] = modern ? "366.0" : "220.68" });

    [Fact]
    public void HomePodKeepsScenesEvenWithCompatibilityOverrides()
    {
        var device = Device("AudioAccessory6,1", true);
        var preferences = ReceiverPreferences.Of(device); preferences.ProtocolOverride = ReceiverProtocol.Raop;
        Assert.Null(ReceiverLatencySelection.Create(device, preferences, ReceiverProtocol.AirPlay2Buffered));
    }

    [Fact]
    public void CompatibilitySelectionIsPerDeviceAndDoesNotChangeScenes()
    {
        var device = Device("AppleTV3,2"); var first = ReceiverPreferences.Of(device); var second = ReceiverPreferences.Of(device);
        var scene = new AppConfig(); int homePodLatency = scene.LatencyMs;
        var selection = ReceiverLatencySelection.Create(device, first)!;
        Assert.True(selection.Compatibility); Assert.Equal(2000, selection.Value);
        selection.Save(first, 1500);
        Assert.Equal(1500, first.CompatibilityLatencyMs); Assert.Equal(2000, second.CompatibilityLatencyMs);
        Assert.Equal(homePodLatency, scene.LatencyMs);
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.Save(first, 500));
    }

    [Fact]
    public void ProtocolInUseControlsFallbackLatencyAndAirportRecommendation()
    {
        var modern = Device("ThirdParty", true);
        Assert.Equal(250, ReceiverLatencySelection.Create(modern, null)!.Value);
        Assert.True(ReceiverLatencySelection.Create(modern, null, ReceiverProtocol.Raop)!.Compatibility);
        var airport = Device("AirPort10,115"); var preferences = ReceiverPreferences.Of(airport); preferences.CompatibilityLatencyMs = 1000;
        Assert.True(ReceiverLatencySelection.Create(airport, preferences)!.BelowRecommendation);
    }

    [Fact]
    public void RealtimeChoicesRespectReceiverFloorAndAppleTvDefault()
    {
        Assert.Equal(200, ReceiverLatencySelection.Create(Device("AppleTV14,1", true), null)!.Value);
        var device = Device("ThirdParty", true); var preferences = ReceiverPreferences.Of(device);
        var selection = ReceiverLatencySelection.Create(device, preferences, processing: 320)!;
        Assert.Equal(370, selection.Value); selection.Save(preferences, 100); Assert.Equal(350, preferences.RealtimeLatencyMs);
        Assert.Equal(200, ReceiverLatencySelection.Create(device, null)!.Policy.MinimumMs);
    }
}
