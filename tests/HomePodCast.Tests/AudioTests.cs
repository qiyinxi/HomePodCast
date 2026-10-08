using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class ResamplerTests
{
    private static float[] Sine(int rate, double freq, double seconds, double amp = 0.5)
    {
        int n = (int)(rate * seconds);
        var x = new float[n * 2];
        for (int i = 0; i < n; i++)
            x[i * 2] = x[i * 2 + 1] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
        return x;
    }

    /// <summary>Amplitude of `freq` in a mono signal (single-bin DFT).</summary>
    private static double Tone(IReadOnlyList<float> stereo, int rate, double freq)
    {
        double re = 0, im = 0;
        int n = stereo.Count / 2;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * freq * i / rate;
            re += stereo[i * 2] * Math.Cos(a);
            im += stereo[i * 2] * Math.Sin(a);
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    [Fact]
    public void Converts_48k_to_44k1_keeping_a_1khz_tone_and_the_frame_count()
    {
        var r = new Resampler(48000, 44100);
        var output = new List<float>();
        var input = Sine(48000, 1000, 1.0);
        for (int i = 0; i < input.Length; i += 960) // 10 ms chunks, like WASAPI
            r.Process(input.AsSpan(i, Math.Min(960, input.Length - i)), output);

        int frames = output.Count / 2;
        // 64-tap kernel: 31 input frames of start-up delay + 32 of look-ahead ≈ 58 output frames held back
        Assert.InRange(frames, 44100 - 64, 44100 - 50);
        var steady = output.Skip(4410 * 2).Take(44100).ToList(); // skip the first 100 ms
        Assert.InRange(Tone(steady, 44100, 1000), 0.49, 0.51);  // passband: unity gain
    }

    [Fact]
    public void Removes_content_above_the_new_nyquist()
    {
        var r = new Resampler(48000, 44100);
        var output = new List<float>();
        r.Process(Sine(48000, 23000, 0.5), output);       // 23 kHz: inaudible, above 22.05 kHz
        var steady = output.Skip(2205 * 2).ToList();
        double alias = Tone(steady, 44100, 44100 - 23000);  // where it would fold to (21.1 kHz)
        Assert.True(alias < 0.5 * 0.01, $"alias amplitude {alias}");  // at least -40 dB
    }

    [Fact]
    public void Adjust_changes_the_output_rate_by_the_requested_ppm()
    {
        var a = new Resampler(44100, 44100);
        var b = new Resampler(44100, 44100) { Adjust = +500e-6 };  // consume input 500 ppm faster
        var outA = new List<float>();
        var outB = new List<float>();
        var input = Sine(44100, 440, 10);
        a.Process(input, outA);
        b.Process(input, outB);
        double ratio = (double)outB.Count / outA.Count;
        Assert.InRange(ratio, 1 - 600e-6, 1 - 400e-6);
    }
}

public class AudioFifoTests
{
    [Fact]
    public void Primes_to_target_before_releasing_audio()
    {
        var fifo = new AudioFifo(44100, targetMs: 12, capMs: 46);
        fifo.Write(new float[200 * 2]);   // 200 frames < 529-frame target
        var dest = new float[352 * 2];
        Assert.False(fifo.Read(dest));    // still priming: silence
        Assert.Equal(200, fifo.Depth);
        fifo.Write(Enumerable.Repeat(0.5f, 400 * 2).ToArray());
        Assert.True(fifo.Read(dest));     // 600 frames ≥ target: real audio
        Assert.Equal(600 - 352, fifo.Depth);
    }

    [Fact]
    public void Overflow_trims_back_to_target_and_underflow_pads_and_reprimes()
    {
        var fifo = new AudioFifo(44100, targetMs: 12, capMs: 46);
        int target = fifo.TargetFrames, cap = fifo.CapFrames;
        fifo.Write(new float[(cap + 100) * 2]);
        Assert.Equal(target, fifo.Depth);
        Assert.Equal(1, fifo.Overflows);

        var dest = new float[352 * 2];
        while (fifo.Depth >= 352) Assert.True(fifo.Read(dest));
        Assert.False(fifo.Read(dest));    // not enough left: padded with silence
        Assert.Equal(0, fifo.Depth);
        fifo.Write(new float[100 * 2]);   // the source is back at once: that was a dropout
        Assert.Equal(1, fifo.Underruns);
    }

    private static (AudioFifo Fifo, Action<int> Advance) Clocked(int targetMs = 12)
    {
        long now = 0;
        var fifo = new AudioFifo(44100, targetMs, targetMs * 3 + 10) { Clock = () => now };
        return (fifo, ms => now += ms * System.Diagnostics.Stopwatch.Frequency / 1000);
    }

    private static void Drain(AudioFifo fifo)
    {
        var dest = new float[352 * 2];
        while (fifo.Read(dest)) { }
    }

    [Fact]
    public void Running_dry_is_a_dropout_only_if_the_source_comes_back_quickly()
    {
        var (fifo, advance) = Clocked();
        fifo.Write(new float[600 * 2]);
        Drain(fifo);
        advance(30);                      // a late burst from the capture device
        fifo.Write(new float[600 * 2]);
        Assert.Equal(1, fifo.Underruns);
        Assert.Equal(0, fifo.IdleGaps);

        Drain(fifo);
        advance(5000);                    // the music was paused for 5 s
        fifo.Write(new float[600 * 2]);
        Assert.Equal(1, fifo.Underruns);
        Assert.Equal(1, fifo.IdleGaps);
    }

    [Fact]
    public void Adaptive_target_grows_on_dropouts_and_relaxes_after_clean_reads()
    {
        var (fifo, advance) = Clocked();
        fifo.MaxTargetFrames = 44100 * 30 / 1000;
        fifo.RelaxAfterReads = 10;
        int baseTarget = fifo.TargetFrames;

        for (int i = 0; i < 10; i++)     // ten dropouts in a row
        {
            fifo.Write(new float[2000 * 2]);
            Drain(fifo);
            advance(20);
        }
        fifo.Write(new float[2000 * 2]);
        Assert.Equal(fifo.MaxTargetFrames, fifo.TargetFrames);   // capped at 30 ms
        Assert.Equal(fifo.TargetFrames + (fifo.CapFrames - fifo.TargetFrames), fifo.CapFrames);

        var dest = new float[352 * 2];
        int before = fifo.TargetFrames;
        for (int i = 0; i < 10; i++)
        {
            fifo.Write(new float[352 * 2]);
            Assert.True(fifo.Read(dest));
        }
        Assert.Equal(before - 44, fifo.TargetFrames);             // one ms back after 10 clean reads
        Assert.True(fifo.TargetFrames > baseTarget);
    }

    [Fact]
    public void Quiet_spells_never_grow_the_adaptive_target()
    {
        var (fifo, advance) = Clocked();
        fifo.MaxTargetFrames = 44100 * 30 / 1000;
        int baseTarget = fifo.TargetFrames;
        for (int i = 0; i < 5; i++)
        {
            fifo.Write(new float[600 * 2]);
            Drain(fifo);
            advance(1000);
        }
        fifo.Write(new float[600 * 2]);
        Assert.Equal(baseTarget, fifo.TargetFrames);
        Assert.Equal(0, fifo.Underruns);
        Assert.Equal(5, fifo.IdleGaps);
    }
}
