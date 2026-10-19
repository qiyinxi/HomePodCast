using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;
using HomePodCast.Tests;

namespace HomePodCast.ProtocolHarness;

/// <summary>A fresh-process, loopback-only check of the paced PTP sender through the real shared-history reader.</summary>
internal static class PtpColdStart
{
    private const int PacketCount = 256, FramesPerPacket = 352, LatencyMs = 500, HeadroomMs = 20;

    public static async Task Run()
    {
        using var diagnostics = new CompatibilityDiagnostics(traceJit: true);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var receiver = new FakeReceiver();
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clock = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, true, eventPort: 0, generalPort: 0);
        using var decrypt = new ChaCha20Poly1305(receiver.Key);
        var pcm = new byte[FramesPerPacket * 4];
        var source = new float[44100 * 6 * 2];
        for (int frame = 0; frame < source.Length / 2; frame++)
            for (int channel = 0; channel < 2; channel++) source[frame * 2 + channel] = Sample(frame, channel) / 32767f;
        // Both large history arrays exist before choosing the epoch. The signal's first frame can then be placed
        // on that history's integer frame grid without changing the requested 500 ms transport latency.
        var timeline = new TimelineBuffer(44100, source.Length / 2, MediaClock.Now);
        var device = new AirPlayDevice("Cold loopback PTP", "cold-ptp", IPAddress.Loopback, 1, "ThirdParty",
            new Dictionary<string, string> { ["features"] = "0x0,0x200" });
        var settings = ReceiverPreferences.Of(device) with { ProtocolOverride = ReceiverProtocol.AirPlay2Realtime };
        var options = new ReceiverConnectOptions(LatencyMs, 10, 10, null, null, new RejectEffects(), null);
        Prepared? prepared = null;
        ReceiverOutputSet? outputs = null;
        try
        {
            outputs = await ReceiverOutputSet.PrepareAsync([new(device.DeviceId, device.Name,
                _ => Task.FromResult(new ReceiverGroupTarget(device, settings)), options)], () => 10, guard.Token,
                connect: (protocol, _, _, _, _, _) =>
                {
                    if (protocol != ReceiverProtocol.AirPlay2Realtime) throw new InvalidOperationException("Unexpected cold-test protocol");
                    prepared = new(receiver, clock);
                    return Task.FromResult(diagnostics.Observe(prepared));
                });
            var transport = prepared ?? throw new InvalidOperationException("Cold PTP transport was not prepared");
            long firstFrame = timeline.FrameAt(MediaClock.Now + MediaClock.FromMs(500));
            long began = timeline.TicksAt(firstFrame), startAt = began + MediaClock.FromMs(HeadroomMs);
            timeline.WriteFrames(firstFrame, source);
            // Do not prepare or invoke Send before this call: the first real invocation in this process must stay cold.
            await outputs.StartAsync(timeline, LatencyMs + HeadroomMs, 10, false, startAt, guard.Token);
            while (transport.Sender.PacketsSent < PacketCount || receiver.Audio.Count < PacketCount)
            {
                ThrowIfLost(transport);
                if (guard.IsCancellationRequested)
                    throw new TimeoutException($"Cold PTP timed out: sent {transport.Sender.PacketsSent}, received {receiver.Audio.Count}, expected {PacketCount}");
                await Task.WhenAny(transport.Lost, Task.Delay(5, guard.Token));
            }
            ThrowIfLost(transport);
            outputs.Dispose();
            transport.Dispose();
            for (int packet = 0; packet < PacketCount; packet++)
            {
                if (!receiver.Audio.TryDequeue(out var captured)) throw new Exception($"Cold PTP packet {packet} is missing");
                Validate(receiver, transport.Sender, captured.Bytes, packet, decrypt, pcm);
            }
            // A fast runner may compile within 60 ms; the same synchronous optimization still consumes the sender's
            // budget. Observe that work directly so the regression does not rely on a slow runner to detect it.
            diagnostics.RequireNoSenderThreadRecompilation();
            Console.WriteLine($"PASS: cold loopback PTP, {PacketCount} continuous packets, {PacketCount * FramesPerPacket} exact stereo frames");
        }
        finally
        {
            try { outputs?.Dispose(); }
            finally
            {
                try { prepared?.Dispose(); }
                finally { diagnostics.Report("cold-stopped"); }
            }
        }
    }

    private static void ThrowIfLost(Prepared prepared)
    {
        if (prepared.Lost.IsCompleted)
            throw new IOException($"Cold PTP sender failed: {prepared.Lost.GetAwaiter().GetResult()}", prepared.LostException);
    }

    private static short Sample(int frame, int channel) => (short)((frame * 17 + channel * 997) % 20001 - 10000);

    private sealed class RejectEffects : IAudioEffect
    {
        public void Process(Span<float> audio, int frames) => throw new InvalidOperationException("Cold PTP must read processed history without running effects again");
    }

    private static void Validate(FakeReceiver receiver, PtpRealtimeSender sender, byte[] packet, int index,
        ChaCha20Poly1305 decrypt, Span<byte> pcm)
    {
        if (packet.Length != 12 + pcm.Length + CounterCipher.TagSize + 8
            || FakeReceiver.Seq(packet) != unchecked((ushort)(sender.FirstSeq + index))
            || FakeReceiver.Rtp(packet) != unchecked(sender.RtpBase + (uint)(index * FramesPerPacket))
            || FakeReceiver.SsrcOf(packet) != receiver.Ssrc || FakeReceiver.Counter(packet) != (ulong)index
            || packet[1] != (index == 0 ? 0xe0 : 0x60))
            throw new Exception($"Cold PTP packet {index} has a gap, wrong timeline or invalid header");
        Span<byte> nonce = stackalloc byte[12];
        CounterCipher.MakeNonce(nonce, (ulong)index);
        decrypt.Decrypt(nonce, packet.AsSpan(12, pcm.Length), packet.AsSpan(12 + pcm.Length, CounterCipher.TagSize),
            pcm, packet.AsSpan(4, 8));
        for (int frame = 0; frame < FramesPerPacket; frame++)
            for (int channel = 0; channel < 2; channel++)
                if (BinaryPrimitives.ReadInt16BigEndian(pcm.Slice(frame * 4 + channel * 2, 2))
                    != Sample(index * FramesPerPacket + frame, channel))
                    throw new Exception($"Cold PTP PCM differs at frame {index * FramesPerPacket + frame}, channel {channel}");
    }

    private sealed class Prepared : IPreparedReceiverSession
    {
        private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _dispose = new();
        private Exception? _failure;
        public PtpRealtimeSender Sender { get; }
        public ReceiverProtocol Protocol => ReceiverProtocol.AirPlay2Realtime;
        public bool UsesPtp => true;
        public int LatencyMs => PtpColdStart.LatencyMs;
        public int? ArrivalToRenderMs => null;
        public IReadOnlyDictionary<string, object?> Info { get; } = new Dictionary<string, object?> { ["model"] = "Cold loopback fake" };
        public Task<string> Lost => _lost.Task;
        public Exception? LostException => Volatile.Read(ref _failure);
        public long PacketsSent => Sender.PacketsSent;

        public Prepared(FakeReceiver receiver, PtpMaster clock)
        {
            Sender = new(IPAddress.Loopback, IPAddress.Loopback, receiver.DataPort, receiver.ControlPort, receiver.SenderControl,
                receiver.Key, receiver.Ssrc, LatencyMs * 44100 / 1000, false, new AudioFifo(44100, 0, 0), null, clock);
            Sender.Failed += error => { Interlocked.CompareExchange(ref _failure, error, null); _lost.TrySetResult(error.Message); };
        }

        public void Start(IReceiverAudioSource source, long startAt, double percent, double cap) => Sender.Start(startAt, source.Read);
        public void SetVolume(double percent, double cap) { } // the fake has no render endpoint or hardware volume
        public void StopSending() => Sender.StopSending();
        public void Dispose() { lock (_dispose) Sender.Dispose(); }
    }
}
