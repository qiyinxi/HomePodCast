using System.Text.Json;
using HomePodCast.UI;

namespace HomePodCast.Tests;

public class AvCalibrationTests
{
    private static readonly AvCalibrationContext Endpoint = new("aabbcc", "output-one", false);

    [Fact]
    public void Stores_a_residual_so_latency_and_FIFO_remain_live()
    {
        var cfg = new AppConfig { VideoDelayExtraMs = 36 };
        var routed = Endpoint with { PerAppRouting = true };
        int estimate = LatencyTuner.EstimatedSoundLagMs(120, cfg, 35, fifoTargetMs: 16);
        Assert.Equal(187, estimate);
        Assert.True(AvCalibrations.Apply(cfg, routed, measuredOffsetMs: 220, estimate));
        Assert.Equal(33, AvCalibrations.Get(cfg, routed)!.ResidualMs);
        Assert.Equal(220, LatencyTuner.SoundLagMs(120, cfg, 35, 16, routed));
        Assert.Equal(300, LatencyTuner.SoundLagMs(200, cfg, 35, 16, routed));
        Assert.Equal(310, LatencyTuner.SoundLagMs(200, cfg, 35, 26, routed));
    }

    [Fact]
    public void Replacement_uses_the_uncalibrated_estimate_instead_of_accumulating_corrections()
    {
        var cfg = new AppConfig();
        Assert.True(AvCalibrations.Apply(cfg, Endpoint, 175, 156));
        Assert.True(AvCalibrations.Apply(cfg, Endpoint, 180, 156));
        Assert.Equal(24, AvCalibrations.Get(cfg, Endpoint)!.ResidualMs);
        Assert.Single(cfg.SyncCalibrations);
    }

    [Fact]
    public void Measurements_are_independent_for_speaker_endpoint_routing_and_default_choice()
    {
        var cfg = new AppConfig();
        var contexts = new[]
        {
            Endpoint,
            Endpoint with { SpeakerId = "different-speaker" },
            Endpoint with { CaptureEndpointId = "different-output" },
            Endpoint with { PerAppRouting = true },
            Endpoint with { FollowsDefault = true },
        };
        for (int i = 0; i < contexts.Length; i++)
            Assert.True(AvCalibrations.Apply(cfg, contexts[i], 170 + i, 156));
        for (int i = 0; i < contexts.Length; i++)
            Assert.Equal(14 + i, AvCalibrations.Get(cfg, contexts[i])!.ResidualMs);
        Assert.Equal(5, cfg.SyncCalibrations.Count);
    }

    [Fact]
    public void No_available_path_never_reuses_the_last_measurement()
    {
        var cfg = new AppConfig { DeviceId = "aabbcc", CaptureDeviceId = "output-one" };
        AvCalibrations.Apply(cfg, Endpoint, 200, 156);
        Assert.Null(AvCalibrations.Get(cfg, null));
        Assert.Null(AvCalibrationContext.For(cfg, null, false));
        Assert.Equal(156, LatencyTuner.SoundLagMs(120, cfg, 0, context: null));
    }

    [Fact]
    public void Identity_is_case_insensitive_but_separator_safe()
    {
        Assert.Equal(Endpoint.Key, (Endpoint with { SpeakerId = "AABBCC", CaptureEndpointId = "OUTPUT-ONE" }).Key);
        Assert.NotEqual(new AvCalibrationContext("a.b", "c", false).Key,
            new AvCalibrationContext("a", "b.c", false).Key);
        var cfg = new AppConfig { DeviceId = "AA:BB:CC", CaptureDeviceId = "output-one" };
        Assert.Equal(Endpoint.Key, AvCalibrationContext.For(cfg, "output-one", false)!.Value.Key);
        cfg.DeviceId = "aa-bb-cc";
        Assert.Equal(Endpoint.Key, AvCalibrationContext.For(cfg, "OUTPUT-ONE", false)!.Value.Key);
    }

    [Fact]
    public void Group_membership_has_its_own_context()
    {
        var cfg = new AppConfig { DeviceId = "AA:BB:CC", CaptureDeviceId = "output-one" };
        var single = AvCalibrationContext.For(cfg, "output-one", false)!.Value;
        cfg.MultiRoomDeviceId = "DD:EE:FF";
        var group = AvCalibrationContext.For(cfg, "output-one", false)!.Value;
        Assert.NotEqual(single.Key, group.Key);
        cfg.MultiRoomDeviceId = "11:22:33";
        Assert.NotEqual(group.Key, AvCalibrationContext.For(cfg, "output-one", false)!.Value.Key);
    }

