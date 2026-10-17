using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class ReceiverTimelineSourceTests
{
    [Theory]
    [InlineData(352)]
    [InlineData(1024)]
    public void Thousand_packets_keep_contiguous_integer_source_frames(int frames)
    {
        long origin = MediaClock.Now;
        var history = new TimelineBuffer(44100, 8192, origin);
        var source = new ReceiverTimelineSource(history, 2250, 0, 1000, ChannelMode.Stereo);
        float[] write = new float[frames * 2], read = new float[frames * 2];
        long start = origin + MediaClock.FromMs(1250);
        for (int packet = 0; packet < 1000; packet++)
        {
            long frame = (long)packet * frames;
            for (int i = 0; i < frames; i++) { write[i * 2] = (frame + i) % 16384; write[i * 2 + 1] = -write[i * 2]; }
            history.WriteFrames(frame, write);
            Assert.True(source.Read(start + (long)((Int128)frame * MediaClock.Frequency / 44100), read));
            Assert.Equal(write, read);
        }
    }

    [Fact]
    public void Realtime_and_AAC_read_the_marker_for_the_same_absolute_presentation_instant()
    {
        long origin = MediaClock.Now;
        var history = new TimelineBuffer(44100, 16384, origin);
        var samples = new float[10000 * 2]; samples[8192 * 2] = 1;
        history.WriteFrames(0, samples);
        long expected = history.TicksAt(8192) + MediaClock.FromMs(2750);
        foreach (var (packetFrames, latency) in new[] { (352, 250), (352, 400), (1024, 1500) })
        {
            var source = new ReceiverTimelineSource(history, 2750, 0, latency, ChannelMode.Stereo);
            float[] block = new float[packetFrames * 2];
            long first = origin + MediaClock.FromMs(2750 - latency);
            bool found = false;
            for (int packet = 0; !found && packet < 32; packet++)
            {
                long send = first + (long)((Int128)packet * packetFrames * MediaClock.Frequency / 44100);
                source.Read(send, block);
                for (int i = 0; i < packetFrames; i++)
                    if (block[i * 2] == 1)
                    {
                        long presented = send + MediaClock.FromMs(latency)
                            + (long)((Int128)i * MediaClock.Frequency / 44100);
                        Assert.InRange(Math.Abs(presented - expected), 0, 2);
                        found = true; break;
                    }
            }
            Assert.True(found);
        }
    }

    [Theory]
    [InlineData(ChannelMode.LeftOnly, 0.25f)]
    [InlineData(ChannelMode.RightOnly, -0.5f)]
    public void Channel_maps_and_skips_are_independent_of_other_readers(ChannelMode channels, float expected)
    {
        long origin = MediaClock.Now;
        var history = new TimelineBuffer(44100, 4096, origin);
        var samples = new float[2048 * 2];
        for (int i = 0; i < 2048; i++) { samples[i * 2] = .25f; samples[i * 2 + 1] = -.5f; }
        history.WriteFrames(0, samples);
        var reader = new ReceiverTimelineSource(history, 1000, 0, 1000, channels);
        var other = new ReceiverTimelineSource(history, 1000, 0, 1000, ChannelMode.Stereo);
        float[] block = new float[704];
        reader.ReadPacket(origin); reader.ReadTarget(0, block);
        Assert.All(block, value => Assert.Equal(expected, value));
        reader.SkipPackets(20);
        Assert.False(reader.Read(origin, block)); Assert.All(block, value => Assert.Equal(0, value));
        Assert.True(other.Read(origin, block));
        Assert.Equal(.25f, block[0]); Assert.Equal(-.5f, block[1]);
    }

    [Fact]
    public void Read_does_not_allocate_or_forward_nonfinite_samples()
    {
        long origin = MediaClock.Now;
        var history = new TimelineBuffer(44100, 4096, origin);
        float[] samples = new float[4096 * 2]; samples[0] = float.NaN; samples[1] = float.PositiveInfinity;
        history.WriteFrames(0, samples);
        var reader = new ReceiverTimelineSource(history, 1000, 0, 1000, ChannelMode.Stereo);
        float[] block = new float[2048];
        reader.Read(origin, block);
        Assert.All(block, value => Assert.Equal(0, value));
        // Signals.Allocated settles the runtime's allocation context before measurement; a background
        // GC must not count its discarded spare bytes as allocations by this realtime reader.
        long bytes = Signals.Allocated(() =>
        {
            for (int i = 0; i < 1000; i++) reader.Read(origin, block);
        });
        Assert.Equal(0, bytes);
    }
}
