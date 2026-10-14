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

/// <summary>
/// The real module (CI job stem-module, .github/workflows/build.yml): ONNX in its own process, through the pipes. Only
/// buffers go in and out; nothing is played.
/// </summary>
public class StemModuleTests
{
    private static string Module => Environment.GetEnvironmentVariable("HOMEPODCAST_STEM_MODULE")!;

    [StemModuleFact]
    public void Stop_during_pipe_inference_exits_the_dedicated_worker_without_an_unhandled_exception()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var mixer = new LiveStemMixer(new StemSettings { ModuleDirectory = Module });
            mixer.SetEnabled(true);
            Assert.True(SpinWait.SpinUntil(() => mixer.Ready || mixer.Error != StemError.None, TimeSpan.FromSeconds(20)));
            Assert.Equal(StemError.None, mixer.Error);
            var thread = mixer.WorkerThread!;
            var pcm = new float[2048]; Array.Fill(pcm, 0.2f);
            for (int i = 0; i < 3; i++) mixer.Process(pcm, 1024);
            Thread.Sleep(1); // child is receiving/processing queued blocks; stop closes its pipes
            mixer.SetEnabled(false);
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "Stopped worker did not release its child/pipes");
            Assert.False(mixer.Enabled);
            Assert.Equal(StemError.None, mixer.Error); // stopping is not a failure
        }
    }

    [StemModuleFact]
    public void Real_module_keeps_up_with_packet_cadence_and_never_blocks_the_sender()
    {
        var settings = new StemSettings { ModuleDirectory = Module, HomePodVocals = 100, HomePodAccompaniment = 0 };
        using var mixer = new LiveStemMixer(settings);
        mixer.SetEnabled(true);
        Assert.True(SpinWait.SpinUntil(() => mixer.Ready || mixer.Error != StemError.None, TimeSpan.FromSeconds(20)), "Model load timed out");
        Assert.Equal(StemError.None, mixer.Error); Assert.True(mixer.Ready);
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
        Assert.Equal(StemError.None, mixer.Error);
        Assert.True(energy > 0.001, "The real model never produced audible output");
        Assert.True(mixer.DroppedBlocks <= 4, $"Inference cannot sustain realtime: {mixer.DroppedBlocks} dropped blocks");
        Assert.True(mixer.MissedFrames < 4410, $"Model missed more than 100 ms: {mixer.MissedFrames} frames");
        Assert.True(maxCallMs < 20, $"Audio thread call took {maxCallMs:F2} ms");
        // Off: the delayed path fades out and the live original fades in (20 ms), and the child is killed off the audio path.
        mixer.SetEnabled(false);
        for (int i = 0; i < 4; i++) { Array.Fill(pcm, 0.3f); mixer.Process(pcm, 352); }
        Assert.All(pcm, v => Assert.Equal(0.3f, v));
    }

    [StemModuleFact]
    public void A_damaged_model_is_reported_as_such_for_a_reinstall()
    {
        string copy = Path.Combine(Path.GetTempPath(), "hpc-stem-damaged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            foreach (var file in Directory.GetFiles(Module)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            using (var model = File.OpenWrite(Path.Combine(copy, "vocals.onnx"))) model.WriteByte(0xFF); // one byte changed
            using var mixer = new LiveStemMixer(new StemSettings { ModuleDirectory = copy });
            mixer.SetEnabled(true);
            Assert.True(SpinWait.SpinUntil(() => mixer.Error != StemError.None, TimeSpan.FromSeconds(20)));
            Assert.Equal(StemError.ModelRejected, mixer.Error); // the worker's exit code 2
            Assert.Contains("SHA-256", mixer.ErrorDetail); // its stderr reaches the log
            mixer.SetEnabled(false);
        }
        finally { Directory.Delete(copy, true); }
    }
}
