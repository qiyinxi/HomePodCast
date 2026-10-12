using System.Diagnostics;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public sealed class StemModuleFact : FactAttribute
{
    public StemModuleFact()
    {
        if (!LiveStemMixer.ModuleExists(Environment.GetEnvironmentVariable("HOMEPODCAST_STEM_MODULE")))
            Skip = "Optional model integration: set HOMEPODCAST_STEM_MODULE to an installed module (no downloads).";
    }
}

public class StemModuleTests
{
    [StemModuleFact]
    public void Real_module_keeps_up_with_packet_cadence_and_never_blocks_the_sender()
    {
        var settings = new StemSettings { ModuleDirectory = Environment.GetEnvironmentVariable("HOMEPODCAST_STEM_MODULE"),
            HomePodVocals = 100, HomePodAccompaniment = 0 };
        using var mixer = new LiveStemMixer(settings);
        mixer.SetEnabled(true);
        Assert.True(SpinWait.SpinUntil(() => mixer.Ready || mixer.Error != null, TimeSpan.FromSeconds(20)), "Model load timed out");
        Assert.Null(mixer.Error); Assert.True(mixer.Ready);
        var pcm = new float[704];
        var clock = Stopwatch.StartNew();
        double maxCallMs = 0, energy = 0;
        const int packets = 750; // about six seconds, ~2000 real stateful model hops
        for (int block = 0; block < packets; block++)
        {
            for (int f = 0; f < 352; f++)
            {
                // A non-silent, two-channel signal with distinct frequencies exercises channel order and state.
                pcm[f * 2] = (float)(0.2 * Math.Sin((block * 352 + f) * 0.07));
                pcm[f * 2 + 1] = (float)(0.15 * Math.Sin((block * 352 + f) * 0.09));
            }
            long start = Stopwatch.GetTimestamp(); mixer.Process(pcm, 352);
            maxCallMs = Math.Max(maxCallMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            foreach (float value in pcm) { Assert.True(float.IsFinite(value)); energy += value * value; }
            double due = (block + 1) * 352 * 1000.0 / 44100;
            while (clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(1);
        }
        Assert.Null(mixer.Error);
        Assert.True(energy > 0.001, "The real model never produced audible output");
        Assert.True(mixer.DroppedBlocks <= 4, $"Inference cannot sustain realtime: {mixer.DroppedBlocks} dropped blocks");
        Assert.True(mixer.MissedFrames < 4410, $"Model missed more than 100 ms: {mixer.MissedFrames} frames");
        Assert.True(maxCallMs < 20, $"Audio thread call took {maxCallMs:F2} ms");
        // Disable restores the low-latency original synchronously, and kills the child process off the audio path.
        mixer.SetEnabled(false); Array.Fill(pcm, 0.3f); mixer.Process(pcm, 352);
        Assert.All(pcm, v => Assert.Equal(0.3f, v));
    }
}
