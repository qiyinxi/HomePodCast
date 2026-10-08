using HomePodCast.Audio;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

public class ReverbTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    private static float[] ImpulseResponse(Reverb reverb, double seconds)
    {
        var x = new float[(int)(seconds * Rate) * 2];
        x[0] = x[1] = 1;
        Signals.Run(reverb, x);
        return x;
    }

    /// <summary>Time for the impulse response energy to fall 60 dB (Schroeder backward integration, T30 × 2).</summary>
    private static double Rt60(float[] ir)
    {
        int n = ir.Length / 2;
        var energy = new double[n + 1];
        for (int i = n - 1; i >= 0; i--) energy[i] = energy[i + 1] + ir[i * 2] * ir[i * 2] + ir[i * 2 + 1] * ir[i * 2 + 1];
        double total = energy[0];
        int t5 = Array.FindIndex(energy, e => e < total * Math.Pow(10, -0.5));
        int t35 = Array.FindIndex(energy, e => e < total * Math.Pow(10, -3.5));
        return 2.0 * (t35 - t5) / Rate;
    }

    [Fact]
    public void Impulse_response_decays_to_silence_without_nan_or_subnormals()
    {
        var reverb = new Reverb(Rate) { Mix = 1, RoomSize = 1, Damping = 0.2f, PreDelayMs = 10 };
        var ir = ImpulseResponse(reverb, 60);
        Assert.All(ir, v => Assert.True(float.IsFinite(v)));
        float peak = ir.Max(MathF.Abs);
        Assert.InRange(peak, 1e-3f, 1f);

        // 1 s windows: energy keeps falling, and the last seconds are exact zeros (flushed, not subnormal).
        double Energy(int second) => ir.AsSpan(second * Rate * 2, Rate * 2).ToArray().Sum(v => (double)v * v);
        for (int s = 1; s < 20; s++) Assert.True(Energy(s) <= Energy(s - 1) * 1.01 || Energy(s) < 1e-20, $"energy grew in second {s}");
        Assert.Equal(0, Energy(59));
        Assert.False(reverb.HasSubnormals());
        Assert.DoesNotContain(ir, float.IsSubnormal);
    }

    [Fact]
    public void Stays_bounded_with_extreme_settings_and_loud_input()
    {
        var reverb = new Reverb(Rate) { Mix = 1, RoomSize = 1, Damping = 0 };
        var x = Signals.Noise(Rate * 20, 1.0);
        Signals.Run(reverb, x);
        Assert.All(x, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) < 8));
    }

    [Fact]
    public void Tail_length_tracks_room_size()
    {
        double[] rooms = [0.0, 0.3, 0.6, 0.9, 1.0];
        var rt = rooms.Select(r => Rt60(ImpulseResponse(new Reverb(Rate) { Mix = 1, RoomSize = (float)r, Damping = 0.5f, PreDelayMs = 0 }, 30))).ToArray();
        output.WriteLine(string.Join(", ", rooms.Zip(rt, (r, t) => $"room {r:F1}: RT60 {t:F2} s")));
        for (int i = 1; i < rt.Length; i++) Assert.True(rt[i] > rt[i - 1] * 1.15, $"RT60 did not grow from room {rooms[i - 1]} to {rooms[i]}");
        Assert.InRange(rt[0], 0.2, 1.5);
        Assert.InRange(rt[^1], 2, 15);
    }

    [Fact]
    public void Pre_delay_moves_the_onset_of_the_tail()
    {
        int Onset(float preDelayMs)
        {
            var ir = ImpulseResponse(new Reverb(Rate) { Mix = 1, PreDelayMs = preDelayMs }, 0.5);
            return Array.FindIndex(ir, v => MathF.Abs(v) > 1e-6f) / 2;
        }
        int none = Onset(0), hundred = Onset(100);
        Assert.InRange(hundred - none, Rate / 10 - 2, Rate / 10 + 2);
    }

    [Fact]
    public void Turning_off_fades_out_then_passes_audio_through_untouched()
    {
        var reverb = new Reverb(Rate) { Mix = 0.5f, RoomSize = 0.8f };
        Signals.Run(reverb, Signals.Noise(Rate, 0.5));
        reverb.Enabled = false;
        var fade = Signals.Sine(Rate, 440, Rate / 5);
        var reference = (float[])fade.Clone();
        Signals.Run(reverb, fade);
        // The first block after switching off still carries the tail at nearly full level: no hard cut.
        Assert.True(Math.Abs(fade[0] - reference[0]) > 1e-4 || Math.Abs(fade[2] - reference[2]) > 1e-4);

        var x = Signals.Noise(Rate / 10, 0.5, seed: 7);
        var copy = (float[])x.Clone();
        Signals.Run(reverb, x);
        Assert.Equal(copy, x);
    }

    [Fact]
    public void Parameter_changes_do_not_click()
    {
        // Steady 200 Hz tone, then move every parameter at once: no sample jumps beyond the signal's own slope.
        var reverb = new Reverb(Rate) { Mix = 0.2f, RoomSize = 0.3f, PreDelayMs = 0 };
        var x = Signals.Sine(Rate, 200, Rate * 2, 0.3);
        int half = Rate;
        for (int i = 0; i < Rate * 2; i += 441)
        {
            if (i == half) { reverb.Mix = 0.8f; reverb.RoomSize = 0.9f; reverb.Damping = 0.9f; reverb.PreDelayMs = 120; }
            reverb.Process(x.AsSpan(i * 2, 882), 441);
        }
        double MaxStep(int from, int to)
        {
            double max = 0;
            for (int i = from + 1; i < to; i++) max = Math.Max(max, Math.Abs(x[i * 2] - x[(i - 1) * 2]));
            return max;
        }
        double before = MaxStep(half / 2, half), after = MaxStep(half, half + Rate / 4);
        output.WriteLine($"max step before {before:F4}, after {after:F4}");
        Assert.True(after < before * 2, "click on parameter change");
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var reverb = new Reverb(Rate) { Mix = 0.4f };
        var x = Signals.Noise(4096, 0.3);
        long bytes = Signals.Allocated(() =>
        {
            for (int i = 0; i < 8; i++) reverb.Process(x, 4096);
            reverb.PreDelayMs = reverb.PreDelayMs == 20 ? 60 : 20;
            reverb.Enabled = !reverb.Enabled;
            for (int i = 0; i < 8; i++) reverb.Process(x, 4096);
        });
        Assert.Equal(0, bytes);
    }
}
