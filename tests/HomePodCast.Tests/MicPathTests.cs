using System.Diagnostics;
using HomePodCast.Audio;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

public class MicPathTests(ITestOutputHelper output)
{
    private static WaveFormat Pcm16(int rate, int channels) => new(rate, channels, 16, channels * 2, false);
    private static WaveFormat Float32(int rate, int channels) => new(rate, channels, 32, channels * 4, true);

    private static byte[] Interleave(WaveFormat fmt, int frames, Func<int, int, double> sample)
    {
        var data = new byte[frames * fmt.BlockAlign];
        int bytes = fmt.BitsPerSample / 8;
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < fmt.Channels; c++)
            {
                var s = data.AsSpan(i * fmt.BlockAlign + c * bytes, bytes);
                double v = sample(i, c);
                if (fmt.IsFloat) BitConverter.TryWriteBytes(s, (float)v);
                else if (bytes == 2) BitConverter.TryWriteBytes(s, (short)Math.Round(v * 32767));
                else if (bytes == 4) BitConverter.TryWriteBytes(s, (int)Math.Round(v * int.MaxValue));
                else if (bytes == 3)
                {
                    int x = (int)Math.Round(v * 8388607);
                    s[0] = (byte)x; s[1] = (byte)(x >> 8); s[2] = (byte)(x >> 16);
                }
            }
        return data;
    }

    public static TheoryData<int, int, bool> Formats => new()
    {
        { 16, 1, false }, // mono USB mic
        { 16, 2, false },
        { 24, 2, false }, // packed 24-bit
        { 32, 2, false },
        { 32, 4, true },  // array mic, float
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void First_channel_is_taken_from_every_common_format(int bits, int channels, bool isFloat)
    {
        var fmt = new WaveFormat(48000, channels, bits, channels * bits / 8, isFloat);
        // Channel 0 carries a ramp, the others carry a different constant that must not leak in.
        var data = Interleave(fmt, 100, (i, c) => c == 0 ? (i - 50) / 64.0 : -0.9);
        var mono = new float[100];
        MicPipeline.FirstChannel(data, 100, fmt, mono);
        for (int i = 0; i < 100; i++) Assert.InRange(mono[i], (i - 50) / 64.0 - 1e-4, (i - 50) / 64.0 + 1e-4);
    }

    [Fact]
    public void Mono_48k_mic_comes_out_as_identical_44k1_stereo_channels()
    {
        var fmt = Pcm16(48000, 1);
        var pipeline = new MicPipeline(fmt, 44100);
        var tap = new AudioTap(44100, 44100, targetMs: 0, capMs: 1000);
        const int packet = 480, packets = 90;   // 10 ms packets, 0.9 s (the tap holds 1 s)
        for (int p = 0; p < packets; p++)
        {
            var data = Interleave(fmt, packet, (i, _) => 0.25 * Math.Sin(2 * Math.PI * 1000 * (p * packet + i) / 48000.0));
            pipeline.Process(data, packet, silent: false, gainDb: 6.0206f, gate: false, gateDb: -50, [], [tap]);
        }

        var outBuf = new float[44100 * 2];
        int frames = tap.Read(outBuf);
        int expected = packets * packet * 44100 / 48000;
        output.WriteLine($"{packets * packet} frames at 48 kHz -> {frames} at 44.1 kHz (ideal {expected})");
        // Two 64-tap resamplers in series (48 → 44.1 kHz, then the tap's drift control) each skip 31 frames at
        // the start and hold 32 back.
        Assert.InRange(frames, expected - 130, expected - 110);
        var steady = outBuf.AsSpan(4410 * 2, (frames - 4410) * 2);
        for (int i = 0; i < steady.Length; i += 2) Assert.Equal(steady[i], steady[i + 1]);
        Assert.InRange(Signals.Tone(steady, 44100, 1000), 0.495, 0.505);  // 0.25 × +6 dB
        Assert.InRange(Signals.Tone(steady, 44100, 1000, ch: 1), 0.495, 0.505);
    }

    [Fact]
    public void A_44k1_mic_is_passed_through_without_resampling()
    {
        var fmt = Float32(44100, 2);
        var pipeline = new MicPipeline(fmt, 44100);
        var tap = new AudioTap(44100, 44100, targetMs: 0, capMs: 1000);
        var data = Interleave(fmt, 441, (i, c) => c == 0 ? Math.Sin(i * 0.1) * 0.5 : 0.3);
        pipeline.Process(data, 441, false, 0, false, -50, [], [tap]);
        Assert.Equal(0, pipeline.ResamplerDelayMs);

        // The tap still steers clock drift with its own resampler; past its start-up the samples match.
        pipeline.Process(data, 441, false, 0, false, -50, [], [tap]);
        var outBuf = new float[882 * 2];
        int frames = tap.Read(outBuf);
        Assert.InRange(frames, 882 - 64, 882 - 60);
        for (int i = 100; i < frames - 1; i++)
        {
            Assert.Equal(outBuf[i * 2], outBuf[i * 2 + 1]);
            double want = Math.Sin(((i + 31) % 441) * 0.1) * 0.5;   // 64-tap kernel: 31 frames of delay
            Assert.InRange(outBuf[i * 2], want - 0.01, want + 0.01);
        }
    }

    [Fact]
    public void Noise_gate_silences_room_noise_and_lets_the_voice_through()
    {
        var gate = new NoiseGate(48000) { Enabled = true, ThresholdDb = -40 };
        var rng = new Random(3);
        var noise = Enumerable.Range(0, 72000).Select(_ => (float)(0.003 * (rng.NextDouble() * 2 - 1))).ToArray(); // ≈ -55 dBFS
        var original = (float[])noise.Clone();
        gate.Process(noise);
        Assert.False(gate.IsOpen);
        Assert.True(noise.Skip(28800).All(v => MathF.Abs(v) < 0.003f * 0.01f), "noise not 40 dB down after 0.6 s");
        Assert.True(noise.Skip(60000).All(v => v == 0), "noise not fully gated after 1.25 s");

        var voice = Enumerable.Range(0, 4800).Select(i => (float)(0.1 * Math.Sin(2 * Math.PI * 300 * i / 48000))).ToArray(); // -20 dBFS
        gate.Process(voice);
        Assert.True(gate.IsOpen);
        Assert.InRange(voice.Skip(2400).Max(), 0.099f, 0.1001f);   // fully open within 50 ms

        var off = new NoiseGate(48000) { Enabled = false };
        var quiet = (float[])original.Clone();
        off.Process(quiet);
        Assert.Equal(original, quiet);
    }

    [Fact]
    public void Pipeline_with_effects_and_taps_does_not_allocate()
    {
        var fmt = Float32(48000, 1);
        var pipeline = new MicPipeline(fmt, 44100);
        var eq = new Equalizer();
        eq.SetPreset(EqPreset.VocalBoost);
        var reverb = new Reverb { Mix = 0.3f };
        var homepod = new AudioTap(44100, 44100, 15, 60);
        var monitor = new AudioTap(44100, 48000, 5, 40);
        var data = Interleave(fmt, 144, (i, _) => Math.Sin(i * 0.05) * 0.2);  // 3 ms packets
        var read = new float[352 * 2];
        IStereoEffect[] effects = [eq, reverb];
        AudioTap[] taps = [homepod, monitor];
        long bytes = Signals.Allocated(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                pipeline.Process(data, 144, false, 3, true, -50, effects, taps);
                homepod.Read(read);
                monitor.Read(read.AsSpan(0, 288));
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Tap_starts_exactly_at_the_target_depth()
    {
        var tap = new AudioTap(44100, 44100, targetMs: 20, capMs: 80);
        var dest = new float[352 * 2];
        Assert.Equal(0, tap.Read(dest));                            // nothing yet: priming, silence
        tap.Write(Signals.Sine(44100, 440, 441 * 3));                // 30 ms arrives in one burst
        Assert.Equal(352, tap.Read(dest));
        Assert.Equal(tap.TargetFrames - 352, tap.Depth);             // the overshoot was dropped, not queued
        Assert.Equal(0, tap.Underruns);
    }

    [Fact]
    public void Tap_absorbs_clock_drift_without_dropouts()
    {
        // Producer: 441 frames every 10 ms of its clock. Consumer: the sender's 352-frame packets, its
        // clock 300 ppm fast. 200 simulated seconds.
        var tap = new AudioTap(44100, 44100, targetMs: 20, capMs: 80);
        long now = 1;
        tap.Clock = () => now;
        long tick = Stopwatch.Frequency / 1000;   // 1 ms
        var chunk = Signals.Sine(44100, 440, 441, 0.2);
        var dest = new float[352 * 2];
        double owed = 0;
        long underrunsAfterStart = -1;
        for (int ms = 0; ms < 200_000; ms++)
        {
            now += tick;
            if (ms % 10 == 0) tap.Write(chunk);
            owed += 44.1 * (1 + 300e-6);
            while (owed >= 352)
            {
                tap.Read(dest);
                owed -= 352;
            }
            if (ms == 2000) underrunsAfterStart = tap.Underruns;

        }
        double depthMs = tap.AverageDepth * 1000 / 44100;
        output.WriteLine($"drift {tap.DriftPpm:F0} ppm, depth {depthMs:F1} ms, underruns {tap.Underruns}, overflows {tap.Overflows}");
        Assert.Equal(underrunsAfterStart, tap.Underruns);
        Assert.Equal(0, tap.Overflows);
        Assert.InRange(tap.DriftPpm, -400, -200);  // ±60 ppm of jitter from sampling a sawtooth depth
        Assert.InRange(depthMs, 18, 22);
    }
}
