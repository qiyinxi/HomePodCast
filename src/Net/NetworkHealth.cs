namespace HomePodCast.Net;

/// <summary>Which ping a spike came from (<see cref="NetworkWatch"/>).</summary>
public enum PingTarget { Speaker, Router }

/// <summary>The Wi-Fi hop the trouble most likely comes from.</summary>
public enum NetworkHop
{
    /// <summary>
    /// The router is not watched (it does not answer ping) and the PC is not known to be wired, so the two hops can't be
    /// told apart.
    /// </summary>
    Unknown,
    /// <summary>Router → HomePod: the speaker's round trip spiked while the router answered as usual, or the PC is wired.</summary>
    Speaker,
    /// <summary>PC → router: the router spiked at the same time as the speaker, and the PC is not wired.</summary>
    Pc,
}

/// <param name="Episodes">Jitter episodes in the window: speaker ping spikes or lost pings, and resend requests or misses,
/// merged when less than <see cref="NetworkHealth.EpisodeGapMs"/> apart.</param>
/// <param name="Threats">Episodes that ate the margin: a speaker spike over it, lost pings with a margin under the ping's
/// timeout, a resend with too little margin for it, or a resend miss.</param>
/// <param name="Threatened">Worth a hint: <see cref="NetworkHealth.ThreatEpisodes"/> threats, or any resend miss.</param>
/// <param name="Hop">Where the threats came from (meaningful when <paramref name="Threatened"/>).</param>
/// <param name="MarginMs">What the latency leaves for the network: latency − speaker processing − <see cref="NetworkHealth.ReserveMs"/>.</param>
/// <param name="SuggestedMs">The latency to suggest; null when not threatened or the cap is reached.</param>
/// <param name="ConnectionThreats">Threats since the connection started (<see cref="NetworkHealth.Reset"/>), not only in the
/// window: the network's likely audible gaps, which the speaker never reports (it only asks for lost packets, never
/// for late ones). An estimate, for 首页's 断音.</param>
public sealed record NetworkVerdict(int Episodes, int Threats, bool Threatened, NetworkHop Hop, int MarginMs, int? SuggestedMs,
    int ConnectionThreats);

/// <summary>
/// The 首页 network status: the current connection's network events over the last 10 minutes, and whether they threaten
/// the margin the latency leaves. 2026-10-08: with the speaker taking 85 ms and the 推荐 120 ms, a stutter while someone
/// raised an arm coincided with the only resend request in 15 minutes.
/// <para>
/// Pure (times are milliseconds on any monotonic clock, passed in) and thread-safe: the ping loops and the resend
/// sampler add from their threads, the UI assesses from its own. It only ever suggests; the UI applies a suggestion only
/// when the user presses its button.
/// </para>
/// Rules:
/// <list type="bullet">
/// <item>Margin = latency − the speaker's arrival-to-render time − <see cref="ReserveMs"/>.</item>
/// <item>Speaker spikes, lost speaker pings and resend activity less than <see cref="EpisodeGapMs"/> apart are one
/// episode (one burst of Wi-Fi trouble is one stutter at most). Router spikes alone are not jitter: routers answer
/// pings to themselves at low priority, and the audio does not wait for them.</item>
/// <item>An episode threatens when its worst speaker round trip exceeds <see cref="SpikeMargins"/> margins, the speaker
/// asked for a resend while a round trip exceeded the margin (the copy likely came too late), or a resend missed (the
/// packet was gone). A resend on a quiet network is the protocol working, not a threat. <see cref="LostPings"/> lost
/// speaker pings count as a round trip of <see cref="NetworkWatch.TimeoutMs"/>, all they show: a threat to margins
/// below 250 ms, not to larger ones. 2026-10-12: at 500 ms (a 410 ms margin) a few lost pings among answers of 2 ms
/// median and 98 ms at most made 断音 read ≈9 and raised the hint, with not one resend request. Most "lost" pings
/// there (and on 2026-10-10's crowded 2.4 GHz band) were Windows reporting TimedOut after about 46 ms, the next ping
/// answered in a few: no measure of a delay at all.</item>
/// <item>A hint is due at <see cref="ThreatEpisodes"/> threats in the window, or at any resend miss.</item>
/// <item>A threat with a router spike within <see cref="SameTimeMs"/> is the PC's Wi-Fi (both pings cross it);
/// otherwise the HomePod's. The majority decides; a tie blames the HomePod (Wi-Fi 4 on the HomePod 2). A wired PC has
/// no Wi-Fi hop of its own: everything is the HomePod's, and router spikes only mean the router answered its own pings
/// late (seen 2026-10-10 with the PC on Ethernet and the HomePod on a crowded 2.4 GHz band).</item>
/// <item>Suggestion: the latency + <see cref="SmallStepMs"/> when every threat was a spike at most that far over its
/// threshold, else + <see cref="LargeStepMs"/>; rounded up to 5 ms and capped at <see cref="MaxSuggestedMs"/>.</item>
/// <item>The connection's threats (<see cref="NetworkVerdict.ConnectionThreats"/>) follow the same rules over every
/// episode since <see cref="Reset"/>; for that, each finished episode is kept only as the margin it needed (under a
/// thousand distinct values: round trips end at the ping's 500 ms timeout), so a long connection costs no more memory
/// than a short one.</item>
/// </list>
/// </summary>
public sealed class NetworkHealth
{
    public const long WindowMs = 10 * 60 * 1000;

