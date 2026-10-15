using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class GroupTimelineSourceTests
{
    private sealed class Gain : IAudioEffect
    {
        public int Calls;
        public void Process(Span<float> interleaved, int frames)
        {
            Calls++;
            for (int i = 0; i < interleaved.Length; i++) interleaved[i] *= 0.5f;
        }
    }

    [Fact]
    public void Effects_run_once_and_every_reader_gets_the_processed_packet()
    {
        var fifo = new AudioFifo(44100, 0, 1000);
        var signal = Enumerable.Range(0, 704).Select(i => i / 1000f).ToArray();
        fifo.Write(signal);
        var gain = new Gain();
        using var source = new GroupTimelineSource(fifo, [ChannelMode.Stereo, ChannelMode.LeftOnly, ChannelMode.RightOnly], gain, () => null);
        long start = source.Timeline.OriginTicks;
        Assert.True(source.ReadPacket(start));
        var output = new float[704];
        for (int target = 0; target < 3; target++)
        {
            source.ReadTarget(target, output);
            for (int i = 0; i < 352; i++)
            {
                Assert.Equal(signal[i * 2 + (target == 2 ? 1 : 0)] * 0.5f, output[i * 2]);
                Assert.Equal(signal[i * 2 + (target == 1 ? 0 : 1)] * 0.5f, output[i * 2 + 1]);
            }
        }
        Assert.Equal(1, gain.Calls);
        Assert.Equal(0, fifo.Depth);
        Assert.True(source.Timeline.ReadAt(start, 0, 0, output));
        Assert.Equal(signal.Select(v => v * 0.5f), output);
    }

    [Fact]
    public void Stalls_leave_a_timestamped_gap_and_underflow_keeps_the_existing_effects_behavior()
    {
        var fifo = new AudioFifo(44100, 0, 1000);
        var gain = new Gain();
        using var source = new GroupTimelineSource(fifo, [ChannelMode.Stereo], gain, () => null);
        long start = source.Timeline.OriginTicks;
        fifo.Write(Enumerable.Repeat(0.5f, 704).ToArray());
        Assert.True(source.ReadPacket(start));
        source.SkipPackets(2);
        Assert.False(source.ReadPacket(source.Timeline.TicksAt(3 * 352)));
        var pcm = Enumerable.Repeat(99f, 704).ToArray();
        Assert.False(source.Timeline.ReadFrames(352, pcm));
        Assert.All(pcm, v => Assert.Equal(0, v));
        Assert.True(source.Timeline.ReadFrames(3 * 352, pcm)); // silence itself is part of the presentation timeline
        Assert.All(pcm, v => Assert.Equal(0, v));
        Assert.Equal(2, gain.Calls);
    }

    [Fact]
    public void Onset_probe_keeps_the_group_deadline_and_intra_packet_sample_offset()
    {
        var fifo = new AudioFifo(44100, 0, 1000);
        var onsets = new List<long>();
        using var source = new GroupTimelineSource(fifo, [ChannelMode.Stereo], null, () => onsets.Add);
        for (int packet = 0; packet < 39; packet++)
            source.ReadPacket(source.Timeline.TicksAt(packet * 352));
        var signal = new float[704];
        signal[10 * 2] = 0.25f;
        fifo.Write(signal);
        long ticks = source.Timeline.TicksAt(39 * 352);
        Assert.True(source.ReadPacket(ticks));
        Assert.Equal(ticks + (long)(10 * (double)MediaClock.Frequency / 44100), Assert.Single(onsets));
    }

    [Fact]
    public void Group_adapter_does_not_allocate_per_packet()
    {
        var fifo = new AudioFifo(44100, 0, 1000);
        using var source = new GroupTimelineSource(fifo, [ChannelMode.Stereo, ChannelMode.LeftOnly], new Gain(), () => null);
        var pcm = new float[704];
        long frame = 0;
        Assert.Equal(0, Signals.Allocated(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                fifo.Write(pcm);
                source.ReadPacket(source.Timeline.TicksAt(frame));
                source.ReadTarget(0, pcm);
                source.ReadTarget(1, pcm);
                frame += 352;
            }
        }));
    }
}
