using System.Diagnostics;
using HomePodCast.Audio;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// The adaptive FIFO's learned target outlives the app, per captured output device. 2026-11-26: it lived in memory only,
/// and each restart replayed a virtual sound card's dropouts (16 → 20 → 24 ms) until the target had grown again.
/// </summary>
public class FifoMemoryTests
{
    private const string Card = "{0.0.0.00000000}.{1111cccc-2222-3333-4444-5555dddd5555}";
    private const string Headphones = "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

    /// <summary>A controller (16 ms base) capturing <paramref name="device"/>'s current value, with what the config remembers.</summary>
    private static StreamController Controller(Func<string?> device, AppConfig config) => new(16)
    {
        FifoMemoryKey = device,
        RememberedFifoTargetMs = config.FifoLearnedTargetFor,
    };

    private static AppConfig Remembering(string device, int ms)
    {
        var config = new AppConfig();
        config.RememberFifoTarget(device, ms);
        return config;
    }

    private static int TargetMs(StreamController c) => (int)Math.Round(c.Fifo.TargetFrames * 1000.0 / c.Fifo.SampleRate);

    /// <summary>
    /// The capture delivers nothing for <paramref name="ms"/> while the sender reads, then what it missed comes at once
    /// (at least a packet): one dropout. Late audio, not a source that paused (AudioFifo.SourcePauseMarginMs).
    /// </summary>
    private static void Dropout(AudioFifo fifo, int ms)
    {
        long now = 0;
        fifo.Clock = () => now;
        fifo.Write(new float[fifo.TargetFrames * 2]);
        var dest = new float[352 * 2];
        while (fifo.Read(dest)) { }
        now += ms * Stopwatch.Frequency / 1000;
        fifo.Write(new float[Math.Max(352, ms * fifo.SampleRate / 1000) * 2]);
    }

    [Fact]
    public void A_connection_starts_from_the_remembered_target_once_its_ceiling_is_known()
    {
        using var c = Controller(() => Card, Remembering(Card, 50));
        Assert.Equal(16, TargetMs(c));                   // a new controller: the ceiling comes with the connection
        int margin = c.Fifo.CapFrames - c.Fifo.TargetFrames;

        c.FitFifoTo(100);                                // the maintainer's Apple TV: 100 ms, so up to 50 ms
        Assert.Equal(50, TargetMs(c));
        Assert.Equal(c.Fifo.TargetFrames + margin, c.Fifo.CapFrames);
    }

    [Theory]
    [InlineData(80, 100, 50)]   // more than this connection's ceiling (half its latency): the ceiling
    [InlineData(80, 200, 80)]
    [InlineData(80, 40, 30)]    // a low latency: the fixed 30 ms
    [InlineData(10, 100, 16)]   // less than the configured target (it was raised since): the configured one
    public void The_remembered_target_stays_between_the_configured_one_and_the_ceiling(int remembered, int latencyMs, int expected)
    {
        using var c = Controller(() => Card, Remembering(Card, remembered));
        c.FitFifoTo(latencyMs);
        Assert.Equal(expected, TargetMs(c));
    }

    [Fact]
    public void A_path_that_sets_no_ceiling_restores_within_the_one_the_fifo_has()
    {
        using var c = Controller(() => Card, Remembering(Card, 50));
        c.RestoreFifoTarget();                           // AirPlay 1, third-party and local outputs: 30 ms from the constructor
        Assert.Equal(30, TargetMs(c));
        c.FitFifoTo(100);                                // then a HomePod at 100 ms on the same controller
        Assert.Equal(50, TargetMs(c));
    }