    /// <summary>Events this close together are one episode.</summary>
    public const long EpisodeGapMs = 1000;

    /// <summary>A router spike this close to an episode puts it on the PC's hop.</summary>
    public const long SameTimeMs = 1000;

    /// <summary>Kept out of the margin: the PC's send timing.</summary>
    public const int ReserveMs = 5;

    /// <summary>
    /// A speaker round trip threatens only past this many margins. Measured 2026-10-08: the first rule (a round trip over
    /// the margin, with 20 ms reserved) raised the hint on the real machine while nothing could be heard. A ping crosses
    /// the air twice and the HomePod may answer pings late; the audio has the whole margin one way.
    /// </summary>
    public const int SpikeMargins = 2;

    /// <summary>
    /// Lost speaker pings in one episode that count as a round trip of <see cref="NetworkWatch.TimeoutMs"/> (one lost
    /// ping on its own is common).
    /// </summary>
    public const int LostPings = 2;

    public const int ThreatEpisodes = 3;
    public const int SmallStepMs = 10, LargeStepMs = 15;
    public const int MaxSuggestedMs = 200;

    /// <summary>Bounds memory on a hopeless network (a spike every 100 ms for 10 minutes is 6000).</summary>
    private const int MaxEntries = 10_000;

    private readonly object _lock = new();
    private readonly List<(long At, long RttMs)> _speaker = []; // RttMs < 0: no answer
    private readonly List<long> _router = [];
    private readonly List<(long At, long Requests, long Misses)> _resends = [];

    // This connection's episodes for ConnectionThreats (the lists above only keep the window): the latest one, which
    // may still grow, and the earlier ones counted by the margin they needed.
    private Episode? _latest;
    private readonly Dictionary<double, int> _earlier = [];

    /// <summary>Counts <see cref="Reset"/> calls: tells one connection's verdicts from the next one's.</summary>
    public int Session { get; private set; }

