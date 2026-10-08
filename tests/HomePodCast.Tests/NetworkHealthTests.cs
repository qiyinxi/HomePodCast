using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// 首页's network status: the 10-minute window, episodes, the margin, which Wi-Fi hop is to blame, and the suggested
/// latency. All times are fake milliseconds; nothing here waits.
/// </summary>
public class NetworkHealthTests
{
    private const int A2R = 85; // the HomePod 2's own processing time

    private const long S = 1000; // one second in ms

    private static NetworkHealth Spikes(params (long At, long Rtt)[] spikes)
    {
        var h = new NetworkHealth();
        foreach (var (at, rtt) in spikes) h.AddPing(PingTarget.Speaker, at, rtt);
        return h;
    }

    [Fact]
    public void A_quiet_connection_is_stable()
    {
        var v = new NetworkHealth().Assess(10 * 60 * S, 120, A2R, routerWatched: true);
        Assert.Equal(0, v.Episodes);
        Assert.Equal(0, v.Threats);
        Assert.False(v.Threatened);
        Assert.Null(v.SuggestedMs);
        Assert.Equal(15, v.MarginMs); // 120 − 85 − 20
    }

    [Fact]
    public void One_spike_over_the_margin_is_counted_but_is_not_yet_worth_a_hint()
    {
        var v = Spikes((100 * S, 48)).Assess(200 * S, 120, A2R, true);
        Assert.Equal(1, v.Episodes);
        Assert.Equal(1, v.Threats);
        Assert.False(v.Threatened);
        Assert.Null(v.SuggestedMs);
    }

    [Fact]
    public void Spikes_over_the_margin_suggest_15_ms_more_on_the_speakers_hop()
    {
        // The example from the brief: 推荐 120 ms, 85 ms in the speaker, three spikes in 10 minutes.
        var v = Spikes((60 * S, 48), (250 * S, 41), (480 * S, 35)).Assess(500 * S, 120, A2R, true);
        Assert.Equal(3, v.Episodes);
        Assert.Equal(3, v.Threats);
        Assert.True(v.Threatened);
        Assert.Equal(NetworkHop.Speaker, v.Hop);
        Assert.Equal(135, v.SuggestedMs);
    }

    [Fact]
    public void Spikes_less_than_a_second_apart_are_one_episode()
    {
        // 100 ms pings through one bad second: one stutter at most, not five.
        var h = Spikes((10_000, 40), (10_100, 60), (10_700, 35), (11_600, 33), (12_500, 31));
        var v = h.Assess(20 * S, 120, A2R, true);
        Assert.Equal(1, v.Episodes);
        Assert.False(v.Threatened);

        h.AddPing(PingTarget.Speaker, 13_501, 31); // 1001 ms after the last one: a new episode
        v = h.Assess(20 * S, 120, A2R, true);
        Assert.Equal(2, v.Episodes);
        Assert.True(v.Threatened);
    }

    [Fact]
    public void Spikes_within_the_margin_are_jitter_but_no_threat()
    {
        // 影视 200 ms leaves 95 ms: Wi-Fi spikes of 60 ms are absorbed.
        var v = Spikes((10 * S, 60), (100 * S, 95), (200 * S, 70)).Assess(300 * S, 200, A2R, true);
        Assert.Equal(3, v.Episodes);
        Assert.Equal(0, v.Threats);
        Assert.False(v.Threatened);
        Assert.Null(v.SuggestedMs);

        v = Spikes((10 * S, 96), (100 * S, 120)).Assess(300 * S, 190, A2R, true); // margin 85
        Assert.True(v.Threatened);
    }

    [Fact]
    public void A_lost_ping_is_over_any_margin()
    {
        var v = Spikes((10 * S, -1), (100 * S, -1)).Assess(300 * S, 300, A2R, true);
        Assert.Equal(2, v.Threats);
        Assert.True(v.Threatened);
        Assert.Null(v.SuggestedMs); // 300 is past the cap: the hint, but no suggestion

        v = Spikes((10 * S, 40), (10_300, -1), (100 * S, 32)).Assess(300 * S, 140, A2R, true); // margin 35
        Assert.Equal(2, v.Episodes);
        Assert.Equal(1, v.Threats); // the lost ping makes its episode a threat; 32 ms alone is within the margin
    }

