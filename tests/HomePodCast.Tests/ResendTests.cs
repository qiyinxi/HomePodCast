using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>Resend requests (0xD5) on a session's control port: whose are answered, and how the unanswerable are counted.</summary>
public class ResendTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static AudioFifo Primed()
    {
        var fifo = new AudioFifo(44100, targetMs: 20, capMs: 2000);
        fifo.Write(TestAudio.Signal(44100));
        return fifo;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > Guard) throw new TimeoutException($"Still waiting for {what} after {Guard.TotalSeconds:F0} s");
            await Task.Delay(5);
        }
    }

    private static byte[] ResendRequest(ushort firstLost, ushort count)
    {
        var request = new byte[8];
        request[0] = 0x80;
        request[1] = 0xD5;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), firstLost);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), count);
        return request;
    }

    [Fact]
    public async Task Only_the_speakers_own_resend_requests_are_answered()
    {
        // Any 8-byte datagram on the LAN made the sender send up to 64 packets (about 92 KB) wherever it said it came
        // from, and counted as the speaker's requests in 首页's network hint.
        using var rx = new FakeReceiver();
        using var other = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        using var sender = new RtpSender([rx.Target(firstSeq: 1000)], 4630, Primed());
        sender.Start(MediaClock.Now + MediaClock.FromMs(10));
        await Until(() => rx.Audio.Count >= 4, "four packets");

        other.Send(ResendRequest(1000, 2), 8, rx.SenderControlEndPoint);
        rx.RequestResend(1001, 1);
        await Until(() => sender.Retransmitted + sender.RetransmitMisses >= 1, "the speaker's request to be answered");
        await Task.Delay(100); // the other host's request came first: whatever it got is in by now

        Assert.Equal(1, sender.RetransmitRequests);
        Assert.Equal(1001, FakeReceiver.Seq(Assert.Single(rx.Resent).Bytes));
        Assert.Equal(0, other.Available);
    }

    /// <summary>Holds the sender thread once, the way a CPU spike or a GC pause on this PC would.</summary>
    private sealed class StallOnce(int packet, int ms) : IAudioEffect
    {
        private int _packets;
        public void Process(Span<float> pcm, int frames)
        {
            if (++_packets == packet) Thread.Sleep(ms);
        }
    }

    [Fact]
    public async Task A_request_for_packets_a_stall_skipped_is_no_network_loss()
    {
        // After a stall the sender jumps its timeline: those sequence numbers are never sent, and a request for them is
        // no packet the network lost. Counted as one, it made 首页 blame the Wi-Fi for a stall on this PC.
        using var rx = new FakeReceiver();
        using var sender = new RtpSender([rx.Target(firstSeq: 1000)], 4630, Primed()) { Effects = new StallOnce(5, 250) };
        sender.Start(MediaClock.Now + MediaClock.FromMs(10));
        await Until(() => sender.SkippedPackets > 0 && rx.Audio.Count >= 8, "a stall and packets after it");

        // The last gap: should this machine stall again by itself, the sender remembers only its last skip.
        var seqs = rx.Audio.Select(p => (int)FakeReceiver.Seq(p.Bytes)).Order().ToArray();
        int gap = Enumerable.Range(1, seqs.Length - 1).Last(i => seqs[i] != seqs[i - 1] + 1);
        ushort firstSkipped = (ushort)(seqs[gap - 1] + 1);
        int skipped = seqs[gap] - firstSkipped;
        Assert.True(skipped > 0);

        rx.RequestResend(firstSkipped, (ushort)Math.Min(skipped, 3)); // never sent: the stall skipped them
        rx.RequestResend(5000, 1);                                      // never sent either, and no stall explains it
        await Until(() => sender.SkippedResendRequests + sender.RetransmitMisses >= Math.Min(skipped, 3) + 1,
            "both requests to be counted");

        Assert.Equal(Math.Min(skipped, 3), sender.SkippedResendRequests);
        Assert.Equal(1, sender.RetransmitMisses);
        Assert.Equal(0, sender.Retransmitted);
        Assert.Empty(rx.Resent);
    }
}
