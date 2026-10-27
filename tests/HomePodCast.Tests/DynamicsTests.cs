using HomePodCast.Audio;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

public class DynamicsTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    private static double LevelDb(float[] x, int fromFrame, double freq) =>
        Signals.Db(Signals.Tone(x.AsSpan(fromFrame * 2), Rate, freq));

    [Theory]
    [InlineData(-6, -24, 4, -19.5)]   // 18 dB over, 4:1 → 4.5 dB over the threshold
    [InlineData(-6, -12, 2, -9)]      // 6 dB over, 2:1 → 3 dB over
    [InlineData(-40, -24, 4, -40)]    // below the threshold: untouched
    public void Compressor_follows_its_static_curve(double inDb, float threshold, float ratio, double expectedDb)
    {
        var comp = new DynamicsCompressor(Rate) { Enabled = true, ThresholdDb = threshold, Ratio = ratio, KneeDb = 0, AttackMs = 1, ReleaseMs = 50 };
        double amp = Math.Pow(10, inDb / 20);
        var x = Signals.Sine(Rate, 1000, Rate, amp);
        Signals.Run(comp, x);
        double measured = LevelDb(x, Rate / 2, 1000);
        output.WriteLine($"in {inDb} dB → {measured:F2} dB (gain reduction {comp.GainReductionDb:F1} dB)");
        Assert.InRange(measured, expectedDb - 0.7, expectedDb + 0.7);
    }

    [Fact]
    public void Compressor_make_up_gain_lifts_the_result_and_off_is_bit_exact()
    {
        var comp = new DynamicsCompressor(Rate) { Enabled = true, ThresholdDb = -20, Ratio = 4, MakeupDb = 6, KneeDb = 0 };
        var x = Signals.Sine(Rate, 500, Rate, 0.05); // −26 dB: under the threshold, only the make-up
        Signals.Run(comp, x);
        Assert.InRange(LevelDb(x, Rate / 2, 500), -26 + 6 - 0.3, -26 + 6 + 0.3);

        comp.Enabled = false;
        Signals.Run(comp, Signals.Sine(Rate, 500, Rate / 2, 0.05)); // fades back to unity
        var y = Signals.Noise(Rate / 10, 0.5);
        var copy = (float[])y.Clone();
        Signals.Run(comp, y);
        Assert.Equal(copy, y);
        Assert.Equal(0, comp.GainReductionDb);
    }

    [Fact]
    public void Soft_knee_is_continuous()
    {
        float previous = 0;
        for (float db = -40; db <= 0; db += 0.25f)
        {
            float r = DynamicsCompressor.ReductionDb(db, -20, 4, 10);
            Assert.True(r <= previous + 1e-4f && r >= previous - 0.25f, $"jump at {db} dB");
            previous = r;
        }
        Assert.Equal(0, DynamicsCompressor.ReductionDb(float.NaN, -20, 4, 10));
    }

    [Fact]
    public void De_esser_turns_down_loud_sibilance_only()
    {
        var deEsser = new DeEsser(Rate) { Enabled = true, FrequencyHz = 5000, ThresholdDb = -30, RangeDb = 10 };
        var x = new float[Rate * 2];
        var hiss = Signals.Sine(Rate, 8000, Rate, 0.3); // −10 dB: 20 dB over the threshold
        var voice = Signals.Sine(Rate, 300, Rate, 0.3);
        for (int i = 0; i < x.Length; i++) x[i] = hiss[i] + voice[i];
        Signals.Run(deEsser, x);
        double sibilance = LevelDb(x, Rate / 2, 8000) - Signals.Db(0.3), body = LevelDb(x, Rate / 2, 300) - Signals.Db(0.3);
        output.WriteLine($"8 kHz {sibilance:F2} dB, 300 Hz {body:F2} dB, reduction {deEsser.GainReductionDb:F1} dB");
        Assert.InRange(sibilance, -10.5, -7);   // at most the range
        Assert.InRange(body, -0.3, 0.3);        // the voice keeps its level

        var quiet = new DeEsser(Rate) { Enabled = true, FrequencyHz = 5000, ThresholdDb = -30 };
        var y = Signals.Sine(Rate, 8000, Rate / 4, 0.005); // −46 dB: under the threshold, untouched
        var copy = (float[])y.Clone();
        Signals.Run(quiet, y);
        Assert.Equal(copy, y);
    }

    [Fact]
    public void Limiter_never_lets_a_sample_past_the_ceiling()
    {
        var limiter = new PeakLimiter(Rate) { Enabled = true, CeilingDb = -3 };
        var x = Signals.Noise(Rate, 1.8);
        Signals.Run(limiter, x);
        float ceiling = MathF.Pow(10, -3 / 20f);
        Assert.All(x, v => Assert.True(MathF.Abs(v) <= ceiling + 1e-6f));
        Assert.True(limiter.GainReductionDb > 3);
    }

    [Fact]
    public void Dynamics_do_not_allocate()
    {
        var comp = new DynamicsCompressor(Rate) { Enabled = true };
        var deEsser = new DeEsser(Rate) { Enabled = true };
        var limiter = new PeakLimiter(Rate) { Enabled = true };
        var x = Signals.Noise(4096, 0.9);
        long bytes = Signals.Allocated(() =>
        {
            for (int i = 0; i < 8; i++) { comp.Process(x, 4096); deEsser.Process(x, 4096); limiter.Process(x, 4096); }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Mic_chain_runs_every_stage_in_order_from_the_settings()
    {
        var settings = new EffectsSettings
        {
            LowCutEnabled = true, LowCutHz = 120, LowCutSlope = 24,
            CompressorEnabled = true, CompressorThresholdDb = -30, CompressorRatio = 6, CompressorAttackMs = 2, CompressorReleaseMs = 300,
            CompressorKneeDb = 3, CompressorMakeupDb = 5,
            DeEsserEnabled = true, DeEsserFrequencyHz = 7000, DeEsserThresholdDb = -20, DeEsserRangeDb = 6,
            LimiterEnabled = true, LimiterCeilingDb = -2, LimiterReleaseMs = 40,
        };
        using var fx = new MicEffects(settings);
        Assert.Equal<IAudioEffect>([fx.LowCut, fx.MicEq, fx.Compressor, fx.DeEsser, fx.Reverb, fx.Limiter], fx.Chain);
        Assert.Equal(new EqBand(EqFilterType.LowCut, 120, Slope: 24), fx.LowCut.Bands.Single());
        Assert.Equal((true, -30f, 6f, 2f, 300f, 3f, 5f), (fx.Compressor.Enabled, fx.Compressor.ThresholdDb, fx.Compressor.Ratio,
            fx.Compressor.AttackMs, fx.Compressor.ReleaseMs, fx.Compressor.KneeDb, fx.Compressor.MakeupDb));
        Assert.Equal((true, 7000f, -20f, 6f), (fx.DeEsser.Enabled, fx.DeEsser.FrequencyHz, fx.DeEsser.ThresholdDb, fx.DeEsser.RangeDb));
        Assert.Equal((true, -2f, 40f), (fx.Limiter.Enabled, fx.Limiter.CeilingDb, fx.Limiter.ReleaseMs));

        settings.LowCutEnabled = settings.CompressorEnabled = settings.DeEsserEnabled = settings.LimiterEnabled = false;
        fx.Apply();
        Assert.False(fx.LowCut.Bands.Single().Enabled);
        Assert.False(fx.Compressor.Enabled || fx.DeEsser.Enabled || fx.Limiter.Enabled);
    }
}