    [Fact]
    public void A_restored_target_still_relaxes_and_a_reconnect_does_not_bring_it_back()
    {
        var config = Remembering(Card, 40);
        using var c = Controller(() => Card, config);
        c.FitFifoTo(100);
        c.Fifo.RelaxAfterReads = 10;
        c.Fifo.Write(new float[2000 * 2]);
        var dest = new float[352 * 2];
        for (int i = 0; i < 30; i++)
        {
            c.Fifo.Write(new float[352 * 2]);
            Assert.True(c.Fifo.Read(dest));
        }
        Assert.Equal(37, TargetMs(c));                   // 1 ms per ten clean reads here (10 minutes in the app)

        Assert.True(TrayApp.RememberFifoTargets(config, [c])); // the minute's save, or the session's end
        Assert.Equal(37, config.FifoLearnedTargetFor(Card));
        c.FitFifoTo(100);                                // the next connection (Apple TV idle release, a retry)
        Assert.Equal(37, TargetMs(c));
    }

    [Fact]
    public void Another_capture_device_does_not_inherit_the_target()
    {
        var config = Remembering(Card, 50);
        using var other = Controller(() => Headphones, config);
        other.FitFifoTo(100);
        Assert.Equal(16, TargetMs(other));               // nothing remembered for the headphones
        Assert.Equal((Headphones, 0), other.LearnedFifoTarget);

        // The same controller, the capture device switched between two connections.
        string device = Card;
        using var c = Controller(() => device, config);
        c.FitFifoTo(100);
        Assert.Equal(50, TargetMs(c));
        device = Headphones;
        Assert.Null(c.LearnedFifoTarget);                // switched while connected: whose dropouts they were is not known
        c.FitFifoTo(100);
        Assert.Equal(16, TargetMs(c));                   // not the sound card's 50 ms
        Assert.Equal((Headphones, 0), c.LearnedFifoTarget);

        config.RememberFifoTarget(Headphones, 22);
        device = Card.ToUpperInvariant();                // endpoint ids may differ in case between Windows APIs
        c.FitFifoTo(100);
        Assert.Equal(50, TargetMs(c));                   // back on the sound card: its own again
        device = Headphones;
        c.FitFifoTo(100);
        Assert.Equal(22, TargetMs(c));
    }

    [Fact]
    public void Nothing_is_restored_or_reported_without_a_known_capture_device()
    {
        var config = Remembering(Card, 50);
        using var none = Controller(() => null, config); // Windows has no output device
        none.FitFifoTo(100);
        Assert.Equal(16, TargetMs(none));
        Assert.Null(none.LearnedFifoTarget);

        using var plain = new StreamController(16);      // the command line's controllers remember nothing
        plain.FitFifoTo(100);
        Assert.Equal(16, TargetMs(plain));
        Assert.Null(plain.LearnedFifoTarget);
    }

    [Fact]
    public void What_dropouts_taught_is_remembered_for_the_device_and_the_next_start_begins_there()
    {
        var config = new AppConfig();
        using (var c = Controller(() => Card, config))
        {
            c.FitFifoTo(100);
            Assert.Equal((Card, 0), c.LearnedFifoTarget);
            Assert.False(TrayApp.RememberFifoTargets(config, [c])); // nothing learned: nothing to save
            Dropout(c.Fifo, 3);                          // 20:46:05 in the maintainer's log: 16 -> 20 ms
            Dropout(c.Fifo, 3);                          // 20:46:11: -> 24 ms
            Assert.Equal((Card, 24), c.LearnedFifoTarget);
            Assert.True(TrayApp.RememberFifoTargets(config, [c]));
            Assert.False(TrayApp.RememberFifoTargets(config, [c])); // unchanged: no second save
        }
        Assert.Equal(24, config.FifoLearnedTargetFor(Card));

        using var next = Controller(() => Card, config); // the app starts again
        next.FitFifoTo(100);
        Assert.Equal(24, TargetMs(next));
    }

    [Fact]
    public void Several_destinations_on_one_capture_device_remember_the_largest_target_and_a_relaxed_one_is_forgotten()
    {
        var config = new AppConfig();
        using var a = Controller(() => Card, config);
        using var b = Controller(() => Card, config);
        a.FitFifoTo(100);
        b.FitFifoTo(100);
        Dropout(a.Fifo, 3);
        Dropout(b.Fifo, 20);
        Assert.True(TrayApp.RememberFifoTargets(config, [a, b]));
        Assert.Equal(36, config.FifoLearnedTargetFor(Card));

        a.Fifo.ForgetLearnedTarget();                    // both back at the configured target
        b.Fifo.ForgetLearnedTarget();
        Assert.True(TrayApp.RememberFifoTargets(config, [a, b]));
        Assert.Null(config.FifoLearnedTargetFor(Card));
        Assert.Empty(config.FifoLearnedTargetMs);
    }