    [Fact]
    public void Any_resend_miss_is_worth_a_hint_at_once()
    {
        var h = new NetworkHealth();
        h.AddResends(50 * S, requests: 1, misses: 1);
        var v = h.Assess(60 * S, 160, A2R, true);
        Assert.Equal(1, v.Episodes);
        Assert.True(v.Threatened);
        Assert.Equal(NetworkHop.Speaker, v.Hop); // the router was quiet
        Assert.Equal(175, v.SuggestedMs);     // a miss is never "small": + 15
    }

    [Fact]
    public void Resend_requests_threaten_only_a_margin_too_small_for_a_resend()
    {
        var h = new NetworkHealth();
        h.AddResends(50 * S, 1, 0);
        h.AddResends(400 * S, 2, 0);
        var tight = h.Assess(500 * S, 120, A2R, true); // margin 15 < 40
        Assert.Equal(2, tight.Episodes);
        Assert.Equal(2, tight.Threats);
        Assert.True(tight.Threatened);
        Assert.Equal(135, tight.SuggestedMs);

        var roomy = h.Assess(500 * S, 150, A2R, true); // margin 45: the copy arrives in time
        Assert.Equal(2, roomy.Episodes);
        Assert.Equal(0, roomy.Threats);
        Assert.False(roomy.Threatened);

        Assert.True(h.Assess(500 * S, 144, A2R, true).Threatened);  // margin 39
        Assert.False(h.Assess(500 * S, 145, A2R, true).Threatened); // margin 40
    }

    [Fact]
    public void A_resend_and_a_spike_in_the_same_second_are_one_episode()
    {
        var h = Spikes((100_000, 45));
        h.AddResends(100_250, 1, 0); // sampled a little later
        var v = h.Assess(200 * S, 120, A2R, true);
        Assert.Equal(1, v.Episodes);
        Assert.Equal(1, v.Threats);
    }

    [Fact]
    public void Zero_resend_deltas_are_not_events()
    {
        var h = new NetworkHealth();
        for (int i = 0; i < 100; i++) h.AddResends(i * S, 0, 0);
        Assert.Equal(0, h.Assess(100 * S, 120, A2R, true).Episodes);
    }

    [Fact]
    public void Events_older_than_ten_minutes_drop_out()
    {
        var h = Spikes((10 * S, 50), (20 * S, 50));
        Assert.True(h.Assess(10 * S + NetworkHealth.WindowMs - 1, 120, A2R, true).Threatened);

        var later = h.Assess(10 * S + NetworkHealth.WindowMs, 120, A2R, true); // the first one is exactly 10 minutes old
        Assert.Equal(1, later.Episodes);
        Assert.False(later.Threatened);

        Assert.Equal(0, h.Assess(20 * S + NetworkHealth.WindowMs, 120, A2R, true).Episodes);

        h.AddResends(30 * S, 0, 1);
        Assert.True(h.Assess(30 * S + NetworkHealth.WindowMs - 1, 120, A2R, true).Threatened);
        Assert.False(h.Assess(30 * S + NetworkHealth.WindowMs, 120, A2R, true).Threatened); // even a miss ages out
    }

    [Fact]
    public void A_router_spike_at_the_same_time_blames_the_pcs_wifi()
    {
        var h = Spikes((100_000, 60), (300_000, 70));
        h.AddPing(PingTarget.Router, 100_400, 45);
        h.AddPing(PingTarget.Router, 299_100, 50); // within a second before
        var v = h.Assess(400 * S, 120, A2R, true);
        Assert.True(v.Threatened);
        Assert.Equal(NetworkHop.Pc, v.Hop);
    }

    [Fact]
    public void A_router_spike_more_than_a_second_away_does_not_count()
    {
        var h = Spikes((100_000, 60), (300_000, 70));
        h.AddPing(PingTarget.Router, 101_001, 45);
        h.AddPing(PingTarget.Router, 298_999, 50);
        Assert.Equal(NetworkHop.Speaker, h.Assess(400 * S, 120, A2R, true).Hop);
    }

    [Fact]
    public void The_router_near_the_end_of_a_long_episode_still_counts()
    {
        var h = Spikes((100_000, 60), (100_800, 40), (101_600, 50), (300_000, 70));
        h.AddPing(PingTarget.Router, 102_500, 45); // 3.1 s after the episode began, 0.9 s after it ended
        h.AddPing(PingTarget.Router, 300_000, 45);
        Assert.Equal(NetworkHop.Pc, h.Assess(400 * S, 120, A2R, true).Hop);
    }

