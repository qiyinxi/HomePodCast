using System.Diagnostics;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class StemTests
{
    [Fact]
    public void Missing_and_overwritten_results_never_alias_another_frame()
    {
        var timeline = new StemTimeline(8);
        Assert.False(timeline.Read(0, out _, out _));
        timeline.Write(0, [0.2f, 0.3f]);
        Assert.True(timeline.Read(0, out float left, out float right));
        Assert.Equal(0.2f, left); Assert.Equal(0.3f, right);
        timeline.Write(8, [0.8f, 0.9f]);
        Assert.False(timeline.Read(0, out _, out _));
        Assert.True(timeline.Read(8, out left, out _)); Assert.Equal(0.8f, left);
    }

    [Fact]
    public void Invalid_preroll_is_discarded_and_channels_stay_in_order()
    {
        var timeline = new StemTimeline();
        timeline.Write(-1, [9f, 9f, 0.1f, -0.2f]);
        Assert.False(timeline.Read(-1, out _, out _));
        Assert.True(timeline.Read(0, out float l, out float r));
        Assert.Equal(0.1f, l); Assert.Equal(-0.2f, r);
    }

    [Theory]
    [InlineData(0.3f, 0.1f)]
    [InlineData(-0.5f, 0.25f)]
    public void Complementary_stems_reconstruct_mixture_and_equal_gains_need_no_estimate(float mixture, float vocal)
    {
        float v = StemMix.Sample(mixture, vocal, 1, 0), a = StemMix.Sample(mixture, vocal, 0, 1);
        Assert.Equal(mixture, v + a, 5);
        Assert.Equal(mixture, StemMix.Sample(mixture, float.NaN, 1, 1));
    }

    [Fact]
    public void Disabled_effect_is_exact_pass_through_and_allocates_nothing()
    {
        using var mixer = new LiveStemMixer(new StemSettings());
        float[] input = [0.2f, -0.3f, 0.1f, 0.4f]; var expected = input.ToArray();
        mixer.Process(input, 2);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) mixer.Process(input, 2);
        Assert.Equal(start, GC.GetAllocatedBytesForCurrentThread()); Assert.Equal(expected, input);
        Assert.False(mixer.Enabled); Assert.False(mixer.MonitorOn);
    }

    [Fact]
    public void Optional_module_and_start_switches_are_not_required_or_persisted()
    {
        Assert.False(LiveStemMixer.ModuleExists(null)); Assert.False(LiveStemMixer.ModuleExists("missing-folder"));
        Assert.Throws<FileNotFoundException>(() => new LiveStemMixer(new StemSettings()).SetEnabled(true));
        string json = System.Text.Json.JsonSerializer.Serialize(new StemSettings());
        Assert.DoesNotContain("Enabled", json); Assert.DoesNotContain("MonitorOn", json);
        Assert.Equal(186, LiveStemMixer.ExtraLatencyMs);
    }

    [Fact]
    public void Module_failure_mutes_isolated_output_and_stops_worker_without_blocking_audio()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hpc-stem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Exists, but cannot execute: deterministic fail-closed exercise without any model download.
            File.WriteAllText(Path.Combine(dir, "HomePodCast.StemWorker.exe"), "invalid executable");
            File.WriteAllText(Path.Combine(dir, "vocals.onnx"), "invalid model");
            using var mixer = new LiveStemMixer(new StemSettings { ModuleDirectory = dir });
            mixer.SetEnabled(true);
            Assert.True(SpinWait.SpinUntil(() => mixer.Error != null, 5000));
            var pcm = new float[704];
            long started = Stopwatch.GetTimestamp();
            for (int block = 0; block < 100; block++) { Array.Fill(pcm, 0.3f); mixer.Process(pcm, 352); }
            Assert.All(pcm, value => Assert.Equal(0f, value));
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
            Assert.True(mixer.MissedFrames > 0);
            mixer.SetEnabled(false); Array.Fill(pcm, 0.3f); mixer.Process(pcm, 352);
            Assert.All(pcm, value => Assert.Equal(0.3f, value));
        }
        finally { Directory.Delete(dir, true); }
    }
}
