using System.Text.Json;
using HomePodCast.Audio;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

public class ParametricEqTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    /// <summary>Measured gain (dB) at `freq` after the glide has settled, from a processed sine.</summary>
    private static double MeasuredDb(Equalizer eq, double freq, double amp = 0.1)
    {
        var x = Signals.Sine(Rate, freq, Rate, amp);
        Signals.Run(eq, x, 352);
        return Signals.Db(Signals.Tone(x.AsSpan(Rate), Rate, freq) / amp);
    }

    public static TheoryData<EqBand, double> Shapes => new()
    {
        { new EqBand(EqFilterType.Peak, 700, 9, 2), 700 },
        { new EqBand(EqFilterType.Peak, 700, 9, 2), 1400 },
        { new EqBand(EqFilterType.Peak, 2600, -12, 0.5), 2600 },
        { new EqBand(EqFilterType.LowShelf, 180, 6, 0.707), 50 },
        { new EqBand(EqFilterType.HighShelf, 5000, -6, 0.707), 15000 },
        { new EqBand(EqFilterType.LowCut, 120, Slope: 12), 60 },
        { new EqBand(EqFilterType.LowCut, 120, Slope: 24), 60 },
        { new EqBand(EqFilterType.LowCut, 120, Slope: 48), 60 },
        { new EqBand(EqFilterType.HighCut, 4000, Slope: 24), 8000 },
        { new EqBand(EqFilterType.Notch, 1000, Q: 4), 1000 },
        { new EqBand(EqFilterType.Notch, 1000, Q: 4), 2000 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_band_type_sounds_like_its_drawn_response(EqBand band, double freq)
    {
        var eq = new Equalizer(Rate);
        eq.SetBands([band]);
        double analytic = eq.MagnitudeDb(freq), measured = MeasuredDb(eq, freq);
        output.WriteLine($"{band.Type} {band.Frequency} Hz {band.Slope} dB/oct at {freq} Hz: response {analytic:F2}, measured {measured:F2}");
        AssertLevel(analytic, measured);
    }

    /// <summary>The measured level matches the response; a deep cut (a notch's centre) only has to be at least 40 dB down.</summary>
    private static void AssertLevel(double analytic, double measured, double tolerance = 0.3)
    {
        if (analytic < -60) Assert.True(measured < -40, $"response {analytic:F1} dB, measured {measured:F1} dB");
        else Assert.InRange(measured, analytic - tolerance, analytic + tolerance);
    }

    [Fact]
    public void Shapes_have_their_textbook_values()
    {
        Assert.InRange(Equalizer.ResponseDb(new EqBand(EqFilterType.Peak, 1234, 7.5, 3), 1234), 7.45, 7.55);
        Assert.InRange(Equalizer.ResponseDb(new EqBand(EqFilterType.LowCut, 100), 100), -3.2, -2.8);           // Butterworth: −3 dB at the corner
        Assert.InRange(Equalizer.ResponseDb(new EqBand(EqFilterType.LowCut, 100, Slope: 48), 100), -3.2, -2.8);
        Assert.InRange(Equalizer.ResponseDb(new EqBand(EqFilterType.LowCut, 400, Slope: 24), 100), -49, -47); // two octaves of 24 dB
        Assert.InRange(Equalizer.ResponseDb(new EqBand(EqFilterType.HighCut, 500, Slope: 36), 2000), -75, -70);
        Assert.True(Equalizer.ResponseDb(new EqBand(EqFilterType.Notch, 3000, Q: 8), 3000) < -60);
        Assert.InRange(Equalizer.ResponseDb(new EqBand(EqFilterType.Notch, 3000, Q: 8), 1000), -0.1, 0);
        Assert.Equal(0, Equalizer.ResponseDb(new EqBand(EqFilterType.LowCut, 1000, Enabled: false), 50));
    }

    [Fact]
    public void Bands_add_up_and_the_output_gain_moves_everything()
    {
        var eq = new Equalizer(Rate);
        EqBand[] bands = [new(EqFilterType.Peak, 100, 6, 1.5), new(EqFilterType.Peak, 6000, -6, 1.5)];
        eq.SetBands(bands, outputGainDb: -3);
        Assert.InRange(eq.MagnitudeDb(100), 6 - 3 - 0.2, 6 - 3 + 0.2);
        Assert.InRange(eq.MagnitudeDb(6000), -6 - 3 - 0.2, -6 - 3 + 0.2);
        Assert.InRange(MeasuredDb(eq, 100), 2.7, 3.3);
        Assert.InRange(MeasuredDb(eq, 1000), -3.4, -2.6);
        Assert.Equal(EqPreset.Custom, eq.Preset);
    }

    [Fact]
    public void Saved_five_band_curves_load_as_the_same_filters()
    {
        foreach (var (preset, gains) in new (string, double[])[] { ("reduce-bass", []), ("vocal-boost", []), ("custom", [3, -3, 6, -2, 4]) })
        {
            var old = new Equalizer(Rate);
            old.Set(preset, gains);
            var settings = new EqSettings { Preset = preset, Gains = gains.Length > 0 ? gains : new double[5] };
            var now = new Equalizer(Rate);
            now.Set(settings);
            Assert.Equal(old.Bands, now.Bands);
            Assert.Equal(old.GainsDb, now.GainsDb);
            Assert.Equal(5, now.Bands.Count);
        }
    }

    [Fact]
    public void Parametric_settings_win_over_the_five_band_values_and_keep_them_close_for_older_versions()
    {
        var settings = new EqSettings
        {
            Preset = "custom",
            Gains = [9, 9, 9, 9, 9], // what an older version would read
            Bands = [new() { Type = EqFilterType.LowCut, Frequency = 80, Slope = 24 }, new() { Type = EqFilterType.Peak, Frequency = 3000, GainDb = 4, Q = 1.2 }],
        };
        var eq = new Equalizer(Rate);
        eq.Set(settings);
        Assert.Equal(2, eq.Bands.Count);
        Assert.InRange(eq.MagnitudeDb(3000), 3.8, 4.2);
        double[] legacy = Equalizer.LegacyValues(eq.Bands);
        Assert.Equal(legacy, eq.GainsDb);
        Assert.InRange(legacy[3], 3, 4.5); // 3.5 kHz sits on the peak
        Assert.True(legacy[0] < -1);       // 60 Hz is below the low cut
    }

    [Fact]
    public void Turning_the_EQ_off_passes_audio_bit_exact_once_settled()
    {
        var settings = new EqSettings { Preset = "custom", Bands = [new() { Type = EqFilterType.HighCut, Frequency = 3000, Slope = 48 }], OutputGainDb = 4 };
        var eq = new Equalizer(Rate);
        eq.Set(settings);
        Signals.Run(eq, Signals.Noise(Rate / 2, 0.3));
        settings.Enabled = false;
        eq.Set(settings);
        Signals.Run(eq, Signals.Noise(Rate / 2, 0.3, seed: 2));
        var x = Signals.Noise(Rate / 10, 0.5, seed: 3);
        var copy = (float[])x.Clone();
        Signals.Run(eq, x);
        Assert.Equal(copy, x);
    }

    /// <summary>Largest second difference of channel 0 in [from, to): a click shows as a spike.</summary>
    private static double SecondDiff(float[] x, int from, int to)
    {
        double max = 0;
        for (int i = Math.Max(1, from); i < to - 1; i++) max = Math.Max(max, Math.Abs(x[(i + 1) * 2] - 2 * x[i * 2] + x[(i - 1) * 2]));
        return max;
    }

    private static float[] RunWithChange(Equalizer eq, double freq, int changeAt, Action change)
    {
        var x = Signals.Sine(Rate, freq, Rate, 0.5);
        for (int i = 0; i < Rate; i += 256)
        {
            if (i == changeAt) change();
            int n = Math.Min(256, Rate - i);
            eq.Process(x.AsSpan(i * 2, n * 2), n);
        }
        return x;
    }

    [Fact]
    public void Sweeping_a_band_s_frequency_and_Q_glides_without_a_click()
    {
        var eq = new Equalizer(Rate);
        eq.SetBands([new EqBand(EqFilterType.Peak, 150, 12, 4)]);
        int at = 86 * 256;
        var x = RunWithChange(eq, 100, at, () => eq.SetBands([new EqBand(EqFilterType.Peak, 90, 12, 0.7)]));
        // The level changes, and the sine's own second difference with it: a click is a spike above both ends.
        double before = SecondDiff(x, Rate / 4, at), during = SecondDiff(x, at, at + Rate / 5), after = SecondDiff(x, Rate * 3 / 4, Rate);
        output.WriteLine($"second difference before {before:E2}, during {during:E2}, after {after:E2}");
        Assert.True(during < Math.Max(before, after) * 1.1, "discontinuity while the band moved");
    }

    [Fact]
    public void Changing_a_band_s_shape_crossfades_without_a_click()
    {
        foreach (var (from, to) in new (EqBand, EqBand)[]
        {
            (new(EqFilterType.Peak, 200, 9, 1), new(EqFilterType.LowCut, 200, Slope: 24)),
            (new(EqFilterType.LowCut, 150, Slope: 48), new(EqFilterType.LowCut, 150, Slope: 48, Enabled: false)),
            (new(EqFilterType.Notch, 100, Q: 2, Enabled: false), new(EqFilterType.Notch, 100, Q: 2)),
        })
        {
            var eq = new Equalizer(Rate);
            eq.SetBands([from]);
            int at = 86 * 256;
            var x = RunWithChange(eq, 100, at, () => eq.SetBands([to]));
            double before = SecondDiff(x, Rate / 4, at), during = SecondDiff(x, at, at + Rate / 5), after = SecondDiff(x, Rate * 3 / 4, Rate);
            output.WriteLine($"{from.Type} → {to.Type} ({to.Enabled}): second difference before {before:E2}, during {during:E2}, after {after:E2}");
            Assert.True(during < Math.Max(Math.Max(before, after), 1e-4) * 1.1, $"click switching {from} to {to}");
            AssertLevel(eq.MagnitudeDb(100), MeasuredDb(eq, 100, 0.5), 0.5);
        }
    }

    [Fact]
    public void Process_does_not_allocate_with_every_band_moving()
    {
        var eq = new Equalizer(Rate);
        var x = Signals.Noise(4096, 0.3);
        var a = Enumerable.Range(0, Equalizer.MaxBands).Select(i => new EqBand(EqFilterType.HighCut, 1000 + i * 1000, Slope: 48)).ToArray();
        var b = Enumerable.Range(0, Equalizer.MaxBands).Select(i => new EqBand(EqFilterType.Peak, 100 + i * 500, 6, 2)).ToArray();
        eq.SetBands(a);
        for (int i = 0; i < 4; i++) eq.Process(x, 4096);
        eq.SetBands(b, 3); // UI thread, may allocate
        long bytes = Signals.Allocated(() => { for (int i = 0; i < 8; i++) eq.Process(x, 4096); });
        Assert.Equal(0, bytes);
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void Deleting_a_band_leaves_the_others_where_they_are()
    {
        var eq = new Equalizer(Rate);
        EqBand[] bands = [new(EqFilterType.Peak, 50, 0, 1), new(EqFilterType.Peak, 100, 12, 1), new(EqFilterType.Peak, 8000, 12, 1)];
        eq.SetBands(bands);
        int slot100 = eq.SlotOf(1), slot8k = eq.SlotOf(2);
        // An 8 kHz tone: with slots tied to list positions, the 8 kHz boost faded out while the 100 Hz bell swept up to it.
        var x = Signals.Sine(Rate, 8000, Rate, 0.05);
        int at = 86 * 256;
        for (int i = 0; i < Rate; i += 256)
        {
            if (i == at) eq.SetBands([bands[1], bands[2]]);
            int n = Math.Min(256, Rate - i);
            eq.Process(x.AsSpan(i * 2, n * 2), n);
        }
        Assert.Equal((slot100, slot8k), (eq.SlotOf(0), eq.SlotOf(1)));
        double Level(int from) => Signals.Db(Signals.Tone(x.AsSpan(from * 2, 2205 * 2), Rate, 8000) / 0.05);
        double before = Level(at - 2205);
        foreach (int from in new[] { at, at + 1102, at + 2205, at + 4410 })
            Assert.InRange(Level(from), before - 0.3, before + 0.3);
    }

    [Fact]
    public void A_custom_curve_with_every_band_deleted_is_flat_plus_the_output_gain()
    {
        var eq = new Equalizer(Rate);
        eq.Set(new EqSettings { Preset = "custom", Bands = [], Gains = [-6, -6, -6, -6, -6], OutputGainDb = -6 });
        Assert.Empty(eq.Bands);
        Assert.Equal(-6, eq.MagnitudeDb(1000), 6);
        Assert.InRange(MeasuredDb(eq, 1000), -6.2, -5.8);
    }

    [Fact]
    public void A_NaN_sample_does_not_silence_everything_after_it()
    {
        var eq = new Equalizer(Rate);
        eq.SetBands([new EqBand(EqFilterType.Peak, 200, 6, 1), new EqBand(EqFilterType.LowCut, 50, Slope: 24)]);
        var deEsser = new DeEsser(Rate) { Enabled = true, ThresholdDb = -60 };
        var x = Signals.Sine(Rate, 300, 4096, 0.5);
        x[100] = float.NaN;
        x[201] = float.PositiveInfinity;
        eq.Process(x, 2048);
        deEsser.Process(x, 2048);
        var y = Signals.Sine(Rate, 300, 4096, 0.5);
        eq.Process(y, 4096);
        deEsser.Process(y, 4096);
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void Bands_are_kept_in_their_ranges()
    {
        var band = new EqBand((EqFilterType)99, double.NaN, 99, 0, Slope: 100).Normalized();
        Assert.Equal(new EqBand(EqFilterType.Peak, 1000, Equalizer.MaxGainDb, Equalizer.MinQ, 48), band);
        Assert.Equal(20, new EqBand(EqFilterType.Peak, 1).Normalized().Frequency);
        var eq = new Equalizer(Rate);
        eq.SetBands(Enumerable.Repeat(new EqBand(EqFilterType.Peak, 100, 1), 40).ToList());
        Assert.Equal(Equalizer.MaxBands, eq.Bands.Count);
    }

    [Fact]
    public void Settings_round_trip_through_the_config_file_with_readable_types()
    {
        var settings = new EqSettings
        {
            Preset = "custom", OutputGainDb = -2, Enabled = false,
            Bands = [new() { Type = EqFilterType.HighShelf, Frequency = 8000, GainDb = 3, Q = 0.8 }],
        };
        string json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"HighShelf\"", json);
        var back = JsonSerializer.Deserialize<EqSettings>(json)!;
        Assert.Equal(settings.Bands[0].ToBand(), back.Bands![0].ToBand());
        Assert.Equal((-2.0, false), (back.OutputGainDb, back.Enabled));

        // A config written before the parametric EQ: no bands, on.
        var old = JsonSerializer.Deserialize<EqSettings>("{\"Preset\":\"custom\",\"Gains\":[1,2,3,4,5]}")!;
        Assert.Null(old.Bands);
        Assert.True(old.Enabled);
        var clone = settings.Clone();
        clone.Bands![0].GainDb = 9;
        Assert.Equal(3, settings.Bands[0].GainDb); // a copy, not the same bands
    }
}
