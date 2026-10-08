using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>RtpSender against in-process fake receivers: one timeline, per-session keys and sequence numbers.</summary>
public class FanOutTests
{
    private const int PacketBytes = 1444;
    private static readonly double PacketMs = 352 * 1000.0 / 44100;

    private static (AudioFifo Fifo, float[] Signal) Primed()
    {
        var fifo = new AudioFifo(44100, targetMs: 20, capMs: 1000);
        var signal = TestAudio.Signal(26460); // 600 ms, more than any test here plays
        fifo.Write(signal);
        return (fifo, signal);
    }

    private static void Play(RtpSender sender, int ms)
    {
        using (sender)
        {
            sender.Start(MediaClock.Now + MediaClock.FromMs(10));
            Thread.Sleep(ms);
        }
        Thread.Sleep(30); // let the receivers drain
    }

    [Fact]
    public void Single_speaker_wire_bytes_match_the_sender_before_groups_existed()
    {
        // SHA-256 of the first 20 packets as sent by the pre-group RtpSender for exactly these inputs
        // (verified side by side with that code). Guards the daily-use single-speaker path.
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var rx = new FakeReceiver(key, 0x12345678);
        var (fifo, _) = Primed();
        Play(new RtpSender([rx.Target(firstSeq: 65530)], 4630, fifo, 0xFFFFF000u), 250);

        var first20 = rx.Audio.Take(20).SelectMany(p => p.Bytes).ToArray();
        Assert.Equal(20 * PacketBytes, first20.Length);
        Assert.Equal("780E8804F59383C4BD2E0BA2FBB060290665B86E0C714DAE9B3A9F9AD4FE30BE",
            Convert.ToHexString(SHA256.HashData(first20)));
    }

    [Fact]
    public void Single_speaker_constructor_is_one_full_stereo_stream()
    {
        using var rx = new FakeReceiver();
        using var sender = new RtpSender(rx.SenderControl, System.Net.IPAddress.Loopback, rx.DataPort, rx.ControlPort,
            rx.Key, rx.Ssrc, 4630, new AudioFifo(44100, 20, 60));
        var s = Assert.Single(sender.Streams);
        Assert.Equal(ChannelMode.Stereo, s.Channels);
        Assert.Equal(s.FirstSeq, sender.FirstSeq);
        Assert.Equal(rx.Ssrc, s.Ssrc);
    }

    [Fact]
    public void Two_speakers_share_rtp_time_and_cadence_but_not_keys_or_sequence_numbers()
    {
        using var a = new FakeReceiver();
        using var b = new FakeReceiver();
        var (fifo, signal) = Primed();
        var sender = new RtpSender([a.Target(firstSeq: 100), b.Target(firstSeq: 65500)], 4630, fifo);
        Play(sender, 250);

        var pa = a.Audio.ToArray();
        var pb = b.Audio.ToArray();
        int n = Math.Min(pa.Length, pb.Length);
        Assert.InRange(n, 20, 45);
        Assert.InRange(pa.Length - pb.Length, -1, 1);
        for (int k = 0; k < n; k++)
        {
            byte[] x = pa[k].Bytes, y = pb[k].Bytes;
            Assert.Equal(PacketBytes, x.Length);

            // one timeline: identical RTP time, 352 frames per packet from the shared base
            Assert.Equal(unchecked(sender.RtpBase + (uint)(k * 352)), FakeReceiver.Rtp(x));
            Assert.Equal(FakeReceiver.Rtp(x), FakeReceiver.Rtp(y));

            // per session: own sequence numbers (B wraps 65535 -> 0), SSRC, cipher counter and key
            Assert.Equal((ushort)(100 + k), FakeReceiver.Seq(x));
            Assert.Equal((ushort)(65500 + k), FakeReceiver.Seq(y));
            Assert.Equal(a.Ssrc, FakeReceiver.SsrcOf(x));
            Assert.Equal(b.Ssrc, FakeReceiver.SsrcOf(y));
            Assert.Equal((ulong)k, FakeReceiver.Counter(x));
            Assert.Equal((ulong)k, FakeReceiver.Counter(y));
            Assert.Equal(k == 0 ? 0xE0 : 0x60, x[1]);
            Assert.Equal(k == 0 ? 0xE0 : 0x60, y[1]);

            var plainA = a.Decrypt(x);
            Assert.Equal(TestAudio.ExpectedPayload(signal, k, ChannelMode.Stereo), plainA);
            Assert.Equal(plainA, b.Decrypt(y));
            Assert.NotEqual(x[12..1420], y[12..1420]);
            Assert.ThrowsAny<CryptographicException>(() => FakeReceiver.Decrypt(x, b.Key));
        }

        // Cadence: packets are 7.98 ms apart, and each one reaches both speakers back-to-back.
        double span = MediaClock.ToMs(pa[n - 1].Qpc - pa[0].Qpc);
        Assert.InRange(span, (n - 1) * PacketMs - 15, (n - 1) * PacketMs + 15);
        var gaps = Enumerable.Range(0, n).Select(k => Math.Abs(MediaClock.ToMs(pb[k].Qpc - pa[k].Qpc))).Order().ToList();
        Assert.True(gaps[n / 2] < 3.0, $"median A/B gap {gaps[n / 2]:F2} ms");
        Assert.Equal(sender.PacketsSent, sender.Streams[0].PacketsSent);
        Assert.Equal(sender.PacketsSent, sender.Streams[1].PacketsSent);
    }

