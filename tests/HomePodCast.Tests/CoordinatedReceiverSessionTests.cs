using System.Buffers.Binary;
using System.Collections.Concurrent;
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
    /// <summary>
    /// Eight seconds of constant history, built before the test chooses its epoch. 2026-10-17: allocating and filling
    /// it after the choice took up to 640 ms of the 250 ms margin on this PC under forced GC load, so the leg started
    /// after its epoch. Any epoch chosen in the next few seconds reads well inside it.
    /// </summary>
    private static TimelineBuffer Timeline()
    {
        var timeline = new TimelineBuffer(44100, 44100 * 8, MediaClock.Now - MediaClock.FromMs(2750));
        var samples = new float[44100 * 8 * 2];
        for (int i = 0; i < samples.Length; i += 2) { samples[i] = .25f; samples[i + 1] = -.5f; }
        timeline.WriteFrames(0, samples);
        return timeline;
    }
    private static ReceiverTimelineSource History(int latency) => new(Timeline(), 2000, 0, latency, ChannelMode.Stereo);

    /// <summary>Waits for the condition, and fails at once with the transport's reason when it records a loss first.</summary>
    private static async Task Until(Func<bool> condition, Func<string?> lost)
    {
        // 2026-10-16: CI runs 38964883151 and 38970600060 waited the whole guard for a first packet and then said only
        // "A task was canceled"; what the transport had recorded when it gave up never reached the failure.
        long began = MediaClock.Now;
        while (!condition())
        {
            if (lost() is { } reason) Assert.Fail($"The transport was lost first: {reason}");
            if (MediaClock.Now - began > MediaClock.FromMs(Guard.TotalMilliseconds))
                Assert.Fail($"Still waiting after {Guard.TotalSeconds:F0} s, and no loss was recorded");
            await Task.Delay(5);
        }
    }

    /// <summary>The first frame on the buffered TCP stream; a sender that gives up leaves it open, so a loss ends the wait.</summary>
    private static async Task<byte[]> FirstBufferedPacket(TcpClient receiver, Func<string?> lost, CancellationToken ct)
    {
        async Task<byte[]> Read()
        {
            byte[] size = new byte[2];
            await receiver.GetStream().ReadExactlyAsync(size, ct);
            var packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(size) - 2];
            await receiver.GetStream().ReadExactlyAsync(packet, ct);
            return packet;
        }
        var reading = Read();
        while (!reading.IsCompleted)
        {
            if (lost() is { } reason) Assert.Fail($"The transport was lost first: {reason}");
            await Task.WhenAny(reading, Task.Delay(5));
        }
        try { return await reading; }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Assert.Fail($"No first buffered packet ({error.GetType().Name}): {lost() ?? "no loss was recorded"}");
            throw;
        }
    }

    private static Task<AirPlayClient> PrepareRealtime(FakeAirPlayReceiver fake, StreamOptions options, PtpClockPool clocks,
        CancellationToken ct) => AirPlayClient.ConnectBufferedAsync(Device(fake), options, new AudioFifo(44100, 0, 999), ct,
            timingPorts: fake.TimingPorts, realtime: true, prepareOnly: true, clocks: clocks);

    [Fact]
    public async Task Prepared_NTP_reads_only_history_at_the_supplied_epoch()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(Guard);
        var options = new StreamOptions(250, 10) { PairingOverRtsp = true, Effects = new BombEffect() };
        using var client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port, options, ChannelMode.Stereo, guard.Token);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        var history = History(250);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedNtp(options, history, start);
        await Until(() => !fake.Rtp.Audio.IsEmpty, () => client.LostReason);
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
        var history = History(250);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            if (ptp) client.StartCoordinatedPtp(options, history, start, true);
            else client.StartCoordinatedNtp(options, history, start);
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
        var history = History(1500);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedPtp(options, history, start, false);
        await FirstBufferedPacket(receiver, () => client.LostReason, guard.Token);
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
        using var client = await PrepareRealtime(fake, options, clocks, guard.Token);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Equal(0, client.PtpRealtime!.PacketsSent);
        var history = History(250);
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedPtp(options, history, start, true);
        await Until(() => client.PtpRealtime.PacketsSent > 0, () => client.LostReason);
        Assert.Equal(start, client.PtpRealtime.StartedAt);
        Assert.Single(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task A_late_PTP_realtime_start_joins_the_common_timeline_instead_of_failing()
    {
        // 2026-10-17: a leg whose Start came more than 60 ms after the group's epoch, because the RTSP round trips or a
        // busy machine took that long, failed with "missed its deadline" before sending anything, and lost the session.
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(Guard);
        using var clocks = new PtpClockPool();
        var options = new StreamOptions(250, 10) { Effects = new BombEffect() };
        using var client = await PrepareRealtime(fake, options, clocks, guard.Token);
        var history = History(250);
        var reads = new ConcurrentQueue<long>();
        long start = MediaClock.Now - MediaClock.FromMs(1000);
        client.StartCoordinatedPtp(options, (ticks, stereo) => { reads.Enqueue(ticks); return history.Read(ticks, stereo); }, start, true);
        var sender = client.PtpRealtime!;
        await Until(() => sender.PacketsSent > 0, () => client.LostReason);
        await Until(() => !fake.Rtp.Audio.IsEmpty, () => null);
        Assert.Equal(start, sender.StartedAt);
        byte[] first = fake.Rtp.Audio.First().Bytes;
        Assert.Equal(0xe0, first[1]); // the marker of the first packet after the FLUSH
        int packet = unchecked((ushort)(FakeReceiver.Seq(first) - sender.FirstSeq));
        Assert.True(packet >= 44100 / 352, $"joined at packet {packet}, less than a second after the epoch");
        Assert.Equal(unchecked(sender.RtpBase + (uint)(packet * 352)), FakeReceiver.Rtp(first));
        Assert.True(reads.TryPeek(out long read));
        Assert.Equal(start + (long)(packet * 352.0 * MediaClock.Frequency / 44100), read);
        byte[] pcm = FakeReceiver.Decrypt(first, fake.StreamKey!);
        Assert.Equal((short)8192, BinaryPrimitives.ReadInt16BigEndian(pcm));
        Assert.Equal((short)-16384, BinaryPrimitives.ReadInt16BigEndian(pcm.AsSpan(2)));
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task PTP_realtime_packets_are_paced_off_the_thread_pool()
    {
        // 2026-10-17: with the pool sized for 4 cores, as on a CI runner, and blocking work queued on it, the pooled send
        // loop woke more than 60 ms after packet 0's deadline in 5 of 20 starts, although Start had returned 247 ms early.
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(Guard);
        using var clocks = new PtpClockPool();
        var options = new StreamOptions(250, 10);
        using var client = await PrepareRealtime(fake, options, clocks, guard.Token);
        var history = History(250);
        var pooled = new ConcurrentQueue<bool>();
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedPtp(options, (ticks, stereo) =>
        {
            pooled.Enqueue(Thread.CurrentThread.IsThreadPoolThread);
            return history.Read(ticks, stereo);
        }, start, true);
        await Until(() => !pooled.IsEmpty, () => client.LostReason);
        Assert.DoesNotContain(true, pooled);
    }

    [Fact]
    public async Task A_PTP_realtime_stall_after_the_first_packet_still_ends_the_session_with_its_reason()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty" };
        using var guard = new CancellationTokenSource(Guard);
        using var clocks = new PtpClockPool();
        var options = new StreamOptions(250, 10);
        using var client = await PrepareRealtime(fake, options, clocks, guard.Token);
        var history = History(250);
        int reads = 0;
        long start = MediaClock.Now + MediaClock.FromMs(250);
        client.StartCoordinatedPtp(options, (ticks, stereo) =>
        {
            // The third packet's audio takes 200 ms to come, so the packets after it cannot be on time any more.
            if (Interlocked.Increment(ref reads) == 3) Thread.Sleep(200);
            return history.Read(ticks, stereo);
        }, start, true);
        await Until(() => client.LostReason != null, () => null);
        Assert.StartsWith("PTP realtime sender missed its deadline: packet ", client.LostReason);
        Assert.True(client.PtpRealtime!.PacketsSent > 0);
    }

    [Fact]
    public async Task A_late_buffered_start_joins_the_common_timeline_instead_of_failing()
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
        var history = History(1500);
        var reads = new ConcurrentQueue<long>();
        long start = MediaClock.Now - MediaClock.FromMs(1000);
        client.StartCoordinatedPtp(options, (ticks, stereo) => { reads.Enqueue(ticks); return history.Read(ticks, stereo); }, start, false);
        byte[] packet = await FirstBufferedPacket(receiver, () => client.LostReason, guard.Token);
        var sender = client.Buffered!;
        Assert.Equal(start, sender.StartedAt);
        uint frames = unchecked(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) - sender.RtpBase);
        Assert.Equal(0u, frames % 1024);
        Assert.True(frames >= 44100 / 1024 * 1024, $"joined at frame {frames}, less than a second after the epoch");
        Assert.True(reads.TryPeek(out long read));
        Assert.Equal(start + (long)(frames * (double)MediaClock.Frequency / 44100), read);
        Assert.Equal((short)8192, encoder.First![0]); Assert.Equal((short)-16384, encoder.First[1]);
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
        string? Lost() => outputs.LostReason
            ?? outputs.Status.Where(s => s.Error != null).Select(s => $"{s.Name}: {s.Error}").FirstOrDefault();
        using var socket = await accepted;
        var realtimePeer = Assert.IsType<Dictionary<string, object?>>(realtime.SessionSetup!["timingPeerInfo"]);
        var bufferedPeer = Assert.IsType<Dictionary<string, object?>>(buffered.SessionSetup!["timingPeerInfo"]);
        Assert.Equal(realtimePeer["ClockID"], bufferedPeer["ClockID"]);
        Assert.DoesNotContain(realtime.Requests.Concat(buffered.Requests), r => r.StartsWith("RECORD"));
        Assert.Equal(0, encoder.Calls);
        var timeline = Timeline();
        long start = MediaClock.Now + MediaClock.FromMs(250);
        await outputs.StartAsync(timeline, 2000, 10, false, start, guard.Token);
        await FirstBufferedPacket(socket, Lost, guard.Token);
        await Until(() => !realtime.Rtp.Audio.IsEmpty, Lost);
        Assert.All(outputs.Status, output => { Assert.True(output.UsesPtp); Assert.Equal(OutputState.Streaming, output.State); });
        Assert.Equal((short)8192, encoder.First![0]); Assert.Equal((short)-16384, encoder.First[1]);
        Assert.Empty(realtime.Errors); Assert.Empty(buffered.Errors);
    }
}