    [Fact]
    public void The_majority_of_threats_decides_the_hop_and_a_tie_blames_the_homepod()
    {
        var h = Spikes((100 * S, 60), (200 * S, 60), (300 * S, 60));
        h.AddPing(PingTarget.Router, 100 * S, 40);
        Assert.Equal(NetworkHop.Speaker, h.Assess(400 * S, 120, A2R, true).Hop); // 1 PC, 2 HomePod

        h.AddPing(PingTarget.Router, 200 * S, 40);
        Assert.Equal(NetworkHop.Pc, h.Assess(400 * S, 120, A2R, true).Hop); // 2 PC, 1 HomePod

        var tie = Spikes((100 * S, 60), (200 * S, 60));
        tie.AddPing(PingTarget.Router, 100 * S, 40);
        Assert.Equal(NetworkHop.Speaker, tie.Assess(400 * S, 120, A2R, true).Hop);
    }

    [Fact]
    public void Only_threats_are_attributed()
    {
        // Two router-backed spikes within the 影视 margin, two quiet-router ones beyond it: the HomePod's hop.
        var h = Spikes((100 * S, 50), (150 * S, 50), (200 * S, 120), (300 * S, -1));
        h.AddPing(PingTarget.Router, 100 * S, 40);
        h.AddPing(PingTarget.Router, 150 * S, 40);
        var v = h.Assess(400 * S, 200, A2R, true);
        Assert.Equal(4, v.Episodes);
        Assert.Equal(2, v.Threats);
        Assert.Equal(NetworkHop.Speaker, v.Hop);
    }

    [Fact]
    public void An_unwatched_router_leaves_the_hop_unknown()
    {
        var h = Spikes((100 * S, 60), (300 * S, 70));
        var v = h.Assess(400 * S, 120, A2R, routerWatched: false);
        Assert.True(v.Threatened);
        Assert.Equal(NetworkHop.Unknown, v.Hop);
        Assert.Equal(135, v.SuggestedMs);
    }

    [Fact]
    public void Router_spikes_alone_are_not_jitter()
    {
        // Routers answer pings to themselves at low priority; the audio never waits for that.
        var h = new NetworkHealth();
        for (int i = 0; i < 20; i++) h.AddPing(PingTarget.Router, i * 10 * S, 80);
        var v = h.Assess(300 * S, 120, A2R, true);
        Assert.Equal(0, v.Episodes);
        Assert.False(v.Threatened);
    }

    [Theory]
    // latency, worst spike, suggestion: + 10 when every threat was at most 10 ms over the margin, else + 15; up to 5; cap 200
    [InlineData(140, 40, 150)]  // margin 35: 5 over
    [InlineData(140, 45, 150)]  // 10 over
    [InlineData(140, 46, 155)]  // 11 over
    [InlineData(133, 35, 145)]  // margin 28: 7 over → 143 → 145
    [InlineData(107, 31, 125)]  // margin 2: 29 over → 122 → 125
    [InlineData(120, 31, 135)]  // 推荐: any reported spike (> 30 ms) is 16+ over
    [InlineData(190, 120, 200)] // 205 capped
    [InlineData(196, 120, 200)] // 215 capped: the cap is still a step up
    [InlineData(200, 120, null)] // at the cap: no suggestion, only the hint
    [InlineData(500, -1, null)]  // 音乐
    public void Suggestion_adds_10_or_15_ms_rounded_up_to_5_and_capped(int latency, int spike, int? expected)
    {
        var v = Spikes((100 * S, spike), (300 * S, spike)).Assess(400 * S, latency, A2R, true);
        Assert.True(v.Threatened);
        Assert.Equal(expected, v.SuggestedMs);
    }

    [Fact]
    public void One_large_threat_makes_the_step_15()
    {
        var v = Spikes((100 * S, 40), (200 * S, 40), (300 * S, 60)).Assess(400 * S, 140, A2R, true); // 5, 5 and 25 over
        Assert.Equal(155, v.SuggestedMs);
    }