    [Fact]
    public void Every_speaker_gets_the_same_sync_packets_with_both_rtp_fields_equal()
    {
        using var a = new FakeReceiver();
        using var b = new FakeReceiver();
        var (fifo, _) = Primed();
        var sender = new RtpSender([a.Target(), b.Target(ChannelMode.LeftOnly)], 4630, fifo);
        Play(sender, 1150); // the first sync goes out at start, the next one a second later

        var sa = a.Sync.ToArray();
        var sb = b.Sync.ToArray();
        Assert.Equal(2, sa.Length);
        Assert.Equal(2, sb.Length);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(sa[i].Bytes, sb[i].Bytes); // same RTP <-> NTP mapping for both sessions
            var p = sa[i].Bytes;
            Assert.Equal(20, p.Length);
            Assert.Equal(i == 0 ? 0x90 : 0x80, p[0]);
            Assert.Equal(0xD4, p[1]);
            Assert.Equal(p[4..8], p[16..20]); // no latency subtracted: the HomePod adds the SETUP latency itself
        }
        // The first sync is sent ~10 ms before packet 0, the second ~1 s later on the same line.
        uint rtp0 = FakeReceiver.Rtp(sa[0].Bytes), rtp1 = FakeReceiver.Rtp(sa[1].Bytes);
        Assert.InRange((int)unchecked(rtp0 - sender.RtpBase), -1500, 0);
        Assert.InRange((int)unchecked(rtp1 - rtp0), 44100 - 1500, 44100 + 1500);
    }

    [Fact]
    public void Split_channels_send_left_to_one_speaker_and_right_to_the_other()
    {
        using var left = new FakeReceiver();
        using var right = new FakeReceiver();
        var (fifo, signal) = Primed();
        Play(new RtpSender([left.Target(ChannelMode.LeftOnly), right.Target(ChannelMode.RightOnly)], 4630, fifo), 150);

        var pl = left.Audio.ToArray();
        var pr = right.Audio.ToArray();
        Assert.True(pl.Length >= 10 && pr.Length >= 10);
        for (int k = 0; k < Math.Min(pl.Length, pr.Length); k++)
        {
            Assert.Equal(TestAudio.ExpectedPayload(signal, k, ChannelMode.LeftOnly), left.Decrypt(pl[k].Bytes));
            Assert.Equal(TestAudio.ExpectedPayload(signal, k, ChannelMode.RightOnly), right.Decrypt(pr[k].Bytes));
        }
    }

    [Fact]
    public void Resend_requests_are_answered_per_session_from_that_sessions_own_packets()
    {
        using var a = new FakeReceiver();
        using var b = new FakeReceiver();
        var (fifo, _) = Primed();
        var sender = new RtpSender([a.Target(firstSeq: 1000), b.Target(firstSeq: 2000)], 4630, fifo);
        using (sender)
        {
            sender.Start(MediaClock.Now + MediaClock.FromMs(10));
            Thread.Sleep(120);
            a.RequestResend(1002, 2);
            a.RequestResend(5000, 1); // never sent by A
            Thread.Sleep(80);
            Assert.Equal(2, sender.Retransmitted);
            Assert.Equal(1, sender.RetransmitMisses);
            Assert.Equal(2, sender.Streams[0].Retransmitted);
            Assert.Equal(0, sender.Streams[1].RetransmitRequests);
        }
        Thread.Sleep(30);

        var resent = a.Resent.ToArray();
        Assert.Equal(2, resent.Length);
        Assert.Empty(b.Resent);
        var original = a.Audio.ToArray();
        for (int i = 0; i < 2; i++)
        {
            var r = resent[i].Bytes;
            Assert.Equal(0xD6, r[1]);
            Assert.Equal((ushort)(1002 + i), FakeReceiver.Seq(r));
            Assert.Equal(original.Single(p => FakeReceiver.Seq(p.Bytes) == 1002 + i).Bytes, r[4..]);
        }
    }

    [Fact]
    public void One_channel_encoding_matches_stereo_encoding_of_that_channel_doubled()
    {
        var src = new float[] { 0.25f, -0.5f, 1.5f, -2f, 0f, 0.999f, -1f, 1f };
        var leftDoubled = new float[src.Length];
        var rightDoubled = new float[src.Length];
        for (int i = 0; i < src.Length / 2; i++)
        {
            leftDoubled[i * 2] = leftDoubled[i * 2 + 1] = src[i * 2];
            rightDoubled[i * 2] = rightDoubled[i * 2 + 1] = src[i * 2 + 1];
        }
        byte[] Stereo(float[] x) { var d = new byte[x.Length * 2]; RtpSender.ToS16BigEndian(x, d); return d; }
        byte[] One(int ch) { var d = new byte[src.Length * 2]; RtpSender.ToS16BigEndianOneChannel(src, d, ch); return d; }

        Assert.Equal(Stereo(leftDoubled), One(0));
        Assert.Equal(Stereo(rightDoubled), One(1));
        Assert.Equal([0x7F, 0xFF, 0x7F, 0xFF], One(0)[4..8]); // +1.5 clips to +32767
        Assert.Equal([0x80, 0x00, 0x80, 0x00], One(1)[4..8]); // -2 clips to -32768
    }
}
