using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public class CoordinatedReceiverSessionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private sealed class BombEffect : IAudioEffect
    { public void Process(Span<float> pcm, int frames) => throw new InvalidOperationException("Shared history must not run effects twice"); }
    private sealed class Encoder : IAacEncoder
    {
        public int Calls;
        public short[]? First;
        public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> stereo)
        {
            First ??= stereo.ToArray(); Interlocked.Increment(ref Calls);
            return [new byte[] { 0x21, 0x10, 0x04, 0x60 }];
        }
        public void Dispose() { }
    }
    private static AirPlayDevice Device(FakeAirPlayReceiver fake) => new("Software", "001122334455", IPAddress.Loopback,
        fake.Port, fake.ModelOverride ?? "ThirdParty", new Dictionary<string, string> { ["features"] = "0x0,0x200" });
    private static ReceiverTimelineSource History(long start, int latency)
    {
        var timeline = new TimelineBuffer(44100, 44100 * 8, start - MediaClock.FromMs(3000));
        var samples = new float[44100 * 8 * 2];
        for (int i = 0; i < samples.Length; i += 2) { samples[i] = .25f; samples[i + 1] = -.5f; }
        timeline.WriteFrames(0, samples);
        return new(timeline, 2000, 0, latency, ChannelMode.Stereo);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(5, guard.Token);
    }

    [Fact]
    public async Task Prepared_NTP_reads_only_history_at_the_supplied_epoch()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(Guard);
        var options = new StreamOptions(250, 10) { PairingOverRtsp = true, Effects = new BombEffect() };
        using var client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port, options, ChannelMode.Stereo, guard.Token);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedNtp(options, History(start, 250), start);
        await Until(() => !fake.Rtp.Audio.IsEmpty);
        Assert.Equal(start, client.Sender!.T0);
        byte[] pcm = FakeReceiver.Decrypt(fake.Rtp.Audio.First().Bytes, fake.StreamKey!);
        Assert.Equal((short)8192, BinaryPrimitives.ReadInt16BigEndian(pcm));
        Assert.Equal((short)-16384, BinaryPrimitives.ReadInt16BigEndian(pcm.AsSpan(2)));
        Assert.Empty(fake.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_prepared_transport_cannot_start_after_local_stop(bool ptp)
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(Guard);
        using var clocks = new PtpClockPool();
        var options = new StreamOptions(250, 10) { PairingOverRtsp = true };
        using var client = ptp
            ? await AirPlayClient.ConnectBufferedAsync(Device(fake), options, new AudioFifo(44100, 0, 0), guard.Token,
                timingPorts: fake.TimingPorts, realtime: true, prepareOnly: true, clocks: clocks)
            : await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port, options, ChannelMode.Stereo, guard.Token);
        client.StopCoordinatedSending();
        long start = MediaClock.Now + MediaClock.FromMs(250);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            if (ptp) client.StartCoordinatedPtp(options, History(start, 250), start, true);
            else client.StartCoordinatedNtp(options, History(start, 250), start);
        });
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Empty(fake.Rtp.Audio);
    }

    [Fact]
    public async Task Prepared_buffered_session_does_not_record_or_encode_until_coordinated_start()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        using var fake = new FakeAirPlayReceiver(false)
        { ModelOverride = "Shairport Sync", BufferedDataPort = ((IPEndPoint)tcp.LocalEndpoint).Port };
        using var guard = new CancellationTokenSource(Guard);
        var accepted = tcp.AcceptTcpClientAsync(guard.Token).AsTask();
        var encoder = new Encoder();
        using var clocks = new PtpClockPool();
        var options = new StreamOptions(1500, 10) { Effects = new BombEffect() };
        using var client = await AirPlayClient.ConnectBufferedAsync(Device(fake), options, new AudioFifo(44100, 0, 999), guard.Token,
            () => encoder, fake.TimingPorts, prepareOnly: true, clocks: clocks);
        using var receiver = await accepted;
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Null(fake.RateAnchor);
        Assert.Equal(0, encoder.Calls);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedPtp(options, History(start, 1500), start, false);
        byte[] size = new byte[2];
        await receiver.GetStream().ReadExactlyAsync(size, guard.Token);
        var packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(size) - 2];
        await receiver.GetStream().ReadExactlyAsync(packet, guard.Token);
        Assert.Equal(start, client.Buffered!.StartedAt);
        Assert.Equal((short)8192, encoder.First![0]); Assert.Equal((short)-16384, encoder.First[1]);
        ulong seconds = Convert.ToUInt64(fake.RateAnchor!["networkTimeSecs"]);
        object fractionValue = fake.RateAnchor["networkTimeFrac"]!;
        ulong fraction = fractionValue is long signed ? unchecked((ulong)signed) : Convert.ToUInt64(fractionValue);
        ulong projected = seconds * 1_000_000_000 + (ulong)((UInt128)fraction * 1_000_000_000 >> 64);
        Assert.InRange(Math.Abs((long)projected - (long)PtpMaster.NanosecondsAt(start + MediaClock.FromMs(1500))), 0, 1);
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task Prepared_PTP_realtime_uses_the_common_future_epoch_and_history_callback()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "Shairport Sync" };
        using var guard = new CancellationTokenSource(Guard);
        using var clocks = new PtpClockPool();
        var options = new StreamOptions(250, 10) { Effects = new BombEffect() };
        using var client = await AirPlayClient.ConnectBufferedAsync(Device(fake), options, new AudioFifo(44100, 0, 999), guard.Token,
            timingPorts: fake.TimingPorts, realtime: true, prepareOnly: true, clocks: clocks);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Equal(0, client.PtpRealtime!.PacketsSent);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedPtp(options, History(start, 250), start, true);
        await Until(() => client.PtpRealtime.PacketsSent > 0);
        Assert.Equal(start, client.PtpRealtime.StartedAt);
        Assert.Single(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task Two_negotiated_PTP_transports_share_a_clock_without_competing_for_ports()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        using var realtime = new FakeAirPlayReceiver(false) { ModelOverride = "Shairport Sync" };
        using var buffered = new FakeAirPlayReceiver(false)
        { ModelOverride = "Shairport Sync", BufferedDataPort = ((IPEndPoint)tcp.LocalEndpoint).Port };
        using var guard = new CancellationTokenSource(Guard);
        var accepted = tcp.AcceptTcpClientAsync(guard.Token).AsTask();
        var encoder = new Encoder();
        ReceiverGroupOutputOptions Output(FakeAirPlayReceiver receiver, string id, ReceiverProtocol protocol)
        {
            var device = Device(receiver) with { DeviceId = id };
            var settings = ReceiverPreferences.Of(device) with { ProtocolOverride = protocol, CompatibilityLatencyMs = 1500 };
            return new(id, id, _ => Task.FromResult(new ReceiverGroupTarget(device, settings)),
                new ReceiverConnectOptions(250, 0, 25, null, null, new BombEffect()) { TimingPorts = receiver.TimingPorts });
        }
        using var outputs = await ReceiverOutputSet.PrepareAsync([
            Output(realtime, "0011", ReceiverProtocol.AirPlay2Realtime), Output(buffered, "0022", ReceiverProtocol.AirPlay2Buffered),
        ], () => 25, guard.Token, connect: (_, device, settings, options, clocks, ct) =>
            ReceiverGroupConnections.PrepareAsync(device, settings, options, clocks, ct, encoder: () => encoder));
        using var socket = await accepted;
        var realtimePeer = Assert.IsType<Dictionary<string, object?>>(realtime.SessionSetup!["timingPeerInfo"]);
        var bufferedPeer = Assert.IsType<Dictionary<string, object?>>(buffered.SessionSetup!["timingPeerInfo"]);
        Assert.Equal(realtimePeer["ClockID"], bufferedPeer["ClockID"]);
        Assert.DoesNotContain(realtime.Requests.Concat(buffered.Requests), r => r.StartsWith("RECORD"));
        Assert.Equal(0, encoder.Calls);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        var timeline = new TimelineBuffer(44100, 44100 * 8, start - MediaClock.FromMs(3000));
        var samples = new float[44100 * 8 * 2];
        for (int i = 0; i < samples.Length; i += 2) { samples[i] = .25f; samples[i + 1] = -.5f; }
        timeline.WriteFrames(0, samples);
        await outputs.StartAsync(timeline, 2000, 10, false, start, guard.Token);
        byte[] size = new byte[2]; await socket.GetStream().ReadExactlyAsync(size, guard.Token);
        byte[] packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(size) - 2];
        await socket.GetStream().ReadExactlyAsync(packet, guard.Token);
        await Until(() => !realtime.Rtp.Audio.IsEmpty);
        Assert.All(outputs.Status, output => { Assert.True(output.UsesPtp); Assert.Equal(OutputState.Streaming, output.State); });
        Assert.Equal((short)8192, encoder.First![0]); Assert.Equal((short)-16384, encoder.First[1]);
        Assert.Empty(realtime.Errors); Assert.Empty(buffered.Errors);
    }
}
