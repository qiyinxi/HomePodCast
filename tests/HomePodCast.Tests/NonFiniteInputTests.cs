using System.Runtime.InteropServices;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// 2026-10-27 review: one NaN or Inf from a driver, an APO or a virtual mic stayed in the state of the stateful
/// effects (EQ, reverb, gate) for good. Captured samples are cleaned where they enter; the effects have backstops.
/// </summary>
public class NonFiniteInputTests
{
    private static byte[] FloatPacket(int frames, Func<int, float> sample)
    {
        var data = new byte[frames * 4];
        for (int i = 0; i < frames; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), sample(i));
        return data;
    }

    [Fact]
    public void A_NaN_or_Inf_from_a_float_mic_never_reaches_the_effects()
    {
        var fmt = new WaveFormat(48000, 1, 32, 4, true);
        var pipeline = new MicPipeline(fmt, 44100);
        var eq = new Equalizer();
        eq.SetPreset(EqPreset.VocalBoost);
        var reverb = new Reverb { Mix = 0.3f };
        var tap = new AudioTap(44100, 44100, targetMs: 0, capMs: 1000);
        IAudioEffect[] effects = [eq, reverb];
        var bad = FloatPacket(480, i => i switch
        {
            10 => float.NaN,
            20 => float.PositiveInfinity,
            30 => 1e30f,
            _ => (float)(0.2 * Math.Sin(i * 0.05)),
        });
        pipeline.Process(bad, 480, false, 0, true, -50, effects, [tap]);
        Assert.Equal(SampleGuard.Limit, pipeline.Peak);
        Assert.True(float.IsFinite(pipeline.Rms));

        var good = FloatPacket(480, i => (float)(0.2 * Math.Sin(i * 0.05)));
        for (int p = 0; p < 50; p++) pipeline.Process(good, 480, false, 0, true, -50, effects, [tap]);
        var output = new float[44100 * 2];
        int frames = tap.Read(output);
        Assert.InRange(frames, 22000, 23000);
        Assert.All(output.AsSpan(0, frames * 2).ToArray(), v => Assert.True(float.IsFinite(v)));
        Assert.True(pipeline.Gate.IsOpen);
    }

    [Fact]
    public void Loopback_samples_are_made_finite_and_bounded_on_the_way_in()
    {
        // ToStereo converts every loopback packet: the endpoint, each app's process loopback, the mixer hub's inputs.
        var fmt = new WaveFormat(48000, 2, 32, 8, true);
        float[] packet = [0.5f, -0.25f, float.NaN, float.NegativeInfinity, 1e30f, -1e30f, float.PositiveInfinity, 1.5f];
        var stereo = new float[8];
        var pinned = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try { LoopbackCapture.ToStereo(pinned.AddrOfPinnedObject(), 4, fmt, stereo); }
        finally { pinned.Free(); }
        Assert.Equal([0.5f, -0.25f, 0, 0, SampleGuard.Limit, -SampleGuard.Limit, 0, 1.5f], stereo);
    }

    [Fact]
    public void A_NaN_that_reaches_the_reverb_is_dropped_with_the_tail()
    {
        var reverb = new Reverb(44100) { Mix = 0.3f, PreDelayMs = 20 };
        var x = Signals.Sine(44100, 440, 44100, 0.2);
        x[1000] = float.NaN;
        x[1001] = float.PositiveInfinity;
        Signals.Run(reverb, x);
        Assert.All(x.AsSpan(44100 / 5 * 2).ToArray(), v => Assert.True(float.IsFinite(v)));
        Assert.InRange(Signals.Tone(x.AsSpan(44100 / 2 * 2), 44100, 440), 0.1, 0.25); // still the voice and its reverb
    }

    [Fact]
    public void A_NaN_does_not_freeze_the_noise_gate()
    {
        var gate = new NoiseGate(48000) { Enabled = true, ThresholdDb = -40 };
        gate.Process(Enumerable.Repeat(0.001f, 48000).ToArray()); // -60 dBFS for 1 s: closed
        Assert.False(gate.IsOpen);
        gate.Process([float.NaN]);
        var voice = Enumerable.Range(0, 4800).Select(i => (float)(0.1 * Math.Sin(2 * Math.PI * 300 * i / 48000))).ToArray();
        gate.Process(voice);
        Assert.True(gate.IsOpen);
        Assert.InRange(voice.Skip(2400).Max(), 0.099f, 0.1001f);
    }
}