    [Fact]
    public void Reset_removes_only_the_requested_path_and_restores_the_estimate()
    {
        var cfg = new AppConfig();
        var other = Endpoint with { PerAppRouting = true };
        AvCalibrations.Apply(cfg, Endpoint, 190, 156);
        AvCalibrations.Apply(cfg, other, 230, 191);
        Assert.True(AvCalibrations.Reset(cfg, Endpoint));
        Assert.False(AvCalibrations.Reset(cfg, Endpoint));
        Assert.Null(AvCalibrations.Get(cfg, Endpoint));
        Assert.NotNull(AvCalibrations.Get(cfg, other));
        Assert.Equal(156, LatencyTuner.SoundLagMs(120, cfg, 0, context: Endpoint));
    }

    [Theory]
    [InlineData(-1, 156)]
    [InlineData(2001, 156)]
    [InlineData(175, -1)]
    [InlineData(175, 4001)]
    [InlineData(0, 3000)]
    public void Impossible_measurements_are_rejected_without_changing_existing_values(int measured, int estimate)
    {
        var cfg = new AppConfig();
        AvCalibrations.Apply(cfg, Endpoint, 175, 156);
        Assert.False(AvCalibrations.Apply(cfg, Endpoint, measured, estimate));
        Assert.Equal(19, AvCalibrations.Get(cfg, Endpoint)!.ResidualMs);
    }

    [Fact]
    public void Malformed_loaded_values_are_ignored_and_removed()
    {
        var cfg = new AppConfig();
        AvCalibrations.Apply(cfg, Endpoint, 175, 156);
        var valid = cfg.SyncCalibrations[Endpoint.Key];
        cfg.SyncCalibrations[Endpoint.Key] = valid with { ResidualMs = 999 };
        cfg.SyncCalibrations["wrong-key"] = valid;
        cfg.SyncCalibrations["null"] = null!;
        Assert.Null(AvCalibrations.Get(cfg, Endpoint));
        AvCalibrations.Normalize(cfg);
        Assert.Empty(cfg.SyncCalibrations);
        cfg.SyncCalibrations = null!;
        AvCalibrations.Normalize(cfg);
        Assert.Empty(cfg.SyncCalibrations);
    }

    [Fact]
    public void Older_configs_and_null_collections_remain_uncalibrated()
    {
        var older = JsonSerializer.Deserialize<AppConfig>("""{"DeviceId":"AA:BB:CC"}""")!;
        AvCalibrations.Normalize(older);
        Assert.Empty(older.SyncCalibrations);
        var missing = JsonSerializer.Deserialize<AppConfig>("""{"SyncCalibrations":null}""")!;
        AvCalibrations.Normalize(missing);
        Assert.Empty(missing.SyncCalibrations);
    }