    [Fact]
    public void A_slower_speaker_leaves_less_margin()
    {
        // The same spikes at the same latency: absorbed with 70 ms in the speaker, a threat with 95.
        var h = Spikes((100 * S, 31), (300 * S, 31));
        Assert.False(h.Assess(400 * S, 140, 70, true).Threatened); // margin 50
        Assert.True(h.Assess(400 * S, 140, 95, true).Threatened);  // margin 25
    }

    [Fact]
    public void Reset_starts_a_new_session_with_nothing_in_it()
    {
        var h = Spikes((100 * S, 60), (300 * S, 70));
        h.AddResends(200 * S, 3, 1);
        h.AddPing(PingTarget.Router, 100 * S, 50);
        int session = h.Session;
        h.Reset();
        Assert.Equal(session + 1, h.Session);
        var v = h.Assess(400 * S, 120, A2R, true);
        Assert.Equal(0, v.Episodes);
        Assert.False(v.Threatened);
    }

    [Fact]
    public void Rounding_up_to_5()
    {
        Assert.Equal(135, NetworkHealth.RoundUpTo5(135));
        Assert.Equal(140, NetworkHealth.RoundUpTo5(136));
        Assert.Equal(140, NetworkHealth.RoundUpTo5(139));
        Assert.Equal(0, NetworkHealth.RoundUpTo5(0));
    }

    [Fact]
    public async Task Threads_can_add_while_the_ui_assesses()
    {
        var h = new NetworkHealth();
        var adders = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                long at = (i * 4 + t) * 2 * S; // every 2 s across all threads: 2000 separate episodes
                if (t == 3) h.AddResends(at, 1, 0);
                else h.AddPing(t == 2 ? PingTarget.Router : PingTarget.Speaker, at, 50);
            }
        })).ToArray();
        var assessor = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) _ = h.Assess(4000 * S, 120, A2R, true);
        });
        await Task.WhenAll([.. adders, assessor]);

        // The window is 3400–4000 s: events k·2 s for k = 1701…1999 (299), less the router thread's (k % 4 == 2: 75).
        var v = h.Assess(4000 * S, 120, A2R, true);
        Assert.Equal(224, v.Episodes);
    }
}

/// <summary>StreamController's feed: the current connection's resend counters go into Health, and a new connection starts over.</summary>
public class NetworkHealthFeedTests
{
    [Fact]
    public async Task Resend_misses_of_the_current_connection_reach_the_status_and_a_new_connection_starts_over()
    {
        using var c = new StreamController { ArrivalToRenderMs = 85 };
        using var rx = new FakeReceiver();
        var fifo = new AudioFifo(44100, targetMs: 20, capMs: 1000);
        using var sender = new RtpSender([rx.Target(firstSeq: 1000)], 4630, fifo);
        sender.Start(MediaClock.Now + MediaClock.FromMs(10));
        Assert.False(c.HealthWatched);

        NetworkVerdict Now() => c.Health.Assess(StreamController.HealthClockMs(), 120, 85, routerWatched: false);
        async Task<NetworkVerdict> Until(Func<NetworkVerdict, bool> done)
        {
            var v = Now();
            for (int i = 0; i < 200 && !done(v); i++) // up to 10 s: the counters are read every 250 ms
            {
                await Task.Delay(50);
                v = Now();
            }
            return v;
        }

        int first;
        using (c.FeedHealth(null, () => sender))
        {
            first = c.Health.Session;
            Assert.True(c.HealthWatched);
            Assert.False(c.RouterWatched);
            rx.RequestResend(5000, 1); // never sent: the speaker would hear a gap
            var v = await Until(v => v.Episodes > 0);
            Assert.Equal(1, v.Episodes);
            Assert.True(v.Threatened);
            Assert.Equal(NetworkHop.Unknown, v.Hop);
        }
        Assert.False(c.HealthWatched);

        using (c.FeedHealth(null, () => sender)) // the next connection (here with the same sender: its old counts don't carry over)
        {
            Assert.Equal(first + 1, c.Health.Session);
            await Task.Delay(600); // a couple of samples
            Assert.Equal(0, Now().Episodes);
            rx.RequestResend(6000, 2);
            var v = await Until(v => v.Episodes > 0);
            Assert.Equal(1, v.Episodes);
            Assert.True(v.Threatened);
        }
    }
}
