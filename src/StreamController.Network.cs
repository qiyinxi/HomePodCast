using HomePodCast.Net;

namespace HomePodCast;

// The 首页 network status: the current connection's ping spikes and resend requests, fed into NetworkHealth.
// A single speaker, or a stereo pair alone (its first member's ping); kept apart from the stream loops, which only
// start and end it. Other groups are not watched: their outputs keep their own latency (混音器).
public sealed partial class StreamController
{
    /// <summary>How often the sender's resend counters are read.</summary>
    private const int ResendSampleMs = 250;

    private HealthFeed? _healthFeed;

    /// <summary>
    /// The current connection's network events (a 10-minute window, and the connection's threats in full), reset on
    /// every new connection. Times are <see cref="HealthClockMs"/>.
    /// </summary>
    public NetworkHealth Health { get; } = new();

    /// <summary>The clock of <see cref="Health"/>'s events, for assessing them.</summary>
    public static long HealthClockMs() => Environment.TickCount64;

    /// <summary><see cref="Health"/> is being fed: a single speaker, or a stereo pair alone, is streaming.</summary>
    public bool HealthWatched => Volatile.Read(ref _healthFeed) != null;

    /// <summary>The router answers ping, so <see cref="Health"/> can tell the PC's Wi-Fi hop from the speaker's.</summary>
    public bool RouterWatched => Volatile.Read(ref _healthFeed)?.Net?.WatchesRouter ?? false;

    /// <summary>
    /// The network this PC reaches the current speaker through (its adapter: Ethernet or Wi-Fi), looked up once per
    /// connection in the background; null until known, and while not streaming to a single speaker.
    /// </summary>
    public NetworkProfile? PcNetwork => Volatile.Read(ref _healthFeed)?.PcNetwork;

    /// <summary>The PC reaches the speaker over a cable: the only Wi-Fi hop is the router's to the speaker.</summary>
    public bool PcWired => PcNetwork?.Kind == AdapterKind.Ethernet;

    /// <summary>
    /// A new connection streams: start over, and feed its ping spikes (<paramref name="net"/>, started only after this
    /// call so that none is missed; null in tests) and its sender's resend requests and misses into <see cref="Health"/>
    /// until disposed. <paramref name="pcNetwork"/> (null in tests) looks up <see cref="PcNetwork"/>, on the thread
    /// pool: it does COM work.
    /// </summary>
    internal IDisposable FeedHealth(NetworkWatch? net, Func<RtpSender?> sender, Func<NetworkProfile>? pcNetwork = null)
    {
        Health.Reset();
        var feed = new HealthFeed(this, net, sender, pcNetwork);
        Volatile.Write(ref _healthFeed, feed);
        return feed;
    }

    /// <summary>
    /// The streaming speakers' own arrival-to-render time, as <see cref="Health"/>'s margin takes it: a group's (its
    /// slowest member's, from the connection itself), else the single speaker's as the latency floor assumes it.
    /// </summary>
    public int SpeakerProcessingMs => _groupRunner?.Current?.ArrivalToRenderMs ?? SafeLatency(0) - SafetyMarginMs;

    private IDisposable? _groupHealth;

    /// <summary>
    /// A stereo pair alone streams: its first member is pinged and both members' resend requests are counted, as a
    /// single speaker's are. 2026-11-10: a stereo pair taken over from its home-theater Apple TV stuttered, and 首页 said
    /// nothing: the home-theater notice and the network hint only knew the single speaker. On 2026-10-12 an Apple TV and
    /// its HomePod spiked in step, so one member's ping stands for the pair.
    /// </summary>
    private void WatchGroupNetwork(SpeakerGroup group)
    {
        if (group.Sender is not { Streams.Count: > 0 } sender) return;
        var address = sender.Streams[0].Remote;
        var net = new NetworkWatch(address);
        var feed = FeedHealth(net, () => group.Sender, () => NetworkProfile.ForSpeaker(address));
        net.Start(); // only now that FeedHealth listens, as for a single speaker
        var stats = new System.Threading.Timer(_ => Log.Info($"group network: {net.TakeSummary()}"), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var watch = new Disposer(() =>
        {
            stats.Dispose();
            feed.Dispose();
            Log.Info($"group network: {net.TakeSummary()}");
            net.Dispose();
        });
        Interlocked.Exchange(ref _groupHealth, watch)?.Dispose();
    }

    /// <summary>The group's stream ended (or Stop tears it down): nothing is pinged or counted any more.</summary>
    private void EndGroupNetworkWatch() => Interlocked.Exchange(ref _groupHealth, null)?.Dispose();

    private sealed class Disposer(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class HealthFeed : IDisposable
    {
        private readonly StreamController _owner;
        private readonly Func<RtpSender?> _sender;
        private readonly System.Threading.Timer _timer;
        private readonly object _lock = new();
        private RtpSender? _seen;
        private long _requests, _misses;
        private bool _disposed;
        private volatile NetworkProfile? _pcNetwork;

        public NetworkWatch? Net { get; }

        public NetworkProfile? PcNetwork => _pcNetwork;

        public HealthFeed(StreamController owner, NetworkWatch? net, Func<RtpSender?> sender, Func<NetworkProfile>? pcNetwork)
        {
            _owner = owner;
            _sender = sender;
            Net = net;
            if (sender() is { } s) (_seen, _requests, _misses) = (s, s.RetransmitRequests, s.RetransmitMisses); // from now on
            if (net != null) net.Spike += OnSpike;
            _timer = new System.Threading.Timer(_ => Sample(), null, ResendSampleMs, ResendSampleMs);
            if (pcNetwork != null) _ = Task.Run(() => LookUp(pcNetwork));
        }

        private void LookUp(Func<NetworkProfile> pcNetwork)
        {
            try
            {
                var profile = pcNetwork();
                _pcNetwork = profile;
                Log.Info($"network: this PC reaches the speaker through {profile}");
            }
            catch (Exception ex)
            {
                Log.Warn($"network: the PC's adapter is unknown ({ex.Message})");
            }
        }

        private void OnSpike(PingTarget target, long rttMs) => _owner.Health.AddPing(target, HealthClockMs(), rttMs);

        private void Sample()
        {
            lock (_lock)
            {
                var s = _sender();
                if (s == null || _disposed) return; // a tick may still run after Dispose: the next connection's Health
                if (s != _seen) (_seen, _requests, _misses) = (s, 0, 0); // a sender that came later: its counters start at 0
                long requests = s.RetransmitRequests, misses = s.RetransmitMisses;
                _owner.Health.AddResends(HealthClockMs(), requests - _requests, misses - _misses);
                (_requests, _misses) = (requests, misses);
            }
        }

        public void Dispose()
        {
            if (Net != null) Net.Spike -= OnSpike;
            _timer.Dispose();
            lock (_lock) _disposed = true;
            Interlocked.CompareExchange(ref _owner._healthFeed, null, this);
        }
    }
}
