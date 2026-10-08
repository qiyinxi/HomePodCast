using HomePodCast.Audio;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

internal static class Signals
{
    public static float[] Sine(int rate, double freq, int frames, double amp = 0.5)
    {
        var x = new float[frames * 2];
        for (int i = 0; i < frames; i++)
            x[i * 2] = x[i * 2 + 1] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
        return x;
    }

    public static float[] Noise(int frames, double amp, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new float[frames * 2];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * (rng.NextDouble() * 2 - 1));
        return x;
    }

    /// <summary>Amplitude of `freq` in channel `ch` of an interleaved stereo signal (single-bin DFT).</summary>
    public static double Tone(ReadOnlySpan<float> stereo, int rate, double freq, int ch = 0)
    {
        double re = 0, im = 0;
        int n = stereo.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * freq * i / rate;
            re += stereo[i * 2 + ch] * Math.Cos(a);
            im += stereo[i * 2 + ch] * Math.Sin(a);
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    /// <summary>Process in WASAPI-sized chunks.</summary>
    public static void Run(IAudioEffect fx, float[] x, int chunk = 441)
    {
        for (int i = 0; i < x.Length / 2; i += chunk)
        {
            int n = Math.Min(chunk, x.Length / 2 - i);
            fx.Process(x.AsSpan(i * 2, n * 2), n);
        }
    }

    /// <summary>Bytes allocated by the second run of `action` on this thread (the first warms up JIT and statics).</summary>
    public static long Allocated(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

public class EqualizerTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    /// <summary>Measured gain (dB) at `freq` after the glide has settled, from a processed sine.</summary>
    private static double MeasuredDb(Equalizer eq, double freq)
    {
        var x = Signals.Sine(Rate, freq, Rate);  // 1 s; the last 0.5 s holds whole cycles of every band frequency
        Signals.Run(eq, x, 352);
        return Signals.Db(Signals.Tone(x.AsSpan(Rate), Rate, freq) / 0.5);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(-12)]
    public void Each_band_alone_hits_its_gain_at_the_band_frequency(double gain)
    {
        for (int band = 0; band < Equalizer.BandCount; band++)
        {
            var gains = new double[Equalizer.BandCount];
            gains[band] = gain;
            var eq = new Equalizer(Rate);
            eq.SetGains(gains);
            double f = Equalizer.BandFrequencies[band];
            Assert.InRange(eq.MagnitudeDb(f), gain - 0.5, gain + 0.5);
            Assert.InRange(MeasuredDb(eq, f), gain - 0.5, gain + 0.5);
        }
    }

    public static TheoryData<string, double[]> Curves => new()
    {
        { "flat", EqPresets.Gains(EqPreset.Flat)! },
        { "bass-boost", EqPresets.Gains(EqPreset.BassBoost)! },
        { "vocal-boost", EqPresets.Gains(EqPreset.VocalBoost)! },
        { "reduce-bass", EqPresets.Gains(EqPreset.ReduceBass)! },
        { "custom", [3, -3, 6, -2, 4] },
        { "custom", [-6, 4, -4, 6, -6] },
        { "custom", [10, 10, 10, 10, 10] },
        { "custom", [-12, 0, 12, 0, -12] },
    };

    [Theory]
    [MemberData(nameof(Curves))]
    public void Curve_passes_through_every_band_gain(string preset, double[] gains)
    {
        var eq = new Equalizer(Rate);
        eq.Set(preset, gains);
        for (int band = 0; band < Equalizer.BandCount; band++)
        {
            double f = Equalizer.BandFrequencies[band];
            double analytic = eq.MagnitudeDb(f);
            double measured = MeasuredDb(eq, f);
            output.WriteLine($"{preset} {f} Hz: want {gains[band]:F1}, response {analytic:F2}, measured {measured:F2}");
            Assert.InRange(analytic, gains[band] - 0.5, gains[band] + 0.5);
            Assert.InRange(measured, gains[band] - 0.5, gains[band] + 0.5);
        }
    }

    [Fact]
    public void Reduce_bass_is_a_gentle_low_shelf()
    {
        var eq = new Equalizer(Rate);
        eq.SetPreset("reduce-bass");
        Assert.Equal(EqPreset.ReduceBass, eq.Preset);
        double previous = double.NegativeInfinity;
        for (double f = 20; f <= 20000; f *= 1.05)
        {
            double db = eq.MagnitudeDb(f);
            if (f <= 100) Assert.InRange(db, -9.5, -6);       // deep bass 6–9 dB down
            if (f >= 400) Assert.InRange(db, -0.5, 0.5);      // the rest untouched
            if (f <= 1000) Assert.True(db >= previous - 0.05, $"not monotonic at {f:F0} Hz"); // no bump, just a slope
            previous = db;
        }
        Assert.InRange(eq.MagnitudeDb(130), -6, -3);          // the knee
        Assert.True(eq.PeakGainDb < 0.5);                     // never adds level
    }

    [Fact]
    public void Preset_ids_round_trip()
    {
        foreach (var p in EqPresets.All) Assert.Equal(p, EqPresets.FromId(EqPresets.Id(p)));
        Assert.Equal(EqPreset.Flat, EqPresets.FromId("nonsense"));
        Assert.Equal(EqPreset.Flat, EqPresets.FromId(null));
    }

    [Fact]
    public void Flat_is_a_bit_exact_passthrough_once_settled()
    {
        var eq = new Equalizer(Rate);
        eq.SetPreset(EqPreset.BassBoost);
        Signals.Run(eq, Signals.Sine(Rate, 100, Rate / 2));
        eq.SetPreset(EqPreset.Flat);
        Signals.Run(eq, Signals.Sine(Rate, 100, Rate / 2)); // glide back and ring out
        var x = Signals.Noise(Rate / 10, 0.5);
        var copy = (float[])x.Clone();
        Signals.Run(eq, x);
        Assert.Equal(copy, x);
    }

    [Fact]
    public void Switching_presets_glides_without_a_click()
    {
        // 80 Hz at -6 dBFS, switch flat -> reduce-bass mid-stream (a night mode toggling it on).
        var eq = new Equalizer(Rate);
        var x = Signals.Sine(Rate, 80, Rate, 0.5);
        int switchAt = 86 * 256;
        for (int i = 0; i < Rate; i += 256)
        {
            if (i == switchAt) eq.SetPreset(EqPreset.ReduceBass);
            int n = Math.Min(256, Rate - i);
            eq.Process(x.AsSpan(i * 2, n * 2), n);
        }

        // A click shows up as a spike in the second difference; a pure 80 Hz sine has a tiny, constant one.
        // (Switching the coefficients in one step measures ~2.7x here; the glide stays at the sine's own value.)
        double SecondDiff(int from, int to)
        {
            double max = 0;
            for (int i = from + 1; i < to - 1; i++)
                max = Math.Max(max, Math.Abs(x[(i + 1) * 2] - 2 * x[i * 2] + x[(i - 1) * 2]));
            return max;
        }
        double before = SecondDiff(Rate / 4, switchAt);
        double during = SecondDiff(switchAt, switchAt + Rate / 5);
        output.WriteLine($"second difference before {before:E2}, during the switch {during:E2}");
        Assert.True(during < before * 1.1, "discontinuity while switching");

        // The level moves gradually: 5 ms after the switch it has gone less than half of the way.
        double Level(int from) => Signals.Db(Signals.Tone(x.AsSpan(from * 2, 2 * Rate / 80 * 2), Rate, 80) / 0.5);
        double start = Level(switchAt - 2 * Rate / 80), early = Level(switchAt + Rate / 200), end = Level(Rate - 2 * Rate / 80);
        output.WriteLine($"level before {start:F2} dB, +5 ms {early:F2} dB, end {end:F2} dB");
        Assert.InRange(end - start, -8, -6.5);
        Assert.True(early - start > (end - start) / 2, "level jumped");
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var eq = new Equalizer(Rate);
        var x = Signals.Noise(4096, 0.3);
        long bytes = Signals.Allocated(() =>
        {
            eq.SetPreset(EqPreset.VocalBoost);
            for (int i = 0; i < 8; i++) eq.Process(x, 4096);
        });
        // SetPreset runs on the UI thread and may allocate; measure Process alone.
        bytes = Signals.Allocated(() => { for (int i = 0; i < 8; i++) eq.Process(x, 4096); });
        Assert.Equal(0, bytes);
    }
}
