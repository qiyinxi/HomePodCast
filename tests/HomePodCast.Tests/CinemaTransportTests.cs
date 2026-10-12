using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

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
    [Fact]
    public void File_pause_and_calibration_preserve_position_and_isolate_the_test_speaker()
    {
        string path = Path.Combine(Path.GetTempPath(), "hpc-cinema-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            CinemaTests.WriteWave(path, 32);
            var layout = Enumerable.Range(0, 6).Select(i => new CinemaSpeaker { Role = (CinemaRole)i, DeviceId = $"device{i}" }).ToArray();
            using var source = new CinemaFileSource(path, layout);
            Assert.True(source.WaitOpened(TimeSpan.FromSeconds(5)), source.Error);
            Assert.True(SpinWait.SpinUntil(() => source.BufferedFrames > 352, 5000));
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
            source.ApplyDelays([0,0,0,0,20,20]);
            source.ReadPacket(MediaClock.Now); source.ReadTarget(4, pcm);
            Assert.All(pcm, v => Assert.Equal(0f, v)); // new delay history starts silent, not with stale calibration/content
        }
        finally { File.Delete(path); }
    }
}