    [Fact]
    public void Calibration_survives_the_real_config_save_load_path()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"homepodcast-calibration-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "config.json");
        try
        {
            var cfg = new AppConfig();
            AvCalibrations.Apply(cfg, Endpoint, 175, 156);
            cfg.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(19, AvCalibrations.Get(loaded, Endpoint)!.ResidualMs);
            Assert.Equal(175, LatencyTuner.SoundLagMs(120, loaded, 0, context: Endpoint));
            loaded.SyncCalibrations[Endpoint.Key] = loaded.SyncCalibrations[Endpoint.Key] with { ResidualMs = -999 };
            loaded.Save(path);
            Assert.Empty(AppConfig.Load(path).SyncCalibrations);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_fifo_or_delay_values_never_produce_an_invalid_compensation()
    {
        var cfg = new AppConfig();
        Assert.Equal(156, LatencyTuner.SoundLagMs(120, cfg, 0, double.NaN));
        Assert.Equal(156, LatencyTuner.SoundLagMs(120, cfg, 0, double.PositiveInfinity));
        cfg.VideoDelayExtraMs = int.MaxValue;
        Assert.Equal(AvCalibrations.MaxEstimatedMs, LatencyTuner.SoundLagMs(int.MaxValue, cfg, int.MaxValue));
        cfg.VideoDelayExtraMs = int.MinValue;
        Assert.Equal(0, LatencyTuner.SoundLagMs(int.MinValue, cfg, int.MinValue));
    }
    [Fact]
    public void A_snapshot_is_current_only_on_the_same_path_latency_capture_extra_and_fifo_target()
    {
        var taken = new AvCalibrationSnapshot(Endpoint, 120, 0, 16);
        Assert.True(taken.SamePathAs(taken));
        Assert.True(taken.SamePathAs(taken with { FifoTargetMs = 16.0004 }));
        Assert.True(taken.SamePathAs(taken with { Context = Endpoint with { SpeakerId = "AABBCC" } })); // the same key
        Assert.False(taken.SamePathAs(null)); // not streaming now
        Assert.False(taken.SamePathAs(taken with { Context = Endpoint with { SpeakerId = "another-speaker" } }));
        Assert.False(taken.SamePathAs(taken with { Context = Endpoint with { CaptureEndpointId = "output-two" } }));
        Assert.False(taken.SamePathAs(taken with { Context = Endpoint with { PerAppRouting = true } }));
        Assert.False(taken.SamePathAs(taken with { EffectiveLatencyMs = 121 }));
        Assert.False(taken.SamePathAs(taken with { CaptureExtraMs = 35 }));
        Assert.False(taken.SamePathAs(taken with { FifoTargetMs = 24 })); // a dropout raised the FIFO target
        var nowhere = new AvCalibrationSnapshot(default, 120, 0, 16);
        Assert.False(nowhere.SamePathAs(nowhere));
    }

    [Fact]
    public void The_sync_test_value_counts_only_after_a_click_on_a_path_that_never_changed()
    {
        bool current = true;
        var gate = new SyncMeasurementGate(() => current);
        Assert.False(gate.CanUse); // no click yet
        gate.ClickPlayed();
        Assert.True(gate.CanUse);
        current = false;
        Assert.False(gate.CanUse);
        current = true; // back to the same path: the measurement still spans the change
        Assert.False(gate.CanUse);
        Assert.True(gate.PathChanged);
    }

    [Fact]
    public void A_test_sound_that_failed_to_play_never_counts()
    {
        var gate = new SyncMeasurementGate(() => true);
        gate.ClickPlayed();
        gate.Failed();
        Assert.False(gate.CanUse);
        gate.ClickPlayed();
        Assert.False(gate.CanUse);
    }

    [Fact]
    public void A_prediction_cut_off_at_the_end_of_the_slider_counts_only_once_the_slider_moved()
    {
        var gate = new SyncMeasurementGate(() => true, startClamped: true);
        gate.ClickPlayed();
        Assert.False(gate.CanUse); // Enter = 应用 would save the slider's end as the measurement
        gate.SliderMoved();
        Assert.True(gate.CanUse);
    }

    [Theory]
    [InlineData(-5, 600)]
    [InlineData(0, 600)]
    [InlineData(156, 600)]
    [InlineData(400, 600)]
    [InlineData(401, 700)]
    [InlineData(571, 800)] // 音乐: 500 ms + the speaker and PC share
    [InlineData(1900, 2000)]
    [InlineData(5000, AvCalibrations.MaxMeasuredMs)]
    public void The_sync_slider_reaches_past_the_prediction(int predictedMs, int maximum) =>
        Assert.Equal(maximum, SyncTestForm.SliderMaximum(predictedMs));

    [Fact]
    public void Every_prediction_that_can_be_saved_starts_inside_the_slider()
    {
        for (int ms = 0; ms <= AvCalibrations.MaxMeasuredMs; ms++)
            Assert.InRange(ms, 0, SyncTestForm.SliderMaximum(ms));
    }

    [Fact]
    public void A_calibration_that_was_not_saved_says_why()
    {
        Assert.Null(TrayApp.SyncCalibrationMessage(SyncCalibrationOutcome.Applied));
        var messages = new[] { SyncCalibrationOutcome.NoTestSound, SyncCalibrationOutcome.PathChanged, SyncCalibrationOutcome.OutOfRange }
            .Select(TrayApp.SyncCalibrationMessage).ToList();
        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m)));
        Assert.Equal(messages.Count, messages.Distinct().Count()); // a changed path no longer reads "no test sound detected"
    }

    [Fact]
    public void A_negative_residual_is_applied_and_compensation_never_goes_below_zero()
    {
        var cfg = new AppConfig();
        Assert.True(AvCalibrations.Apply(cfg, Endpoint, 120, 156));
        Assert.Equal(-36, AvCalibrations.Get(cfg, Endpoint)!.ResidualMs);
        Assert.Equal(120, LatencyTuner.SoundLagMs(120, cfg, 0, context: Endpoint));
        Assert.Equal(200, LatencyTuner.SoundLagMs(200, cfg, 0, context: Endpoint));
        Assert.True(AvCalibrations.Apply(cfg, Endpoint, 0, 156));
        Assert.Equal(0, LatencyTuner.SoundLagMs(100, cfg, 0, context: Endpoint));
    }
}
