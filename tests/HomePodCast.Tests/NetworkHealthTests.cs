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

    private static NetworkHealth Every100s(long rtt, int count = 3) =>
        Spikes(Enumerable.Range(1, count).Select(k => (k * 100 * S, rtt)).ToArray());

    [Fact]
    public void A_quiet_connection_is_stable()
    {
        var v = new NetworkHealth().Assess(10 * 60 * S, 120, A2R, routerWatched: true);
        Assert.Equal(0, v.Episodes);
        Assert.Equal(0, v.Threats);
        Assert.False(v.Threatened);
        Assert.Null(v.SuggestedMs);
        Assert.Equal(30, v.MarginMs); // 120 − 85 − 5
    }

    [Fact]
    public void Spikes_under_twice_the_margin_are_jitter_not_threats()
    {
        // Seen on the real machine 2026-10-08: spikes like these raised the first rule's hint while nothing could be heard.
        var v = Spikes((60 * S, 35), (250 * S, 48), (480 * S, 60)).Assess(500 * S, 120, A2R, true);
        Assert.Equal(3, v.Episodes);
        Assert.Equal(0, v.Threats);
        Assert.False(v.Threatened);
        Assert.Null(v.SuggestedMs);
    }

    [Fact]
    public void Two_big_spikes_are_counted_but_are_not_yet_worth_a_hint()
    {
        var v = Spikes((100 * S, 70), (200 * S, 80)).Assess(300 * S, 120, A2R, true);
        Assert.Equal(2, v.Episodes);
        Assert.Equal(2, v.Threats);
        Assert.False(v.Threatened);
        Assert.Null(v.SuggestedMs);
    }

    [Fact]
    public void Three_big_spikes_suggest_more_latency_on_the_speakers_hop()
    {
        // 推荐 120 ms, 85 ms in the speaker: round trips over 60 ms; 15, 30 and 5 over → + 15.
        var v = Spikes((60 * S, 75), (250 * S, 90), (480 * S, 65)).Assess(500 * S, 120, A2R, true);
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
        var h = Spikes((10_000, 70), (10_100, 90), (10_700, 40), (11_600, 35), (12_500, 31));
        var v = h.Assess(30 * S, 120, A2R, true);
        Assert.Equal(1, v.Episodes);
        Assert.Equal(1, v.Threats);

        h.AddPing(PingTarget.Speaker, 13_501, 70); // 1001 ms after the last one: a new episode
        h.AddPing(PingTarget.Speaker, 20_000, 70);
        v = h.Assess(30 * S, 120, A2R, true);
        Assert.Equal(3, v.Episodes);
        Assert.True(v.Threatened);
    }

    [Fact]
    public void A_lost_ping_alone_is_no_threat_but_two_in_one_episode_are()
    {
        var single = Spikes((10 * S, -1), (100 * S, -1), (200 * S, -1)).Assess(300 * S, 300, A2R, true);
        Assert.Equal(3, single.Episodes);
        Assert.Equal(0, single.Threats);

        var pairs = Spikes((10_000, -1), (10_100, -1), (100_000, -1), (100_100, -1), (200_000, -1), (200_100, -1))
            .Assess(300 * S, 300, A2R, true);
        Assert.Equal(3, pairs.Threats);
        Assert.True(pairs.Threatened);
        Assert.Null(pairs.SuggestedMs); // 300 is past the cap: the hint, but no suggestion
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
    public void A_resend_on_a_quiet_network_is_the_protocol_working()
    {
        var h = new NetworkHealth();
        h.AddResends(50 * S, 1, 0);
        h.AddResends(400 * S, 2, 0);
        h.AddResends(450 * S, 1, 0);
        var v = h.Assess(500 * S, 120, A2R, true);
        Assert.Equal(3, v.Episodes);
        Assert.Equal(0, v.Threats);
        Assert.False(v.Threatened);
    }

    [Fact]
    public void A_resend_while_the_network_is_slow_likely_came_too_late()
    {
        var h = new NetworkHealth();
        foreach (long at in new[] { 100_000L, 200_000, 300_000 })
        {
            h.AddPing(PingTarget.Speaker, at, 45); // over the 30 ms margin, under twice it
            h.AddResends(at + 250, 1, 0);          // sampled a little later
        }
        var v = h.Assess(400 * S, 120, A2R, true);
        Assert.Equal(3, v.Episodes);
        Assert.Equal(3, v.Threats);
        Assert.True(v.Threatened);
        Assert.Equal(135, v.SuggestedMs); // a late resend is never "small": + 15
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
        var h = Spikes((10 * S, 70), (20 * S, 70), (30 * S, 70));
        Assert.True(h.Assess(10 * S + NetworkHealth.WindowMs - 1, 120, A2R, true).Threatened);

        var later = h.Assess(10 * S + NetworkHealth.WindowMs, 120, A2R, true); // the first one is exactly 10 minutes old
        Assert.Equal(2, later.Episodes);
        Assert.False(later.Threatened);

        Assert.Equal(0, h.Assess(30 * S + NetworkHealth.WindowMs, 120, A2R, true).Episodes);

        h.AddResends(40 * S, 0, 1);
        Assert.True(h.Assess(40 * S + NetworkHealth.WindowMs - 1, 120, A2R, true).Threatened);
        Assert.False(h.Assess(40 * S + NetworkHealth.WindowMs, 120, A2R, true).Threatened); // even a miss ages out
    }

    [Fact]
    public void A_router_spike_at_the_same_time_blames_the_pcs_wifi()
    {
        var h = Spikes((100_000, 70), (200_000, 75), (300_000, 80));
        h.AddPing(PingTarget.Router, 100_400, 45);
        h.AddPing(PingTarget.Router, 199_100, 50); // within a second before
        var v = h.Assess(400 * S, 120, A2R, true);
        Assert.True(v.Threatened);
        Assert.Equal(NetworkHop.Pc, v.Hop);
    }

    [Fact]
    public void A_router_spike_more_than_a_second_away_does_not_count()
    {
        var h = Spikes((100_000, 70), (200_000, 75), (300_000, 80));
        h.AddPing(PingTarget.Router, 101_001, 45);
        h.AddPing(PingTarget.Router, 198_999, 50);
        Assert.Equal(NetworkHop.Speaker, h.Assess(400 * S, 120, A2R, true).Hop);
    }

    [Fact]
    public void The_router_near_the_end_of_a_long_episode_still_counts()
    {
        var h = Spikes((100_000, 70), (100_800, 40), (101_600, 50), (200_000, 70), (300_000, 70));
        h.AddPing(PingTarget.Router, 102_500, 45); // 2.5 s after the episode began, 0.9 s after it ended
        h.AddPing(PingTarget.Router, 300_000, 45);
        Assert.Equal(NetworkHop.Pc, h.Assess(400 * S, 120, A2R, true).Hop);
    }

    [Fact]
    public void The_majority_of_threats_decides_the_hop_and_a_tie_blames_the_homepod()
    {
        var h = Every100s(70);
        h.AddPing(PingTarget.Router, 100 * S, 40);
        Assert.Equal(NetworkHop.Speaker, h.Assess(400 * S, 120, A2R, true).Hop); // 1 PC, 2 HomePod

        h.AddPing(PingTarget.Router, 200 * S, 40);
        Assert.Equal(NetworkHop.Pc, h.Assess(400 * S, 120, A2R, true).Hop); // 2 PC, 1 HomePod

        var tie = Every100s(70, count: 4);
        tie.AddPing(PingTarget.Router, 100 * S, 40);
        tie.AddPing(PingTarget.Router, 200 * S, 40);
        Assert.Equal(NetworkHop.Speaker, tie.Assess(500 * S, 120, A2R, true).Hop);
    }

    [Fact]
    public void Only_threats_are_attributed()
    {
        // 影视 200 ms (margin 110): two router-backed spikes are absorbed; three quiet-router episodes are threats.
        var h = Spikes((100 * S, 150), (150 * S, 150), (200 * S, 250), (250 * S, 260), (300_000, -1), (300_100, -1));
        h.AddPing(PingTarget.Router, 100 * S, 40);
        h.AddPing(PingTarget.Router, 150 * S, 40);
        var v = h.Assess(400 * S, 200, A2R, true);
        Assert.Equal(5, v.Episodes);
        Assert.Equal(3, v.Threats);
        Assert.Equal(NetworkHop.Speaker, v.Hop);
    }

    [Fact]
    public void An_unwatched_router_leaves_the_hop_unknown()
    {
        var v = Every100s(70).Assess(400 * S, 120, A2R, routerWatched: false);
        Assert.True(v.Threatened);
        Assert.Equal(NetworkHop.Unknown, v.Hop);
        Assert.Equal(130, v.SuggestedMs); // every spike 10 over the threshold: + 10
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
    // latency, spike (three of them), suggestion: + 10 when every threat was at most 10 ms over twice the margin,
    // else + 15; rounded up to 5; capped at 200
    [InlineData(140, 105, 150)]  // margin 50, threshold 100: 5 over
    [InlineData(140, 110, 150)]  // 10 over
    [InlineData(140, 111, 155)]  // 11 over
    [InlineData(133, 90, 145)]   // margin 43, threshold 86: 4 over → 143 → 145
    [InlineData(120, 61, 130)]   // 推荐: threshold 60
    [InlineData(120, 75, 135)]
    [InlineData(190, 250, 200)]  // 205 capped
    [InlineData(196, 250, 200)]  // 211 capped: the cap is still a step up
    [InlineData(200, 300, null)] // at the cap: no suggestion, only the hint
    public void Suggestion_adds_10_or_15_ms_rounded_up_to_5_and_capped(int latency, int spike, int? expected)
    {
        var v = Every100s(spike).Assess(400 * S, latency, A2R, true);
        Assert.True(v.Threatened);
        Assert.Equal(expected, v.SuggestedMs);
    }

    [Fact]
    public void One_large_threat_makes_the_step_15()
    {
        var v = Spikes((100 * S, 105), (200 * S, 105), (300 * S, 130)).Assess(400 * S, 140, A2R, true); // 5, 5 and 30 over
        Assert.Equal(155, v.SuggestedMs);
    }

    [Fact]
    public void A_slower_speaker_leaves_less_margin()
    {
        // The same spikes at the same latency: absorbed with 70 ms in the speaker, a threat with 105.
        var h = Every100s(61);
        Assert.False(h.Assess(400 * S, 140, 70, true).Threatened); // margin 65, threshold 130
        Assert.True(h.Assess(400 * S, 140, 105, true).Threatened); // margin 30, threshold 60
    }

    [Fact]
    public void Reset_starts_a_new_session_with_nothing_in_it()
    {
        var h = Spikes((100 * S, 70), (300 * S, 80));
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
