using System.Diagnostics;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class CompressorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Rate = 44100, Packet = 352;
    private static readonly float Ceiling = MathF.Pow(10, Compressor.CeilingDb / 20);

    private static float[] Tone(double amplitude, double seconds, double freq = 1000, double rightScale = 1)
    {
        int n = (int)(Rate * seconds);
        var x = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            float s = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / Rate));
            x[i * 2] = s;
            x[i * 2 + 1] = (float)(s * rightScale);
        }
        return x;
    }

    /// <summary>Feed the signal packet by packet, exactly as the sender does.</summary>
    private static void Run(IAudioEffect fx, float[] x)
    {
        for (int i = 0; i < x.Length; i += Packet * 2)
            fx.Process(x.AsSpan(i), Math.Min(Packet, (x.Length - i) / 2));
    }

    private static double PeakDb(ReadOnlySpan<float> x, int channel = -1)
    {
        float peak = 0;
        for (int i = 0; i < x.Length; i++)
            if (channel < 0 || i % 2 == channel) peak = Math.Max(peak, Math.Abs(x[i]));
        return 20 * Math.Log10(peak);
    }

    private static Compressor On() => new(Rate) { Enabled = true };

    [Theory]
    [InlineData(-60f, 8f)]      // below the knee: make-up gain only
    [InlineData(-40f, 8f)]
    [InlineData(-30f, 7.0625f)] // middle of the 10 dB soft knee: 3/4 * 5² / 20 = 0.9375 dB of reduction
    [InlineData(-20f, 0.5f)]    // above: -30 + 10/4 = -27.5 out, +8 make-up
    [InlineData(-6f, -10f)]     // -30 + 24/4 = -24 out → -18 + 8
    [InlineData(0f, -14.5f)]    // full scale comes out at -14.5 dBFS
    public void Gain_curve_matches_threshold_ratio_knee_and_makeup(float inputDb, float expectedGainDb)
    {
        Assert.Equal(expectedGainDb, Compressor.GainDb(inputDb), 3);
    }

    [Fact]
    public void Gain_curve_is_continuous_and_output_level_never_falls_as_input_rises()
    {
        float previousOut = float.NegativeInfinity;
        for (float x = Compressor.FloorDb; x <= 12f; x += 0.05f)
        {
            float outDb = x + Compressor.GainDb(x);
            Assert.True(outDb >= previousOut - 1e-3f, $"output fell at {x} dB");
            if (!float.IsNegativeInfinity(previousOut)) Assert.True(outDb - previousOut < 0.06f, $"jump at {x} dB");
            Assert.True(outDb <= Compressor.CeilingDb + 1e-4f, $"above the ceiling at {x} dB");
            previousOut = outDb;
        }
    }

    [Fact]
    public void Silence_and_hiss_get_no_makeup_gain()
    {
        Assert.Equal(0f, Compressor.GainDb(-80f));
        Assert.Equal(0f, Compressor.GainDb(float.NegativeInfinity));
        Assert.Equal(0f, Compressor.GainDb(float.NaN));
    }

    [Fact]
    public void Loud_tone_settles_on_the_curve()
    {
        var x = Tone(0.5, 1.0); // -6 dBFS
        Run(On(), x);
        double expected = -6.02 + Compressor.GainDb(-6.02f);
        Assert.InRange(PeakDb(x.AsSpan(x.Length - Rate / 10 * 2)), expected - 0.3, expected + 0.3);
    }

    [Fact]
    public void Quiet_tone_is_lifted_by_the_makeup_gain_after_the_release()
    {
        var x = Tone(0.01, 3.0); // -40 dBFS
        Run(On(), x);
        Assert.InRange(PeakDb(x.AsSpan(x.Length - Rate / 10 * 2)), -40 + 8 - 0.3, -40 + 8 + 0.3);
    }

    [Fact]
    public void Gain_is_stereo_linked()
    {
        var x = Tone(0.5, 1.0, rightScale: 0.1); // right channel 20 dB quieter
        Run(On(), x);
        var tail = x.AsSpan(x.Length - Rate / 10 * 2);
        Assert.InRange(PeakDb(tail, 0) - PeakDb(tail, 1), 19.9, 20.1); // same gain on both: the image does not shift
    }

    [Fact]
    public void Sudden_full_scale_after_a_quiet_passage_is_held_at_the_ceiling()
    {
        var c = On();
        Run(c, Tone(0.01, 2.0));            // quiet: gain sits at +8 dB
        Assert.InRange(c.CurrentGainDb, 7.5f, 8.01f);
        var burst = new float[Packet * 2 * 20];
        Array.Fill(burst, 1f);              // worst case: full-scale step, no zero crossing to hide in
        Run(c, burst);
        Assert.All(burst, s => Assert.True(Math.Abs(s) <= Ceiling + 1e-6f, $"{s} above the ceiling"));
        Assert.InRange(PeakDb(burst.AsSpan(burst.Length - Packet * 2)), -14.6, -14.4); // and then on the curve
    }

    [Fact]
    public void Disabled_is_a_bit_exact_bypass()
    {
        var x = Tone(0.9, 0.2);
        var y = (float[])x.Clone();
        Run(new Compressor(Rate), y);
        Assert.Equal(x, y);
    }

    [Fact]
    public void Turning_it_off_fades_back_to_unity_and_then_bypasses()
    {
        var c = On();
        Run(c, Tone(1.0, 0.5));
        Assert.True(c.CurrentGainDb < -14);
        c.Enabled = false;
        var fade = Tone(1.0, 0.05);
        Run(c, fade);
        Assert.True(PeakDb(fade) < -1, "no jump straight back to full level");
        Run(c, Tone(1.0, 3.0));             // release toward 0 dB
        Assert.Equal(0f, c.CurrentGainDb);
        var x = Tone(1.0, 0.1);
        var y = (float[])x.Clone();
        Run(c, y);
        Assert.Equal(x, y);
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var c = On();
        var chain = new EffectChain(c);
        var packet = Tone(0.7, (double)Packet / Rate);
        for (int i = 0; i < 200; i++) chain.Process(packet, Packet); // warm up (JIT, statics)

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++)
        {
            chain.Process(packet, Packet);
            c.Enabled = (i / 500) % 2 == 0; // includes the fade/bypass paths
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Costs_well_under_one_percent_of_a_core()
    {
        var c = On();
        var packet = Tone(0.7, (double)Packet / Rate);
        for (int i = 0; i < 500; i++) c.Process(packet, Packet);
        int packets = 60 * Rate / Packet; // one minute of audio
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < packets; i++) c.Process(packet, Packet);
        sw.Stop();
        double share = sw.Elapsed.TotalSeconds / 60.0;
        output.WriteLine($"night mode: {sw.Elapsed.TotalMilliseconds:F1} ms per minute of audio = {share:P3} of one core");
        Assert.True(share < 0.01, $"{share:P3} of real time");
    }

    [Fact]
    public void Effect_chain_runs_in_order_and_accepts_an_effect_in_front()
    {
        var log = new List<string>();
        var chain = new EffectChain(new Probe("night", log));
        chain.Insert(0, new Probe("eq", log));
        chain.Process(new float[Packet * 2], Packet);
        Assert.Equal(new[] { "eq", "night" }, log);
        chain.Remove(chain.Effects[0]);
        Assert.Single(chain.Effects);
    }

    private sealed class Probe(string name, List<string> log) : IAudioEffect
    {
        public void Process(Span<float> interleaved, int frames) => log.Add(name);
    }
}
