using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class TimelinePumpTests
{
    private sealed class Gain : IAudioEffect
    {
        public int Calls;
        public void Process(Span<float> samples, int frames)
        {
            Calls++;
            for (int i = 0; i < samples.Length; i++) samples[i] *= 0.5f;
        }
    }

    [Fact]
    public void Non_integral_QPC_anchor_never_overlaps_a_packet_and_skips_whole_frames()
    {
        var fifo = new AudioFifo(44100, 0, 1000);
        using var pump = new TimelinePump(fifo, null, 5000);
        long t0 = pump.Timeline.OriginTicks + (long)Math.Round(2_500_114 * (double)MediaClock.Frequency / 10_000_000);
        long first = pump.Timeline.FrameAt(t0);
        var pcm = new float[704];
        for (int packet = 0; packet < 1000; packet++)
        {
            Array.Fill(pcm, (float)packet);
            fifo.Write(pcm);
            long deadline = t0 + (long)(packet * 352.0 * MediaClock.Frequency / 44100);
            pump.Produce(deadline);
            Assert.True(pump.Timeline.ReadFrames(first + packet * 352L, pcm));
            Assert.All(pcm, value => Assert.Equal(packet, value));
        }
        pump.SkipPackets(3);
        Array.Fill(pcm, 0.25f); fifo.Write(pcm);
        pump.Produce(t0 + (long)(1003 * 352.0 * MediaClock.Frequency / 44100));
        Assert.False(pump.Timeline.ReadFrames(first + 1000 * 352L, pcm));
        Assert.All(pcm, value => Assert.Equal(0, value));
        Assert.True(pump.Timeline.ReadFrames(first + 1003 * 352L, pcm));
        Assert.All(pcm, value => Assert.Equal(0.25f, value));
    }

    [Fact]
    public void AirPlay_and_local_output_read_one_processed_source_at_the_same_presentation_time()
    {
        var fifo = new AudioFifo(44100, 0, 1000);
        var gain = new Gain();
        using var pump = new TimelinePump(fifo, gain, 5000);
        long t0 = pump.Timeline.TicksAt(1024);
        var input = new float[352 * 3 * 2];
        input[(352 + 200) * 2] = 1;
        input[(352 + 200) * 2 + 1] = -0.5f;
        fifo.Write(input);
        for (int packet = 0; packet < 3; packet++) pump.Produce(t0 + (long)(packet * 352.0 * MediaClock.Frequency / 44100));
        var source = new TimelineGroupSource(pump.Timeline, [ChannelMode.Stereo, ChannelMode.LeftOnly], 10584, 260, () => null);
        long sourceTime = t0 + (long)(352.0 * MediaClock.Frequency / 44100);
        Assert.True(source.ReadPacket(sourceTime + MediaClock.FromMs(20)));
        var airplay = new float[704];
        source.ReadTarget(0, airplay);
        Assert.Equal(0.5f, airplay[200 * 2]);
        Assert.Equal(-0.25f, airplay[200 * 2 + 1]);
        var local = new LocalOutputRenderer(pump.Timeline, new("fake", "Bluetooth") { ExtraLatencyMs = 200 }, 44100, 352, 260, () => 100);
        local.SetVolume(100);
        var output = new float[704];
        Assert.True(local.Render(0, new(0, sourceTime + MediaClock.FromMs(60)), output));
        int peak = Enumerable.Range(0, 352).MaxBy(i => Math.Abs(output[i * 2]));
        Assert.InRange(peak, 199, 201);
        Assert.Equal(-output[peak * 2] / 2, output[peak * 2 + 1]);
        Assert.Equal(3, gain.Calls);
        var left = new float[704]; source.ReadTarget(1, left);
        Assert.Equal(0.5f, left[400]); Assert.Equal(0.5f, left[401]);
        Assert.Equal(3, gain.Calls); // independent outputs never run the effects again
    }

    [Fact]
    public async Task History_retains_large_positive_trim_after_the_live_writer_moves_forward()
    {
        var options = new LocalOutputOptions("fake", "Local") { TrimMs = 5000 };
        using var outputs = await LocalOutputSet.PrepareAsync([options], () => "capture", () => 100, default, _ => new FakeLocalRenderDevice());
        using var pump = new TimelinePump(new AudioFifo(44100, 0, 1000), null, outputs.HistoryMs(3000));
        pump.Timeline.WriteFrames(0, Enumerable.Repeat(0.25f, 1500 * 2).ToArray());
        pump.Timeline.WriteFrames(8 * 44100, new float[704]);
        var renderer = new LocalOutputRenderer(pump.Timeline, options, 48000, 480, 3000, () => 100);
        renderer.SetVolume(100);
        var pcm = new float[960];
        Assert.True(renderer.Render(8 * 48000 + 960, new(8 * 48000, pump.Timeline.OriginTicks + MediaClock.FromMs(8000)), pcm));
        Assert.All(pcm, value => Assert.InRange(value, 0.2499f, 0.2501f));
    }

    [Fact]
    public void Half_frame_transport_offset_uses_one_integer_cursor_including_skips()
    {
        var timeline = new TimelineBuffer(44100, 200000, 0);
        timeline.WriteFrames(-500, Enumerable.Range(-500, 160000).SelectMany(frame => new[] { (float)frame, -(float)frame }).ToArray());
        using var source = new TimelineGroupSource(timeline, [ChannelMode.Stereo], 88200, 2025, () => null);
        var pcm = new float[704];
        long first = timeline.FrameAt(MediaClock.FromMs(-5));
        for (int packet = 0; packet < 443; packet++)
        {
            Assert.True(source.ReadPacket(MediaClock.FromMs(20) + (long)(packet * 352.0 * MediaClock.Frequency / 44100)));
            source.ReadTarget(0, pcm);
            Assert.Equal(first + packet * 352, pcm[0]);
            Assert.Equal(-pcm[0], pcm[1]);
        }
        source.SkipPackets(2);
        source.ReadPacket(MediaClock.FromMs(20) + (long)(445 * 352.0 * MediaClock.Frequency / 44100));
        source.ReadTarget(0, pcm);
        Assert.Equal(first + 445 * 352, pcm[0]);
    }

    [Fact]
    public void Per_speaker_positive_delay_reads_older_audio_in_both_group_sources()
    {
        foreach (bool mixed in new[] { false, true })
        {
            var fifo = new AudioFifo(44100, 0, 1000);
            var signal = TestAudio.Signal(352 * 6);
            fifo.Write(signal);
            var actual = new float[704];
            if (mixed)
            {
                using var pump = new TimelinePump(fifo, null, 5000, 0);
                for (int packet = 0; packet < 5; packet++) pump.Produce((long)(packet * 352.0 * MediaClock.Frequency / 44100));
                using var source = new TimelineGroupSource(pump.Timeline, [ChannelMode.Stereo, ChannelMode.Stereo], 10584, 260, () => null, [0, 10]);
                for (int packet = 0; packet < 4; packet++) source.ReadPacket(MediaClock.FromMs(20) + (long)(packet * 352.0 * MediaClock.Frequency / 44100));
                source.ReadTarget(0, actual);
                Assert.Equal(signal[(1056 * 2)..((1056 + 352) * 2)], actual);
                source.ReadTarget(1, actual);
            }
            else
            {
                using var source = new GroupTimelineSource(fifo, [ChannelMode.Stereo, ChannelMode.Stereo], null, () => null, [0, 10]);
                long t0 = source.Timeline.TicksAt(1024);
                for (int packet = 0; packet < 4; packet++) source.ReadPacket(t0 + (long)(packet * 352.0 * MediaClock.Frequency / 44100));
                source.ReadTarget(0, actual);
                Assert.Equal(signal[(1056 * 2)..((1056 + 352) * 2)], actual);
                source.ReadTarget(1, actual);
            }
            Assert.Equal(signal[((1056 - 441) * 2)..((1056 - 441 + 352) * 2)], actual);
        }
    }
}
