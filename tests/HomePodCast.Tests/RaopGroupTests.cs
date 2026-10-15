using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class RaopGroupTests
{
    private sealed class Gain : IAudioEffect
    {
        public int Calls;
        public void Process(Span<float> stereo, int frames)
        {
            Interlocked.Increment(ref Calls);
            for (int i = 0; i < stereo.Length; i++) stereo[i] *= 0.5f;
        }
    }

    private static RaopGroupOutputOptions Output(RaopOutputSetTests.FakeGroupRaop receiver, bool required = false) =>
        new("raop", "RAOP", IPAddress.Loopback, receiver.Port, new RaopOptions
        {
            Txt = new Dictionary<string, string> { ["et"] = "0", ["cn"] = "1" },
            EndTimeout = TimeSpan.FromSeconds(5),
        }) { Required = required };

    private static async Task Until(Func<bool> ready, CancellationToken ct)
    {
        while (!ready()) await Task.Delay(5, ct);
    }

    private static int Marker(ReadOnlySpan<short> pcm)
    {
        for (int i = 0; i < pcm.Length / 2; i++)
            if (pcm[i * 2] == 12288 && pcm[i * 2 + 1] == -4096) return i;
        return -1;
    }

    private static int AirPlayMarker(FakeReceiver receiver, byte[] packet)
    {
        byte[] payload = receiver.Decrypt(packet);
        for (int i = 0; i < payload.Length / 4; i++)
            if (BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(i * 4)) == 12288
                && BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(i * 4 + 2)) == -4096) return i;
        return -1;
    }

    private static async Task<uint> ReadRaopMarker(UdpClient audio, CancellationToken ct)
    {
        while (true)
        {
            byte[] packet = (await audio.ReceiveAsync(ct)).Buffer;
            int frame = Marker(RaopTests.DecodeEscape(packet[12..]));
            if (frame >= 0) return unchecked(FakeReceiver.Rtp(packet) + (uint)frame);
        }
    }

    private static async Task<uint> ReadAirPlayMarker(FakeReceiver receiver, CancellationToken ct)
    {
        while (true)
        {
            while (receiver.Audio.TryDequeue(out var packet))
            {
                int frame = AirPlayMarker(receiver, packet.Bytes);
                if (frame >= 0) return unchecked(FakeReceiver.Rtp(packet.Bytes) + (uint)frame);
            }
            await Task.Delay(5, ct);
        }
    }

    // Independently interpret the receiver's sync mapping, not the sender's wall-clock arrival time.
    private static Int128 Presentation(byte[] sync, uint marker, int extraLatencyFrames)
    {
        uint anchor = BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(4));
        long delta = unchecked((int)(marker - anchor)) + (long)extraLatencyFrames;
        return (Int128)BinaryPrimitives.ReadUInt64BigEndian(sync.AsSpan(8)) + ((Int128)delta << 32) / 44100;
    }

    [Fact]
    public async Task Mixed_protocol_markers_have_the_same_presentation_time_and_effects_run_once()
    {
        // A starved runner deliberately skips timeline packets. Only retry such a run, as the existing fan-out tests do.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var airplay = new FakeReceiver();
            using var raop = new RaopOutputSetTests.FakeGroupRaop();
            using var outputs = await RaopOutputSet.PrepareAsync([Output(raop)], () => 10, default);
            var member = new FakeMember("AirPlay", airplay, new ConcurrentQueue<string>()) { LatencyFrames = 44100 };
            var fifo = new AudioFifo(44100, 0, 1000);
            var signal = new float[44100];
            const int markerFrame = 352 * 10 + 123;
            signal[markerFrame * 2] = 0.75f;
            signal[markerFrame * 2 + 1] = -0.25f;
            fifo.Write(signal);
            var gain = new Gain();
            using var group = await SpeakerGroup.ConnectAsync([member.Setup()], fifo, 10, default,
                effects: gain, raopOutputs: outputs, groupDelayMs: 2020);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                var raopMarker = ReadRaopMarker(raop.Audio, timeout.Token);
                var airplayMarker = ReadAirPlayMarker(airplay, timeout.Token);
                await Task.WhenAll(raopMarker, airplayMarker);
                await Until(() => !airplay.Sync.IsEmpty && !raop.Sync.IsEmpty, timeout.Token);
                if (group.TimelineSkippedPackets > 0 || group.Sender!.SkippedPackets > 0) continue;
                var apTime = Presentation(airplay.Sync.First().Bytes, await airplayMarker, member.LatencyFrames);
                var raopTime = Presentation(raop.Sync.First(), await raopMarker, 0);
                double errorMs = (double)(raopTime - apTime) * 1000 / 4294967296.0;
                Assert.InRange(errorMs, -1, 1);
                Assert.True(group.Stats.PacketsSent > group.Sender.PacketsSent);
                group.Dispose();
                Assert.Equal(group.TimelineProducedPackets, gain.Calls);
                Assert.True(gain.Calls > 10);
                return;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested
                && (group.TimelineSkippedPackets > 0 || group.Sender!.SkippedPackets > 0)) { }
        }
        Assert.Fail("the producer or AirPlay sender skipped packets in every mixed-protocol attempt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RAOP_can_stream_alone_or_with_a_local_output_without_an_AirPlay_sender(bool includeLocal)
    {
        using var receiver = new RaopOutputSetTests.FakeGroupRaop();
        using var outputs = await RaopOutputSet.PrepareAsync([Output(receiver)], () => 10, default);
        var device = new FakeLocalRenderDevice();
        using var locals = includeLocal ? await LocalOutputSet.PrepareAsync([new("local", "Local")],
            () => "capture", () => 10, default, _ => device) : null;
        using var group = await SpeakerGroup.ConnectAsync([], new AudioFifo(44100, 0, 1000), 5, default,
            localOutputs: locals, raopOutputs: outputs, groupDelayMs: 2020);
        await receiver.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(group.Sender);
        Assert.NotNull(group.Timeline);
        Assert.Equal(OutputKind.Raop, group.Kind);
        Assert.True(group.Stats.PacketsSent > 0);
        group.SetVolume(0);
        Assert.Equal(-144, receiver.Volumes.Last());
        group.Dispose();
        Assert.True(!includeLocal || device.Disposed);
        await receiver.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_required_RAOP_takeover_ends_the_group_but_an_optional_one_keeps_AirPlay_running(bool required)
    {
        using var receiver = new RaopOutputSetTests.FakeGroupRaop();
        using var airplay = new FakeReceiver();
        var member = new FakeMember("AirPlay", airplay, new ConcurrentQueue<string>());
        using var outputs = await RaopOutputSet.PrepareAsync([Output(receiver, required)], () => 10, default);
        using var group = await SpeakerGroup.ConnectAsync([member.Setup()], new AudioFifo(44100, 0, 1000), 10, default,
            raopOutputs: outputs, groupDelayMs: 2020);
        await receiver.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var sender = group.Sender;
        receiver.CloseConnection(reset: false);
        await receiver.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        group.SetVolume(10); // Observe the orderly FIN with a protocol command, without racing the 5 s keepalive timer.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Until(() => outputs.Status[0].NeedsUserAction, timeout.Token);
        if (required)
        {
            await group.Lost.WaitAsync(timeout.Token);
            Assert.Equal(RaopClient.ClosedByReceiver, group.LostReason);
            Assert.True(group.LostRequiresUserAction);
        }
        else
        {
            Assert.False(group.Lost.IsCompleted);
            Assert.Same(sender, group.Sender);
            Assert.False(member.Disposed);
            group.SetVolume(0);
            Assert.Equal(0, member.Volumes.Last());
        }
    }

    [Fact]
    public async Task A_RAOP_only_group_stops_when_its_last_optional_receiver_requires_user_action()
    {
        using var receiver = new RaopOutputSetTests.FakeGroupRaop();
        using var outputs = await RaopOutputSet.PrepareAsync([Output(receiver)], () => 10, default);
        using var group = await SpeakerGroup.ConnectAsync([], new AudioFifo(44100, 0, 1000), 10, default,
            raopOutputs: outputs, groupDelayMs: 2020);
        await receiver.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        receiver.CloseConnection(reset: false);
        await receiver.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        group.SetVolume(10);
        await group.Lost.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RaopClient.ClosedByReceiver, group.LostReason);
        Assert.True(group.LostRequiresUserAction);
    }

    [Fact]
    public async Task Required_takeover_during_AirPlay_preparation_preserves_its_terminal_reason()
    {
        using var receiver = new RaopOutputSetTests.FakeGroupRaop();
        using var airplay = new FakeReceiver();
        var member = new FakeMember("AirPlay", airplay, new ConcurrentQueue<string>());
        using var outputs = await RaopOutputSet.PrepareAsync([Output(receiver, required: true)], () => 10, default);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = new MemberSetup("AirPlay", async ct =>
        {
            preparing.TrySetResult();
            await release.Task.WaitAsync(ct);
            return member;
        });
        var connecting = SpeakerGroup.ConnectAsync([setup], new AudioFifo(44100, 0, 1000), 10, default,
            raopOutputs: outputs, groupDelayMs: 2020);
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receiver.CloseConnection(reset: false);
        await receiver.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outputs.SetVolume(10);
        await outputs.Lost.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Assert.ThrowsAsync<RaopRefusedException>(() => connecting);
        Assert.True(member.Disposed);
        Assert.Equal(0, receiver.Audio.Available);
    }
}
