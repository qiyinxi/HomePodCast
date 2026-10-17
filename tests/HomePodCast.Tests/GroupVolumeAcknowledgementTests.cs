using System.Collections.Concurrent;
using System.Net;
using System.Text;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class GroupVolumeAcknowledgementTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private sealed class Source : IReceiverAudioSource
    {
        public bool Read(long ticks, Span<float> stereo) { stereo.Fill(.5f); return true; }
        public bool ReadPacket(long ticks) => true;
        public void ReadTarget(int index, Span<float> stereo) => stereo.Fill(.5f);
        public void SkipPackets(int packets) { }
        public void Dispose() { }
    }
    private static AirPlayDevice ThirdParty(FakeAirPlayReceiver fake) => new("Software", fake.DeviceId, IPAddress.Loopback,
        fake.Port, "ThirdParty", new Dictionary<string, string> { ["features"] = "0x0,0x100" });
    private static ReceiverPreferences Realtime(AirPlayDevice device)
    {
        var settings = ReceiverPreferences.Of(device);
        settings.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        return settings;
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    public async Task Confirmed_group_volume_rejects_actual_non_success_replies(int status)
    {
        using var fake = new FakeAirPlayReceiver(false)
        { RefuseRequest = request => request.StartLine.StartsWith("SET_PARAMETER ") ? status : null };
        using var guard = new CancellationTokenSource(Guard);
        using var client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port,
            new StreamOptions(250, 0), ChannelMode.Stereo, guard.Token);
        client.VolumeCapPercent = 25;
        var error = Assert.Throws<AirPlayException>(() => client.SetGroupVolumePercentConfirmed(80));
        Assert.Equal(status, error.ResponseStatusCode);
        Assert.Equal("SET_PARAMETER volume", error.RequestName);
        Assert.Contains(fake.Requests, request => request.StartsWith("SET_PARAMETER ") && request.Contains("refused"));
        Assert.DoesNotContain(fake.Requests, request => request.StartsWith("RECORD"));
        Assert.Empty(fake.Rtp.Audio); Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task Confirmed_group_volume_preserves_live_cap_mute_and_wire_format()
    {
        var bodies = new ConcurrentQueue<string>();
        using var fake = new FakeAirPlayReceiver(false)
        {
            RefuseRequest = request =>
            {
                if (request.StartLine.StartsWith("SET_PARAMETER ")) bodies.Enqueue(Encoding.UTF8.GetString(request.Body));
                return null;
            },
        };
        using var guard = new CancellationTokenSource(Guard);
        using var client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port,
            new StreamOptions(250, 0), ChannelMode.Stereo, guard.Token);
        client.VolumeCapPercent = 25; client.SetGroupVolumePercentConfirmed(80);
        client.SetGroupVolumePercentConfirmed(0); client.SetGroupVolumePercentConfirmed(double.NaN);
        client.VolumeCapPercent = 10; client.SetGroupVolumePercentConfirmed(80);
        Assert.Equal(["volume: -22.500000", "volume: -144.000000", "volume: -144.000000", "volume: -27.000000"], bodies.ToArray());
        Assert.DoesNotContain(fake.Requests, request => request.StartsWith("RECORD"));
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task Independent_Apple_refused_volume_cannot_record_or_start_its_sender()
    {
        using var fake = new FakeAirPlayReceiver(false)
        { RefuseRequest = request => request.StartLine.StartsWith("SET_PARAMETER ") ? 500 : null };
        using var guard = new CancellationTokenSource(Guard);
        var client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port,
            new StreamOptions(250, 0), ChannelMode.Stereo, guard.Token);
        using var session = new AppleGroupMemberSession(client);
        var error = Assert.Throws<AirPlayException>(() => session.Start(new Source(), MediaClock.Now + MediaClock.FromMs(1000), 80, 25));
        Assert.Equal(500, error.ResponseStatusCode); Assert.Equal(25, client.VolumeCapPercent);
        Assert.Null(session.Sender);
        Assert.DoesNotContain(fake.Requests, request => request.StartsWith("RECORD") || request.StartsWith("FLUSH"));
        Assert.Empty(fake.Rtp.Audio); Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task Modern_group_transport_rejects_refused_volume_publication()
    {
        using var fake = new FakeAirPlayReceiver(false)
        { ModelOverride = "ThirdParty", RefuseRequest = request => request.StartLine.StartsWith("SET_PARAMETER ") ? 500 : null };
        using var guard = new CancellationTokenSource(Guard);
        using var clocks = new PtpClockPool();
        var device = ThirdParty(fake);
        using var session = await ReceiverGroupConnections.PrepareAsync(device, Realtime(device),
            new ReceiverConnectOptions(250, 0, 100, null, null, new EffectChain()), clocks, guard.Token);
        var error = Assert.Throws<AirPlayException>(() => session.SetVolume(80, 25));
        Assert.Equal(500, error.ResponseStatusCode);
        Assert.Equal(ReceiverProtocol.AirPlay2Realtime, session.Protocol);
        Assert.DoesNotContain(fake.Requests, request => request.StartsWith("RECORD"));
        Assert.Empty(fake.Rtp.Audio); Assert.Empty(fake.Errors);
    }
}