    /// <summary>A new connection: forget everything.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _speaker.Clear();
            _router.Clear();
            _resends.Clear();
            _latest = null;
            _earlier.Clear();
            Session++;
        }
    }

    /// <summary>A ping that spiked: its round trip in ms, or a negative value for no answer.</summary>
    public void AddPing(PingTarget target, long atMs, long rttMs)
    {
        lock (_lock)
        {
            if (target == PingTarget.Router)
            {
                Prune(_router, atMs, static t => t);
                _router.Add(atMs);
            }
            else
            {
                Prune(_speaker, atMs, static s => s.At);
                _speaker.Add((atMs, rttMs < 0 ? -1 : rttMs));
                Track(new Episode(atMs, atMs, Math.Max(0, rttMs), rttMs < 0 ? 1 : 0, 0, 0));
            }
        }
    }

    /// <summary>Resend requests and misses (counter deltas) seen at <paramref name="atMs"/>.</summary>
    public void AddResends(long atMs, long requests, long misses)
    {
        if (requests <= 0 && misses <= 0) return;
        lock (_lock)
        {
            Prune(_resends, atMs, static r => r.At);
            _resends.Add((atMs, Math.Max(0, requests), Math.Max(0, misses)));
            Track(new Episode(atMs, atMs, 0, 0, Math.Max(0, requests), Math.Max(0, misses)));
        }
    }

    /// <summary>
    /// One event into this connection's episodes, as <see cref="Assess"/> groups them: within <see cref="EpisodeGapMs"/>
    /// of the latest episode it joins it (also when it was added a little out of order: the ping loops and the resend
    /// sampler add from their own threads), later it starts the next one. An event added more than a second late
    /// counts as an episode of its own.
    /// </summary>
    private void Track(Episode e)
    {
        if (_latest is not { } latest)
        {
            _latest = e;
        }
        else if (e.Start >= latest.Start - EpisodeGapMs && e.Start <= latest.End + EpisodeGapMs)
        {
            _latest = latest.Merge(e);
        }
        else if (e.Start > latest.End)
        {
            Count(latest);
            _latest = e;
        }
        else
        {
            Count(e);
        }

        void Count(Episode closed) => _earlier[closed.Need] = _earlier.GetValueOrDefault(closed.Need) + 1;
    }

    /// <param name="latencyMs">The latency the connection actually uses.</param>
    /// <param name="arrivalToRenderMs">The speaker's own processing time.</param>
    /// <param name="routerWatched">The router answers pings, so the hop can be told.</param>
    /// <param name="pcWired">The PC reaches the speaker over a cable: the only Wi-Fi hop is the HomePod's.</param>
    public NetworkVerdict Assess(long nowMs, int latencyMs, int arrivalToRenderMs, bool routerWatched, bool pcWired = false)
    {
        int margin = latencyMs - arrivalToRenderMs - ReserveMs;
        long from = nowMs - WindowMs;
        List<Event> events;
        long[] router;
        int connectionThreats;
        lock (_lock)
        {
            events = _speaker.Where(s => s.At > from).Select(s => new Event(s.At, s.RttMs, 0, 0))
                .Concat(_resends.Where(r => r.At > from).Select(r => new Event(r.At, null, r.Requests, r.Misses)))
                .OrderBy(e => e.At).ToList();
            router = _router.Where(t => t > from - SameTimeMs).ToArray();
            connectionThreats = _earlier.Where(n => n.Key > margin).Sum(n => n.Value)
                                + (_latest is { } latest && latest.Threatens(margin) ? 1 : 0);
        }

        int episodes = 0, threats = 0, onPc = 0, onSpeaker = 0;
        bool missed = false, allSmall = true;
        for (int i = 0; i < events.Count;)
        {
            // One episode: everything until a quiet gap of more than EpisodeGapMs.
            long start = events[i].At, end = start;
            long worst = 0; // worst answered speaker round trip
            int lostPings = 0;
            long requests = 0, misses = 0;
            for (; i < events.Count && events[i].At - end <= EpisodeGapMs; i++)
            {
                var e = events[i];
                end = e.At;
                if (e.RttMs is { } rtt)
                {
                    if (rtt < 0) lostPings++;
                    else worst = Math.Max(worst, rtt);
                }
                requests += e.Requests;
                misses += e.Misses;
            }
            episodes++;

            if (!new Episode(start, end, worst, lostPings, requests, misses).Threatens(margin)) continue;

            bool lost = lostPings >= LostPings;
            bool resendLate = requests > 0 && worst > margin; // a resend while the network was slow likely came too late
            threats++;
            missed |= misses > 0;
            if (lost || resendLate || misses > 0 || worst - SpikeMargins * margin > SmallStepMs) allSmall = false;
            if (router.Any(t => t >= start - SameTimeMs && t <= end + SameTimeMs)) onPc++;
            else onSpeaker++;
        }

        bool threatened = threats >= ThreatEpisodes || missed;
        var hop = HopOf(routerWatched, pcWired, onPc, onSpeaker);
        int? suggested = null;
        if (threatened)
        {
            int ms = Math.Min(MaxSuggestedMs, RoundUpTo5(latencyMs + (allSmall ? SmallStepMs : LargeStepMs)));
            if (ms > latencyMs) suggested = ms;
        }
        return new NetworkVerdict(episodes, threats, threatened, hop, margin, suggested, connectionThreats);
    }

    /// <summary>
    /// Which hop to blame for the threats: a wired PC has no Wi-Fi of its own, so it is always the HomePod's (a router
    /// spike then only means the router answered its own ping late); otherwise the majority of the router-backed
    /// (<paramref name="onPc"/>) and the quiet-router threats, a tie blaming the HomePod; unknown while the router isn't watched.
    /// </summary>
    internal static NetworkHop HopOf(bool routerWatched, bool pcWired, int onPc, int onSpeaker) =>
        pcWired ? NetworkHop.Speaker
        : !routerWatched ? NetworkHop.Unknown
        : onPc > onSpeaker ? NetworkHop.Pc : NetworkHop.Speaker;

    internal static int RoundUpTo5(int ms) => (ms + 4) / 5 * 5;

    private static void Prune<T>(List<T> list, long nowMs, Func<T, long> at)
    {
        long keep = nowMs - WindowMs - SameTimeMs;
        if (list.Count > 0 && at(list[0]) < keep) list.RemoveAll(x => at(x) < keep);
        if (list.Count >= MaxEntries) list.RemoveRange(0, list.Count - MaxEntries + 1);
    }

    private readonly record struct Event(long At, long? RttMs, long Requests, long Misses);

    /// <summary>One burst of events: its worst answered speaker round trip, lost speaker pings, resend requests and misses.</summary>
    private readonly record struct Episode(long Start, long End, long WorstMs, int LostPings, long Requests, long Misses)
    {
        /// <summary>
        /// The margin that would have absorbed it: it threatens every margin below this (the rules above). A resend miss:
        /// no margin does. A resend while a round trip exceeded the margin likely came too late: the round trip.
        /// Otherwise a round trip beyond <see cref="SpikeMargins"/> margins: the round trip / that. Lost pings count as a
        /// round trip of <see cref="NetworkWatch.TimeoutMs"/> (<see cref="RoundTripMs"/>).
        /// </summary>
        public double Need =>
            Misses > 0 ? double.PositiveInfinity
            : Requests > 0 ? RoundTripMs
            : (double)RoundTripMs / SpikeMargins;

        /// <summary>
        /// The worst round trip, <see cref="NetworkWatch.TimeoutMs"/> at least when <see cref="NetworkHealth.LostPings"/>
        /// pings went unanswered: all they show is a round trip past the timeout (or a ping dropped on the way).
        /// </summary>
        private long RoundTripMs => LostPings >= NetworkHealth.LostPings ? Math.Max(WorstMs, NetworkWatch.TimeoutMs) : WorstMs;

        public bool Threatens(int marginMs) => marginMs < Need;

        public Episode Merge(Episode e) => new(Math.Min(Start, e.Start), Math.Max(End, e.End), Math.Max(WorstMs, e.WorstMs),
            LostPings + e.LostPings, Requests + e.Requests, Misses + e.Misses);
    }
}
