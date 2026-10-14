using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// The cinema's file source on the sender's side: one RTP timeline for every speaker, the lock-free ring between the
/// decoder and the sender, pause and calibration. Generated files, loopback receivers; nothing plays.
/// </summary>
public class CinemaTransportTests
{
    [Fact]
    public void Six_distinct_payloads_share_one_encrypted_RTP_timeline()
    {
        var receivers = Enumerable.Range(0, 6).Select(_ => new FakeReceiver()).ToArray();
        try
        {
            using var source = new ConstantSource();
            using var sender = new RtpSender(receivers.Select((r, i) => r.Target(firstSeq: (ushort)(100 + i))).ToArray(),
                8820, new AudioFifo(44100, 16, 60)) { GroupSource = source };
            sender.Start(MediaClock.Now + MediaClock.FromMs(20)); Thread.Sleep(220); sender.Dispose(); Thread.Sleep(30);
            var packets = receivers.Select(r => r.Audio.ToArray()).ToArray();
            Assert.All(packets, p => Assert.True(p.Length >= 10));
            for (int k = 0; k < 10; k++)
                for (int i = 0; i < receivers.Length; i++)
                {
                    Assert.Equal(FakeReceiver.Rtp(packets[0][k].Bytes), FakeReceiver.Rtp(packets[i][k].Bytes));
                    byte[] expected = new byte[352 * 4];
                    RtpSender.ToS16BigEndian(Enumerable.Repeat((i + 1) * 0.02f, 704).ToArray(), expected);
                    Assert.Equal(expected, receivers[i].Decrypt(packets[i][k].Bytes));
                }
        }
        finally { foreach (var receiver in receivers) receiver.Dispose(); }
    }

    private sealed class ConstantSource : IGroupPacketSource
    {
        public bool ReadPacket(long ticks) => true;
        public void ReadTarget(int index, Span<float> stereo) => stereo.Fill((index + 1) * 0.02f);
        public void SkipPackets(int packets) { }
        public void Dispose() { }
    }

    private static CinemaFileSource Open(string path, List<CinemaSpeaker> layout)
    {
        var source = new CinemaFileSource(path, layout);
        Assert.True(source.WaitOpened(TimeSpan.FromSeconds(10)), source.ErrorLog);
        return source;
    }

    private static void WaitForPacket(CinemaFileSource source) =>
        Assert.True(SpinWait.SpinUntil(() => source.BufferedFrames >= RtpSender.FramesPerPacket, 5000), "the decoder did not fill the ring");

