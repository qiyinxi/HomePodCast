using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// The pure parts of the 5.1 capture (experimental since 1.4): the three pair resamplers keep every channel in its slot
/// and all six channels in step. The WASAPI side needs a 5.1 endpoint and is on the device checklist.
/// </summary>
public sealed class SurroundCaptureTests
{
    private static float[] Frames(int count, Func<int, int, float> sample)
    {
        var frames = new float[count * 6];
        for (int f = 0; f < count; f++)
            for (int c = 0; c < 6; c++) frames[f * 6 + c] = sample(f, c);
        return frames;
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Every_channel_stays_in_its_slot_at_48_kHz(int deviceRate)
    {
        var resampler = new SurroundResampler(deviceRate, 48000);
        var output = new List<float>();
        int produced = 0;
        for (int block = 0; block < 50; block++)
            produced += resampler.Process(Frames(deviceRate / 100, (_, c) => (c + 1) / 10f), output);

        Assert.Equal(output.Count, produced * 6);
        Assert.InRange(produced, 48000 / 2 - 200, 48000 / 2); // half a second, less the kernel's look-ahead
        for (int f = produced / 2; f < produced; f++)          // past the kernel's start-up
            for (int c = 0; c < 6; c++)
                Assert.Equal((c + 1) / 10f, output[f * 6 + c], 3);
    }

    [Fact]
    public void All_pairs_put_out_the_same_frames_whatever_the_block_sizes_and_trim()
    {
        var resampler = new SurroundResampler(44100, 48000) { Adjust = 300e-6 };
        var reference = new Resampler(44100, 48000) { Adjust = 300e-6 };
        var output = new List<float>();
        var expected = new List<float>();
        var random = new Random(7);
        for (int block = 0; block < 200; block++)
        {
            int count = random.Next(1, 900);
            int start = block * 1000;
            var frames = Frames(count, (f, c) => MathF.Sin((start + f) * 0.01f * (c + 1)));
            resampler.Process(frames, output);
            var pair = new float[count * 2];
            for (int f = 0; f < count; f++) (pair[f * 2], pair[f * 2 + 1]) = (frames[f * 6 + 4], frames[f * 6 + 5]);
            reference.Process(pair, expected);
        }

        Assert.Equal(expected.Count / 2, output.Count / 6);
        for (int f = 0; f < expected.Count / 2; f++)
        {
            Assert.Equal(expected[f * 2], output[f * 6 + 4]); // Ls, Rs: the third pair, bit for bit as one stereo resampler
            Assert.Equal(expected[f * 2 + 1], output[f * 6 + 5]);
        }
    }

    [Fact]
    public void Reset_starts_all_pairs_afresh()
    {
        var resampler = new SurroundResampler(44100, 48000);
        var first = new List<float>();
        var again = new List<float>();
        var frames = Frames(441, (f, c) => f % 7 == c ? 1f : 0f);
        resampler.Process(frames, first);
        resampler.Reset();
        resampler.Process(frames, again);
        Assert.Equal(first, again);
    }

    [Fact]
    public void A_stereo_FIFO_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SurroundCapture(new AudioFifo(44100, 16, 58), () => null, () => null));
    }
}
