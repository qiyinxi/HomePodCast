using System.Net;
using System.Reflection;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class AppleTvPresentationTests
{
    [Fact]
    public async Task Ptp_session_keeps_live_home_statistics_and_clears_them_on_disconnect()
    {
        // A real PTP sender against a loopback fake; no capture, discovery or speaker connection.
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var device = new AirPlayDevice("Software", "001122334455", IPAddress.Loopback, fake.Port,
            "ThirdParty", new Dictionary<string, string> { ["features"] = "0x0,0x200" });
        using var clocks = new PtpClockPool();
        using var client = await AirPlayClient.ConnectBufferedAsync(device, new StreamOptions(200, 10),
            new AudioFifo(44100, 0, 999), guard.Token, timingPorts: fake.TimingPorts,
            realtime: true, prepareOnly: true, clocks: clocks);
        Assert.Null(client.Sender);
        Assert.NotNull(client.PtpRealtime);

        Sta.Run(() =>
        {
            var config = TestApp.Config();
            var app = TestApp.Create(config);
            var c = app.Controller;
            c.ReceiverLookup = _ => device with { Model = "AppleTV14,1" };
            Set(c, "_client", client);
            Set(c, "<State>k__BackingField", StreamState.Streaming);
            Set(c, "<EffectiveLatencyMs>k__BackingField", 200);
            using var page = new HomePage(app);
            page.UpdateState();

            Assert.Same(client.PtpRealtime, c.AudioSessionIdentity);
            Assert.Equal(ReceiverProtocol.AirPlay2Realtime, c.ActiveProtocol);
            long onset = 0;
            c.ObserveAudioOnset(ticks => onset = ticks);
            client.PtpRealtime!.OnsetSent!(123);
            Assert.Equal(123, onset);
            c.ObserveAudioOnset(null);
            Assert.Null(client.PtpRealtime.OnsetSent);

            Assert.Equal("200", TestApp.Field<MetricTile>(page, "_effective").ValueText);
            Assert.Equal("0", TestApp.Field<MetricTile>(page, "_buffer").ValueText);
            Assert.Equal(client.ArrivalToRenderMs!.ToString(), TestApp.Field<MetricTile>(page, "_speakerDelay").ValueText);
            Assert.Equal("0/0", TestApp.Field<MetricTile>(page, "_resent").ValueText);
            Assert.StartsWith("已推送 ", TestApp.Field<TextBlock>(page, "_streamStats").Text);
            Assert.Equal("设备延迟", TestApp.Field<TextBlock>(page, "_sceneHeader").Text);
            var panel = TestApp.Field<ReceiverLatencyPanel>(page, "_receiverLatency");
            Assert.Equal("Apple TV · AirPlay 2 低延迟", TestApp.Field<TextBlock>(panel, "_protocol").Text);
            Assert.DoesNotContain(All(page), control => control.Text == "管理配置");

            Set(c, "_client", null);
            Set(c, "<State>k__BackingField", StreamState.Idle);
            page.UpdateState();
            Assert.Null(c.AudioSessionIdentity);
            Assert.Null(c.ActiveProtocol);
            Assert.Null(c.RetransmissionStats);
            foreach (string tile in new[] { "_effective", "_buffer", "_speakerDelay", "_resent" })
                Assert.Equal("—", TestApp.Field<MetricTile>(page, tile).ValueText);
        });
    }

    private static void Set(object owner, string field, object? value) =>
        owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);

    private static IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control parent)
    {
        foreach (System.Windows.Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in All(child)) yield return descendant;
        }
    }
}
