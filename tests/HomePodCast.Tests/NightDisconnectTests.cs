using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// 夜间自动断开: the window (also across midnight), what counts as idle, when it disconnects and reconnects, the sound
/// it listens for at the end of the send chain, the setting, and the status line it leaves.
/// </summary>
public class NightDisconnectTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(2), Short = TimeSpan.FromMinutes(3);

    private static AppConfig On(int from = 23, int until = 7, int idleMinutes = 15) => new()
    {
        NightDisconnect = true,
        NightDisconnectFrom = new TimeOnly(from, 0),
        NightDisconnectUntil = new TimeOnly(until, 0),
        NightDisconnectIdleMinutes = idleMinutes,
    };

    private static TimeOnly At(int hour, int minute = 0) => new(hour, minute);

    // ---------------------------------------------------------------- the window

    [Theory]
    [InlineData(23, 0, true)] // from is inclusive
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)] // until is exclusive
    [InlineData(12, 0, false)]
    [InlineData(22, 59, false)]
    public void A_window_across_midnight(int hour, int minute, bool inside)
    {
        Assert.Equal(inside, NightDisconnectPolicy.InWindow(At(23), At(7), At(hour, minute)));
    }

    [Theory]
    [InlineData(0, 59, false)]
    [InlineData(1, 0, true)]
    [InlineData(4, 59, true)]
    [InlineData(5, 0, false)]
    [InlineData(23, 0, false)]
    public void A_window_within_one_day(int hour, int minute, bool inside)
    {
        Assert.Equal(inside, NightDisconnectPolicy.InWindow(At(1), At(5), At(hour, minute)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(12)]
    public void The_same_start_and_end_is_no_window(int hour)
    {
        Assert.False(NightDisconnectPolicy.InWindow(At(23), At(23), At(hour)));
    }

    // ---------------------------------------------------------------- idle

    [Fact]
    public void Idle_is_since_the_later_of_input_and_sound()
    {
        Assert.Equal(Short, NightDisconnectPolicy.Idle(sinceInput: Long, sinceSound: Short)); // a film plays untouched
        Assert.Equal(Short, NightDisconnectPolicy.Idle(sinceInput: Short, sinceSound: Long)); // someone types in silence
        Assert.Equal(Long, NightDisconnectPolicy.Idle(sinceInput: Long, sinceSound: TimeSpan.MaxValue)); // nothing played this run
    }

    [Fact]
    public void Input_windows_cannot_report_counts_as_just_now()
    {
        Assert.Equal(TimeSpan.Zero, NightDisconnectPolicy.Idle(sinceInput: null, sinceSound: TimeSpan.MaxValue));
    }

    // ---------------------------------------------------------------- decisions

    [Fact]
    public void Disconnects_in_the_window_once_idle_long_enough()
    {
        var cfg = On(idleMinutes: 15);
        Assert.Equal(NightAction.Disconnect, NightDisconnectPolicy.Decide(cfg, At(1), wanted: true, endedByNight: false, TimeSpan.FromMinutes(15)));
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(cfg, At(1), wanted: true, endedByNight: false, TimeSpan.FromMinutes(14)));
    }

    [Fact]
    public void Idle_from_before_the_window_counts_at_its_start()
    {
        // Gone at 22:30 with nothing playing: at 23:00 the PC has been unused for 30 minutes already.
        Assert.Equal(NightAction.Disconnect, NightDisconnectPolicy.Decide(On(), At(23), wanted: true, endedByNight: false, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Never_outside_the_window_nor_when_off_nor_when_nothing_is_wanted()
    {
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(On(), At(12), wanted: true, endedByNight: false, Long));
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(new AppConfig(), At(1), wanted: true, endedByNight: false, Long));
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(On(), At(1), wanted: false, endedByNight: false, Long));
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(On(23, 23), At(1), wanted: true, endedByNight: false, Long));
    }

    [Fact]
    public void Off_by_default()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.NightDisconnect);
        Assert.Equal(At(23), cfg.NightDisconnectFrom);
        Assert.Equal(At(7), cfg.NightDisconnectUntil);
        Assert.Equal(15, cfg.NightDisconnectIdleMinutes);
    }

    [Fact]
    public void Reconnects_when_the_window_ends_only_what_it_ended()
    {
        var cfg = On();
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(cfg, At(6, 59), wanted: false, endedByNight: true, Long)); // still night
        Assert.Equal(NightAction.Reconnect, NightDisconnectPolicy.Decide(cfg, At(7), wanted: false, endedByNight: true, Long));
        Assert.Equal(NightAction.Reconnect, NightDisconnectPolicy.Decide(cfg, At(9, 30), wanted: false, endedByNight: true, Long)); // woken late
        // The user disconnected (or connected again) in between: nothing to restore.
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(cfg, At(7), wanted: false, endedByNight: false, Long));
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(cfg, At(7), wanted: true, endedByNight: true, Long));
    }

    [Fact]
    public void Turned_off_it_reconnects_nothing()
    {
        Assert.Equal(NightAction.None, NightDisconnectPolicy.Decide(new AppConfig(), At(8), wanted: false, endedByNight: true, Long));
    }

    [Fact]
    public void Every_idle_choice_and_time_choice_is_a_valid_setting()
    {
        Assert.All(NightDisconnectPolicy.IdleChoices, m => Assert.Equal(m, NightDisconnectPolicy.ClampIdleMinutes(m)));
        Assert.Contains(NightDisconnectPolicy.DefaultIdleMinutes, NightDisconnectPolicy.IdleChoices);
        Assert.Contains(NightDisconnectPolicy.DefaultFrom, NightDisconnectPolicy.TimeChoices);
        Assert.Contains(NightDisconnectPolicy.DefaultUntil, NightDisconnectPolicy.TimeChoices);
        Assert.Equal(24, NightDisconnectPolicy.TimeChoices.Distinct().Count());
    }

    [Fact]
    public void Times_show_as_24_hour_clock()
    {
        Assert.Equal("07:00", NightDisconnectPolicy.Format(At(7)));
        Assert.Equal("23:30", NightDisconnectPolicy.Format(At(23, 30)));
    }

    // ---------------------------------------------------------------- the setting

    [Fact]
    public void The_setting_survives_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-night-{Guid.NewGuid():N}", "config.json");
        try
        {
            On(from: 0, until: 6, idleMinutes: 45).Save(path);
            var loaded = AppConfig.Load(path);
            Assert.True(loaded.NightDisconnect);
            Assert.Equal(At(0), loaded.NightDisconnectFrom);
            Assert.Equal(At(6), loaded.NightDisconnectUntil);
            Assert.Equal(45, loaded.NightDisconnectIdleMinutes);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Old_configs_without_the_setting_have_it_off()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>("""{ "DeviceId": "AA:BB", "LatencyMs": 120 }""")!;
        Assert.False(cfg.NightDisconnect);
        Assert.Equal(15, cfg.NightDisconnectIdleMinutes);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(-5, 15)]
    [InlineData(1, 1)]
    [InlineData(100_000, 1440)]
    public void A_hand_edited_idle_time_is_kept_sensible(int written, int loaded)
    {
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-night-{Guid.NewGuid():N}", "config.json");
        try
        {
            new AppConfig { NightDisconnectIdleMinutes = written }.Save(path);
            Assert.Equal(loaded, AppConfig.Load(path).NightDisconnectIdleMinutes);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ---------------------------------------------------------------- the sound it listens for

    [Fact]
    public void Nothing_heard_yet_is_silent_for_ever()
    {
        Assert.Equal(TimeSpan.MaxValue, new SoundActivity(() => 1000).SilentFor(5000));
    }

    [Fact]
    public void A_packet_above_the_threshold_is_sound_one_below_is_not()
    {
        long now = 1000;
        var activity = new SoundActivity(() => now);
        var packet = new float[RtpSender.FramesPerPacket * 2];

        packet[101] = SoundActivity.Threshold / 2; // dithered silence
        activity.Process(packet, RtpSender.FramesPerPacket);
        Assert.Equal(TimeSpan.MaxValue, activity.SilentFor(now));

        packet[101] = -0.05f; // a quiet note, either polarity
        activity.Process(packet, RtpSender.FramesPerPacket);
        now = 61_000;
        Assert.Equal(TimeSpan.FromMinutes(1), activity.SilentFor(now));

        Array.Clear(packet);
        activity.Process(packet, RtpSender.FramesPerPacket); // zeros afterwards don't move it
        Assert.Equal(TimeSpan.FromMinutes(1), activity.SilentFor(now));
    }

    [Fact]
    public void Only_the_packets_frames_are_looked_at()
    {
        var activity = new SoundActivity(() => 1000);
        var buffer = new float[RtpSender.FramesPerPacket * 2];
        buffer[^1] = 0.5f; // past the 10 frames handed over
        activity.Process(buffer, 10);
        Assert.Equal(TimeSpan.MaxValue, activity.SilentFor(1000));
    }

    [Fact]
    public void It_measures_after_the_other_effects()
    {
        // Added last to the chain, it hears what the speaker plays: here an effect that mutes everything.
        var activity = new SoundActivity(() => 1000);
        var chain = new EffectChain(new Mute());
        chain.Add(activity);
        var packet = Enumerable.Repeat(0.5f, RtpSender.FramesPerPacket * 2).ToArray();
        chain.Process(packet, RtpSender.FramesPerPacket);
        Assert.Equal(TimeSpan.MaxValue, activity.SilentFor(1000));
    }

    private sealed class Mute : IAudioEffect
    {
        public void Process(Span<float> interleaved, int frames) => interleaved.Clear();
    }

    // ---------------------------------------------------------------- the status line

    [Fact]
    public void Stopping_with_a_reason_shows_it()
    {
        using var c = new StreamController();
        string status = TrayApp.NightDisconnectedText(At(7));
        Assert.Equal(L.F("夜间已自动断开，{0} 自动重新连接", "07:00"), status);
        c.Stop(status);
        Assert.Equal(StreamState.Idle, c.State);
        Assert.Equal(status, c.StatusText);
        c.Stop();
        Assert.Equal(L.T("未连接"), c.StatusText);
    }
}