    [Fact]
    public void Remembering_replaces_the_dictionary_a_stream_thread_may_be_reading()
    {
        var config = new AppConfig();
        var before = config.FifoLearnedTargetMs;
        Assert.True(config.RememberFifoTarget(Card, 30));
        Assert.NotSame(before, config.FifoLearnedTargetMs);
        Assert.Empty(before);

        before = config.FifoLearnedTargetMs;
        Assert.False(config.RememberFifoTarget(Card.ToUpperInvariant(), 30)); // the same device, the same target
        Assert.Same(before, config.FifoLearnedTargetMs);
        Assert.False(config.RememberFifoTarget(Headphones, 0));               // nothing to forget
        Assert.True(config.RememberFifoTarget(Card, 500));                    // no connection's ceiling is above 100 ms
        Assert.Equal(100, config.FifoLearnedTargetFor(Card));
    }

    [Fact]
    public void The_remembered_targets_survive_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-fifo-{Guid.NewGuid():N}", "config.json");
        try
        {
            var config = Remembering(Card, 50);
            config.RememberFifoTarget(Headphones, 22);
            config.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(50, loaded.FifoLearnedTargetFor(Card.ToUpperInvariant()));
            Assert.Equal(22, loaded.FifoLearnedTargetFor(Headphones));
            Assert.Equal(AppConfig.DefaultFifoTargetMs, loaded.FifoTargetMs); // the configured target is not what is learned

            // A file from before the setting, and one edited by hand.
            File.WriteAllText(path, """{ "DeviceId": "AA:BB", "LatencyMs": 120 }""");
            Assert.Empty(AppConfig.Load(path).FifoLearnedTargetMs);
            File.WriteAllText(path, """{ "FifoLearnedTargetMs2": { "card": 5000, "": 40, "gone": -3 } }""");
            loaded = AppConfig.Load(path);
            Assert.Equal(100, loaded.FifoLearnedTargetFor("CARD"));
            Assert.Single(loaded.FifoLearnedTargetMs);
            File.WriteAllText(path, """{ "FifoLearnedTargetMs2": null }""");
            Assert.Empty(AppConfig.Load(path).FifoLearnedTargetMs);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Targets_that_1_5_0_beta_5_remembered_are_dropped_once()
    {
        // 2026-11-27: they were learned from dropouts that were none (the player stopping its stream at each track change
        // took the target to its ceiling), so the name they were stored under is no longer read.
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-fifo-{Guid.NewGuid():N}", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $$"""{ "DeviceId": "AA:BB", "LatencyMs": 250, "FifoLearnedTargetMs": { "{{Card}}": 50 } }""");
            var loaded = AppConfig.Load(path);
            Assert.Equal("AA:BB", loaded.DeviceId);                   // the file still loads, with everything else in it
            Assert.Equal(250, loaded.LatencyMs);
            Assert.Null(loaded.FifoLearnedTargetFor(Card));
            Assert.Empty(loaded.FifoLearnedTargetMs);

            using (var c = Controller(() => Card, loaded))
            {
                c.FitFifoTo(100);
                Assert.Equal(16, TargetMs(c));                        // starts from the configured target again
            }

            loaded.RememberFifoTarget(Card, 24);                      // what is learned from now on is kept, under the new name
            loaded.Save(path);
            string saved = File.ReadAllText(path);
            Assert.Contains("\"FifoLearnedTargetMs2\"", saved);
            Assert.DoesNotContain("\"FifoLearnedTargetMs\"", saved);  // the old values are gone from the file
            Assert.Equal(24, AppConfig.Load(path).FifoLearnedTargetFor(Card));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