    [Fact]
    public void A_71_file_plays_every_channel_on_seven_HomePods_with_the_LFE_in_the_fronts()
    {
        string path = CinemaTests.TempWave(32, 8, CinemaTests.Surround71, 48000);
        try
        {
            using var source = Open(path, CinemaTests.Layout(CinemaTests.L7));
            Assert.Equal(8, source.Channels);
            Assert.Equal(CinemaTests.Surround71, source.Mask);
            WaitForPacket(source);
            source.ReadPacket(MediaClock.Now);
            // Levels FL .02 FR .04 C .06 LFE .08 BL .10 BR .12 SL .14 SR .16; the fronts carry the LFE at −6 dB, so the
            // busiest speaker sums 1.5 and everything is scaled by 1/1.5.
            float scale = 1 / 1.5f;
            float[] expected = [(0.02f + 0.04f) * scale, (0.04f + 0.04f) * scale, 0.06f * scale, 0.14f * scale, 0.16f * scale,
                0.10f * scale, 0.12f * scale];
            var pcm = new float[704];
            for (int speaker = 0; speaker < expected.Length; speaker++)
            {
                source.ReadTarget(speaker, pcm);
                Assert.InRange(pcm[200], expected[speaker] - 0.0005f, expected[speaker] + 0.0005f);
                Assert.Equal(pcm[200], pcm[201]); // the same on both channels of the stereo packet
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_sender_gets_every_frame_in_order_while_the_decoder_fills_the_ring()
    {
        // 44.1 kHz: no resampler, so the frames arrive exactly as written. Each speaker plays its own channel only.
        const int frames = 44100 * 2;
        static float Expected(int frame, int channel) => (frame % 4410) / 4410f * 0.5f + channel * 0.01f;
        string path = CinemaTests.TempWave(32, 8, CinemaTests.Surround71, 44100, frames, Expected);
        try
        {
            using var source = Open(path, CinemaTests.Layout(CinemaTests.L8));
            var pcm = new float[704];
            int[] channelOf = [0, 1, 2, 3, 4, 5, 6, 7]; // L8 is in mask order
            for (int packet = 0; packet < 200; packet++)
            {
                WaitForPacket(source);
                Assert.True(source.ReadPacket(MediaClock.Now));
                for (int speaker = 0; speaker < 8; speaker++)
                {
                    source.ReadTarget(speaker, pcm);
                    for (int f = 0; f < 352; f += 50)
                        Assert.Equal(Expected(packet * 352 + f, channelOf[speaker]), (double)pcm[f * 2], 5);
                }
            }
            Assert.Equal(200 * 352 / 44100.0, source.PositionSeconds, 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_pause_and_calibration_preserve_position_and_isolate_the_test_speaker()
    {
        string path = CinemaTests.TempWave(32, 6, CinemaTests.Rear51, 48000);
        try
        {
            using var source = Open(path, CinemaTests.Layout("FL FR C Sub BL BR"));
            WaitForPacket(source);
            var pcm = new float[704]; source.ReadPacket(MediaClock.Now); source.ReadTarget(2, pcm);
            Assert.InRange(pcm[100], 0.059f, 0.061f);
            double position = source.PositionSeconds;
            source.Paused = true; source.ReadPacket(MediaClock.Now); source.ReadTarget(2, pcm);
            Assert.Equal(position, source.PositionSeconds); Assert.All(pcm, v => Assert.Equal(0f, v));
            source.BeginPulse(4); long pulse = MediaClock.Now; source.ReadPacket(pulse);
            source.ReadTarget(2, pcm); Assert.All(pcm, v => Assert.Equal(0f, v));
            source.ReadTarget(4, pcm); Assert.Contains(pcm, v => Math.Abs(v) > 0.0001f);
            Assert.Equal(pulse, source.PulseStartedTicks); Assert.Equal(position, source.PositionSeconds);
            source.EndCalibration(); source.Paused = false;
            source.ApplyDelays([0, 0, 0, 0, 20, 20]);
            source.ReadPacket(MediaClock.Now); source.ReadTarget(4, pcm);
            Assert.All(pcm, v => Assert.Equal(0f, v)); // new delay history starts silent, not with stale calibration/content
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ending_the_calibration_at_any_moment_lets_the_next_packet_play_the_file()
    {
        // Before 2026-10-14 the sender took the pending pulse and then set the calibration: an end in between was lost,
        // and the file stayed paused and silent for good (review of #22).
        string path = CinemaTests.TempWave(16, 2, CinemaTests.Stereo, 44100, frames: 44100 * 20);
        try
        {
            using var source = Open(path, CinemaTests.Layout(CinemaTests.L2));
            var pcm = new float[704];

            // Ended before the sender saw the pulse, and right after it did.
            source.BeginPulse(1);
            source.EndCalibration();
            WaitForPacket(source);
            double before = source.PositionSeconds;
            source.ReadPacket(MediaClock.Now);
            Assert.False(source.Calibrating);
            Assert.True(source.PositionSeconds > before);
            source.BeginPulse(1);
            source.ReadPacket(MediaClock.Now);
            Assert.True(source.Calibrating);
            source.EndCalibration();
            WaitForPacket(source);
            before = source.PositionSeconds;
            source.ReadPacket(MediaClock.Now);
            source.ReadTarget(0, pcm);
            Assert.True(source.PositionSeconds > before);
            Assert.InRange(pcm[100], 0.019f, 0.021f);

            // The sender and the calibrating thread at full speed: once the last end is in, the file plays again.
            using var stop = new CancellationTokenSource();
            var sender = Task.Run(() =>
            {
                var buffer = new float[704];
                for (int i = 0; i < 2000 && !stop.IsCancellationRequested; i++)
                {
                    source.ReadPacket(MediaClock.Now);
                    source.ReadTarget(0, buffer);
                    source.ReadTarget(1, buffer);
                }
            });
            var random = new Random(22);
            for (int i = 0; i < 300; i++)
            {
                source.BeginPulse(i % 2);
                Thread.SpinWait(random.Next(50, 5000));
                source.EndCalibration();
                Thread.SpinWait(random.Next(50, 5000));
            }
            stop.Cancel();
            Assert.True(sender.Wait(10000));
            Assert.False(source.Calibrating);
            WaitForPacket(source);
            before = source.PositionSeconds;
            source.ReadPacket(MediaClock.Now);
            source.ReadTarget(0, pcm);
            Assert.True(source.PositionSeconds > before, "the file stayed paused after the calibration ended");
            Assert.InRange(pcm[100], 0.019f, 0.021f);
        }
        finally { File.Delete(path); }
    }
}
