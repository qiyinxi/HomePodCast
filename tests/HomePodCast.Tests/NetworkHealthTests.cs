using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Pages;

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
    public void Lost_pings_only_threaten_a_margin_under_half_the_pings_timeout()
    {
        // 2026-10-12 at 500 ms (a 410 ms margin): a few lost pings among answers of 2 ms made 断音 read ≈9, with no
        // resend request. All a lost ping shows is a round trip past the 500 ms timeout: twice a 250 ms margin.
        NetworkHealth Pairs() => Spikes((10_000, -1), (10_100, -1), (100_000, -1), (100_100, -1), (200_000, -1), (200_100, -1));
        var at500 = Pairs().Assess(300 * S, 500, A2R, true);
        Assert.Equal(3, at500.Episodes);
        Assert.Equal(0, at500.Threats);
        Assert.False(at500.Threatened);
        Assert.Equal(0, at500.ConnectionThreats);

        Assert.Equal(0, Pairs().Assess(300 * S, 340, A2R, true).Threats); // margin 250: twice it is the timeout
        var at339 = Pairs().Assess(300 * S, 339, A2R, true);
        Assert.Equal(3, at339.Threats);
        Assert.Equal(3, at339.ConnectionThreats);

        // A resend among them may have come too late: the round trip itself is the margin it needed.
        var resend = new NetworkHealth();
        resend.AddPing(PingTarget.Speaker, 10_000, -1);
        resend.AddPing(PingTarget.Speaker, 10_100, -1);
        resend.AddResends(10_300, 1, 0);
        var v = resend.Assess(300 * S, 500, A2R, true);
        Assert.Equal(1, v.Threats);
        Assert.Equal(1, v.ConnectionThreats);
        Assert.Equal(0, resend.Assess(300 * S, 600, A2R, true).Threats); // margin 510
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
    public void A_wired_pc_puts_every_threat_on_the_homepods_hop()
    {
        // 2026-10-10: the PC on Ethernet, the HomePod on a crowded 2.4 GHz band. A router spike next to each speaker spike
        // only means the router answered its own ping late: a wired PC has no Wi-Fi hop to blame.
        var h = Every100s(70);
        foreach (long at in new[] { 100_200L, 200_300, 300_100 }) h.AddPing(PingTarget.Router, at, 45);
        Assert.Equal(NetworkHop.Pc, h.Assess(400 * S, 120, A2R, routerWatched: true).Hop); // Wi-Fi or unknown adapter: as before

        var wired = h.Assess(400 * S, 120, A2R, routerWatched: true, pcWired: true);
        Assert.True(wired.Threatened);
        Assert.Equal(NetworkHop.Speaker, wired.Hop);
        Assert.Equal(130, wired.SuggestedMs); // only the hop changes
        Assert.Equal(NetworkHop.Speaker, h.Assess(400 * S, 120, A2R, routerWatched: false, pcWired: true).Hop); // not "unknown" either
    }

    [Theory]
    // routerWatched, pcWired, threats with a router spike, threats without, hop
    [InlineData(false, false, 0, 3, NetworkHop.Unknown)]
    [InlineData(true, false, 2, 1, NetworkHop.Pc)]
    [InlineData(true, false, 1, 1, NetworkHop.Speaker)] // a tie blames the HomePod
    [InlineData(true, false, 0, 3, NetworkHop.Speaker)]
    [InlineData(true, true, 3, 0, NetworkHop.Speaker)]
    [InlineData(false, true, 0, 3, NetworkHop.Speaker)]
    public void Hop_rules(bool routerWatched, bool pcWired, int onPc, int onSpeaker, NetworkHop hop) =>
        Assert.Equal(hop, NetworkHealth.HopOf(routerWatched, pcWired, onPc, onSpeaker));

    [Fact]
    public void The_connections_threats_outlast_the_window()
    {
        // 断音 counts since the connection started; the hint and the 网络 line look at the last 10 minutes.
        var h = Spikes((60 * S, 75), (250 * S, 90), (480 * S, 65)); // three threats at 推荐 120 ms
        h.AddPing(PingTarget.Speaker, 900 * S, 40);              // jitter, not a threat
        h.AddPing(PingTarget.Speaker, 1500 * S, 70);             // a fourth threat, 25 minutes in
        var v = h.Assess(1600 * S, 120, A2R, true);
        Assert.Equal(1, v.Episodes);
        Assert.Equal(1, v.Threats);
        Assert.False(v.Threatened);
        Assert.Equal(4, v.ConnectionThreats);
    }

    [Fact]
    public void The_connections_threats_follow_the_same_rules_as_the_window()
    {
        // Everything inside the window: both counts agree.
        void Same(NetworkHealth h, long now, int latency, int arrivalToRender, int expected)
        {
            var v = h.Assess(now, latency, arrivalToRender, true);
            Assert.Equal(expected, v.Threats);
            Assert.Equal(expected, v.ConnectionThreats);
        }

        Same(Spikes((60 * S, 35), (250 * S, 48), (480 * S, 60)), 500 * S, 120, A2R, 0);                    // under twice the margin
        Same(Spikes((10_000, 70), (10_100, 90), (10_700, 40), (11_600, 35), (12_500, 31)), 30 * S, 120, A2R, 1); // one bad second
        Same(Spikes((10 * S, -1), (100 * S, -1), (200 * S, -1)), 300 * S, 300, A2R, 0);                     // single lost pings
        Same(Spikes((10_000, -1), (10_100, -1), (100_000, -1), (100_100, -1)), 300 * S, 300, A2R, 2);       // lost pairs
        Same(Spikes((10_000, -1), (10_100, -1), (100_000, -1), (100_100, -1)), 300 * S, 500, A2R, 0);       // not at 500 ms
        Same(Spikes((100 * S, 150), (200 * S, 250), (300_000, -1), (300_100, -1)), 400 * S, 200, A2R, 2);  // 影视: 150 absorbed

        var quiet = new NetworkHealth();
        quiet.AddResends(50 * S, 1, 0);
        quiet.AddResends(400 * S, 2, 0);
        Same(quiet, 500 * S, 120, A2R, 0); // a resend on a quiet network is the protocol working

        var late = new NetworkHealth();
        foreach (long at in new[] { 100_000L, 200_000, 300_000 })
        {
            late.AddResends(at + 250, 1, 0);          // the sampler's thread got there first:
            late.AddPing(PingTarget.Speaker, at, 45); // still one episode with its ping
        }
        Same(late, 400 * S, 120, A2R, 3);

        var miss = new NetworkHealth();
        miss.AddResends(50 * S, 1, 1);
        Same(miss, 60 * S, 160, A2R, 1);

        // The same events at another margin: each episode is kept as the margin it needed, so any margin can be asked.
        var h = Every100s(61);
        Same(h, 400 * S, 140, 70, 0);  // margin 65, threshold 130
        Same(h, 400 * S, 140, 105, 3); // margin 30, threshold 60
    }

    [Fact]
    public void A_long_bad_connection_is_counted_in_full()
    {
        // Six hours of a separate threat every 4 s and harmless jitter in between: the window lists are pruned, the
        // connection's count is not.
        var h = new NetworkHealth();
        const int bursts = 6 * 3600 / 4;
        for (int k = 0; k < bursts; k++)
        {
            h.AddPing(PingTarget.Speaker, k * 4 * S, 70 + k % 50);
            h.AddPing(PingTarget.Speaker, k * 4 * S + 2 * S, 40);
        }
        var v = h.Assess(bursts * 4 * S, 120, A2R, true);
        Assert.Equal(bursts, v.ConnectionThreats);
        Assert.Equal(149, v.Threats); // the last 10 minutes: k = 5251…5399
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
        Assert.Equal(0, v.ConnectionThreats);
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

    [Fact]
    public async Task The_pcs_adapter_is_looked_up_once_per_connection_in_the_background()
    {
        using var c = new StreamController();
        Assert.Null(c.PcNetwork);
        int lookups = 0;
        var wired = new NetworkProfile("以太网", AdapterKind.Ethernet, "ASUS_D0", NetworkCategory.Private);
        using (c.FeedHealth(null, () => null, () =>
               {
                   Interlocked.Increment(ref lookups);
                   return wired;
               }))
        {
            for (int i = 0; i < 200 && c.PcNetwork == null; i++) await Task.Delay(25);
            Assert.Equal(wired, c.PcNetwork);
            Assert.True(c.PcWired);
            await Task.Delay(300); // the resend sampler ticks; nothing looks the adapter up again
        }
        Assert.Equal(1, lookups);
        Assert.Null(c.PcNetwork); // not streaming: unknown again
        Assert.False(c.PcWired);

        using (c.FeedHealth(null, () => null, () => wired with { Kind = AdapterKind.WiFi, Interface = "WLAN" }))
        {
            for (int i = 0; i < 200 && c.PcNetwork == null; i++) await Task.Delay(25);
            Assert.False(c.PcWired);
        }
        using (c.FeedHealth(null, () => null)) Assert.False(c.PcWired); // not looked up (tests): the rules as before
    }
}

/// <summary>
/// 首页's network texts: the hint names the band, a wired PC never hears about its own Wi-Fi, and 断音 counts the
/// network's share.
/// </summary>
public class HomeNetworkTextTests
{
    // 38 episodes, 3 of them threats: the hint counts the threats ("10 分钟内 3 次").
    private static NetworkVerdict Threatened(NetworkHop hop, int? suggestedMs) => new(38, 3, true, hop, 30, suggestedMs, 3);

    [Fact]
    public void The_homepods_hop_leads_with_the_band_and_placement_comes_second()
    {
        var capped = HomePage.NetworkHintText(Threatened(NetworkHop.Speaker, null));
        Assert.StartsWith("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内可能断音 3 次）", capped); // says what the 3 are
        Assert.Contains("2.4G", capped);
        Assert.True(capped.IndexOf("5G", StringComparison.Ordinal) < capped.IndexOf("摆放位置", StringComparison.Ordinal));

        var suggested = HomePage.NetworkHintText(Threatened(NetworkHop.Speaker, 135));
        Assert.Contains("建议把延迟调到 135\u00A0ms", suggested); // the suggestion stays, the band is a short addition
        Assert.EndsWith("改连 5G 更管用", suggested);

        // Either hop: the band is still worth a look.
        Assert.Contains("改连 5G", HomePage.NetworkHintText(Threatened(NetworkHop.Unknown, 135)));
        Assert.Contains("改连 5G", HomePage.NetworkHintText(Threatened(NetworkHop.Unknown, null)));
        // The PC's own Wi-Fi: its own advice, unchanged.
        Assert.EndsWith("试试关掉无线网卡的节能，或改用网线", HomePage.NetworkHintText(Threatened(NetworkHop.Pc, null)));
    }

    [Fact]
    public void A_homepod_taken_over_from_its_apple_tv_hears_about_the_home_theater_not_the_band()
    {
        // 2026-10-12: the HomePod and its Apple TV both spiked every 2.1 s while it was the Apple TV's home theater
        // speaker; the stutters stopped when the Apple TV's audio went back to the TV. On 5 GHz already.
        foreach (var hop in new[] { NetworkHop.Speaker, NetworkHop.Unknown })
        {
            var suggested = HomePage.NetworkHintText(Threatened(hop, 135), "客厅");
            Assert.StartsWith("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内可能断音 3 次），建议把延迟调到 135 ms。", suggested);
            Assert.Contains("Apple TV「客厅」的家庭影院音箱", suggested);
            Assert.EndsWith("在 Apple TV 上把音频输出改回电视更管用", suggested);
            Assert.DoesNotContain("5G", suggested);

            var capped = HomePage.NetworkHintText(Threatened(hop, null), "客厅");
            Assert.EndsWith("在 Apple TV 上把音频输出改回电视最管用", capped);
            Assert.DoesNotContain("5G", capped);
        }
        // The PC's own Wi-Fi is still the PC's.
        Assert.StartsWith("电脑这边", HomePage.NetworkHintText(Threatened(NetworkHop.Pc, null), "客厅"));
        // Not taken over: the band, as before.
        Assert.Contains("改连 5G", HomePage.NetworkHintText(Threatened(NetworkHop.Speaker, null)));
    }

    [Fact]
    public void The_home_theater_notice_names_both_and_the_latency_that_absorbs_it()
    {
        // Shown at once while such a HomePod streams below 200 ms, before any dropout is counted.
        Assert.Equal(200, HomePage.HomeTheaterLatencyMs);
        Assert.Equal("卧室是 Apple TV「客厅」的家庭影院音箱：它们之间的连接会定时占用 Wi-Fi，延迟低于 200 ms 时可能断音。" +
                     "打游戏时在 Apple TV 上把音频输出改回电视最管用，也可以把延迟调到 200 ms",
            HomePage.HomeTheaterNotice("卧室", "客厅"));
    }

    [Theory]
    [InlineData(120, 80)]  // 推荐: a suggestion (135 ms)
    [InlineData(200, 250)] // at the cap: no suggestion
    public void A_wired_pc_never_hears_about_its_own_wifi(int latency, int spike)
    {
        // A router spike next to every speaker spike: on Wi-Fi that is the PC's hop, wired it never is.
        var h = new NetworkHealth();
        foreach (long at in new[] { 100_000L, 200_000, 300_000 })
        {
            h.AddPing(PingTarget.Speaker, at, spike);
            h.AddPing(PingTarget.Router, at + 100, 50);
        }
        Assert.StartsWith("电脑这边", HomePage.NetworkHintText(h.Assess(400_000, latency, 85, routerWatched: true)));
        foreach (bool routerWatched in new[] { true, false })
        {
            var text = HomePage.NetworkHintText(h.Assess(400_000, latency, 85, routerWatched, pcWired: true));
            Assert.StartsWith("HomePod 那边", text);
            Assert.DoesNotContain("电脑这边", text);
            Assert.DoesNotContain("无线网卡", text);
            Assert.DoesNotContain("网线", text);
        }
    }

    [Theory]
    [InlineData(0, 0, "0")]
    [InlineData(2, 0, "2")]
    [InlineData(0, 3, "≈3")]
    [InlineData(2, 3, "≈5")]
    public void Dropouts_add_the_networks_estimate_and_say_it_is_one(long pc, int network, string shown) =>
        Assert.Equal(shown, HomePage.DropoutValue(pc, network));

    [Fact]
    public void The_dropout_tooltip_splits_the_count_while_the_network_is_watched()
    {
        var tip = HomePage.DropoutTip(1, 3);
        Assert.StartsWith("这次连接里的断音：电脑这边 1 次（实测），网络 3 次（估计）\n", tip);
        Assert.Equal("这次连接中电脑这边断音的次数", HomePage.DropoutTip(1, null)); // a group: the network is not watched
    }

    [Fact]
    public void Stutters_on_a_crowded_band_no_longer_show_as_0()
    {
        // 2026-10-10 at 200 ms: the PC's FIFO never ran dry (断音 said 0), but the speaker's pings went unanswered in
        // pairs now and then, and spiked to ~110 ms (absorbed: the margin is 110 ms, twice that 220).
        var h = new NetworkHealth();
        for (long at = 15_000; at < 20 * 60 * 1000; at += 30_000)
        {
            h.AddPing(PingTarget.Speaker, at, -1);
            h.AddPing(PingTarget.Speaker, at + 600, -1);
            h.AddPing(PingTarget.Speaker, at + 12_000, 110);
        }
        var v = h.Assess(20 * 60 * 1000, 200, 85, routerWatched: true, pcWired: true);
        Assert.Equal(40, v.ConnectionThreats); // one per lost pair, since the connection started
        Assert.Equal(20, v.Threats);           // the hint's last 10 minutes
        Assert.Equal(NetworkHop.Speaker, v.Hop);
        Assert.Equal("≈40", HomePage.DropoutValue(0, v.ConnectionThreats));
    }

    // ---------------------------------------------------------------- which of the line and the hint 首页 shows

    private static readonly NetworkVerdict Calm = new(0, 0, false, NetworkHop.Speaker, 30, null, 0);

    [Fact]
    public void The_network_line_says_stable_or_counts_the_jitter_of_the_last_10_minutes()
    {
        var quiet = new NetworkHealth().Assess(10 * 60 * 1000, 120, 85, routerWatched: true);
        Assert.Equal("网络：稳定", HomePage.NetworkLine(quiet.Episodes));
        Assert.False(HomePage.NoticeFor(quiet, null, false, 120, "卧室").Show);

        var h = new NetworkHealth();
        foreach (var (at, rtt) in new (long, long)[] { (60_000, 35), (250_000, 48) }) h.AddPing(PingTarget.Speaker, at, rtt);
        var jittery = h.Assess(500_000, 120, 85, routerWatched: true);
        Assert.False(HomePage.NoticeFor(jittery, null, false, 120, "卧室").Show); // absorbed: only the line counts it
        Assert.Equal("网络：最近 10 分钟抖动 2 次", HomePage.NetworkLine(jittery.Episodes));
        Assert.Equal("网络：最近 10 分钟抖动 1 次", HomePage.NetworkLine(1));
    }

    [Fact]
    public void A_threatening_verdict_shows_the_hint_with_its_suggestion_until_it_was_applied_on_this_connection()
    {
        var threatened = new NetworkVerdict(12, 4, true, NetworkHop.Speaker, 15, 135, 4);
        var hint = HomePage.NoticeFor(threatened, null, applied: false, 120, "卧室");
        Assert.True(hint.Show);
        Assert.Equal(135, hint.SuggestedMs);                      // 应用 sets 135 ms (LatencyTuner.UseCustom: 自定义, one reconnect)
        Assert.Equal(HomePage.NetworkHintText(threatened), hint.Text);
        Assert.False(HomePage.NoticeFor(threatened, null, applied: true, 135, "卧室").Show); // gone after 应用

        var capped = threatened with { SuggestedMs = null };       // already at the most the hint suggests
        var noButton = HomePage.NoticeFor(capped, null, false, 200, "卧室");
        Assert.True(noButton.Show);
        Assert.Null(noButton.SuggestedMs);                         // advice only: no 应用, the latency is never changed
    }

    [Fact]
    public void A_homepod_taken_over_from_its_apple_tv_gets_the_home_theater_notice_below_200_ms_at_once()
    {
        foreach (int latency in new[] { 105, 120, 199 })
        {
            var notice = HomePage.NoticeFor(Calm, "客厅", applied: false, latency, "卧室");
            Assert.True(notice.Show);                              // before any dropout is counted
            Assert.True(notice.HomeTheater);
            Assert.False(notice.Threatened);
            Assert.Equal(200, notice.SuggestedMs);
            Assert.Equal(HomePage.HomeTheaterNotice("卧室", "客厅"), notice.Text);
        }
        Assert.False(HomePage.NoticeFor(Calm, "客厅", false, 200, "卧室").Show);   // at 200 ms: the plain line
        Assert.False(HomePage.NoticeFor(Calm, "客厅", false, 500, "卧室").Show);
        Assert.False(HomePage.NoticeFor(Calm, null, false, 105, "卧室").Show);     // not taken from an Apple TV
        Assert.False(HomePage.NoticeFor(Calm, "客厅", applied: true, 105, "卧室").Show); // 应用 pressed on this connection
    }

    [Fact]
    public void Once_dropouts_threaten_a_homepod_taken_from_its_apple_tv_the_hint_names_the_home_theater_and_suggests_200_ms()
    {
        var threatened = new NetworkVerdict(12, 4, true, NetworkHop.Speaker, 15, 135, 4);
        var hint = HomePage.NoticeFor(threatened, "客厅", false, 120, "卧室");
        Assert.True(hint.Threatened && hint.HomeTheater);
        Assert.Equal(200, hint.SuggestedMs);                      // not 135: that would still stutter
        Assert.Contains("建议把延迟调到 200 ms", hint.Text);
        Assert.Contains("Apple TV「客厅」的家庭影院音箱", hint.Text);
        Assert.DoesNotContain("5G", hint.Text);

        // At 200 ms or more the verdict's own suggestion stands.
        var above = HomePage.NoticeFor(threatened with { SuggestedMs = 250 }, "客厅", false, 200, "卧室");
        Assert.False(above.HomeTheater);
        Assert.Equal(250, above.SuggestedMs);
    }
}
