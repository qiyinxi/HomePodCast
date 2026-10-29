using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>The 6-channel FIFO of the 5.1 surround path (experimental since 1.4): everything counts frames, as in stereo.</summary>
public sealed class AudioFifoChannelTests
{
    private const int Ch = 6;

    private static float[] Frames(int first, int count)
    {
        var data = new float[count * Ch];
        for (int f = 0; f < count; f++)
            for (int c = 0; c < Ch; c++) data[f * Ch + c] = (first + f) + c / 10f;
        return data;
    }

    [Fact]
    public void Six_channels_round_trip_in_order()
    {
        var fifo = new AudioFifo(48000, targetMs: 0, capMs: 250, channels: Ch);
        Assert.Equal(Ch, fifo.Channels);
        Assert.Equal(48000, fifo.SampleRate);
        fifo.Write(Frames(0, 700));
        Assert.Equal(700, fifo.Depth);
        var out1 = new float[512 * Ch];
        Assert.True(fifo.Read(out1));
        Assert.Equal(Frames(0, 512), out1);
        fifo.Write(Frames(700, 1000));
        var out2 = new float[1024 * Ch];
        Assert.True(fifo.Read(out2));
        Assert.Equal(Frames(512, 1024), out2);
        Assert.Equal(164, fifo.Depth);
    }

    [Fact]
    public void Priming_and_padding_count_frames()
    {
        var fifo = new AudioFifo(48000, targetMs: 40, capMs: 250, channels: Ch); // target 1920 frames
        Assert.Equal(1920, fifo.TargetFrames);
        fifo.Write(Frames(0, 1900));
        var block = new float[1024 * Ch];
        Assert.False(fifo.Read(block)); // still priming: silence, nothing taken
        Assert.All(block, v => Assert.Equal(0f, v));
        Assert.Equal(1900, fifo.Depth);

        fifo.Write(Frames(1900, 100));
        Assert.True(fifo.Read(block));
        Assert.Equal(Frames(0, 1024), block);
        Assert.False(fifo.Read(block)); // 976 left: padded with silence, then priming again
        Assert.Equal(Frames(1024, 976), block[..(976 * Ch)]);
        Assert.All(block[(976 * Ch)..], v => Assert.Equal(0f, v));
        Assert.Equal(0, fifo.Depth);
    }

    [Fact]
    public void Overflow_trims_to_the_target_in_frames()
    {
        var fifo = new AudioFifo(48000, targetMs: 40, capMs: 250, channels: Ch); // cap 12000 frames
        fifo.BeginSession();
        var reader = new float[Ch];
        fifo.Read(reader); // a sender is reading: overflows count
        fifo.Write(Frames(0, 12001));
        Assert.Equal(1920, fifo.Depth);
        Assert.Equal(1, fifo.Overflows);
        Assert.Equal(12001 - 1920, fifo.DroppedFrames);
        var block = new float[4 * Ch];
        Assert.True(fifo.Read(block));
        Assert.Equal(Frames(12001 - 1920, 4), block); // the newest frames are kept
    }

    [Fact]
    public void The_mixer_ledger_stays_stereo()
    {
        var fifo = new AudioFifo(48000, 40, 250, channels: Ch);
        Assert.Throws<InvalidOperationException>(() => fifo.WriteMixer(new Dictionary<uint, float[]> { [1] = new float[2 * 10] }, 10));
        Assert.Throws<InvalidOperationException>(() => fifo.RemoveMixerSource(1));

        var stereo = new AudioFifo(44100, 0, 250);
        Assert.Equal(2, stereo.Channels);
        stereo.WriteMixer(new Dictionary<uint, float[]> { [1] = new float[2 * 10] }, 10);
        Assert.Equal(10, stereo.Depth);
    }
}
