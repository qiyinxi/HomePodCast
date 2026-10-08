using HomePodCast.Net;

namespace HomePodCast;

// The 首页 network status: the current connection's ping spikes and resend requests, fed into NetworkHealth.
// Single speaker only (a group has no ping watch); kept apart from the stream loop, which only starts and ends it.
public sealed partial class StreamController
{
    /// <summary>How often the sender's resend counters are read.</summary>
    private const int ResendSampleMs = 250;

    private HealthFeed? _healthFeed;

    /// <summary>
    /// The current connection's network events (10-minute window), reset on every new connection. Times are
    /// <see cref="HealthClockMs"/>.
    /// </summary>
    public NetworkHealth Health { get; } = new();

    /// <summary>The clock of <see cref="Health"/>'s events, for assessing them.</summary>
    public static long HealthClockMs() => Environment.TickCount64;

    /// <summary><see cref="Health"/> is being fed: a single speaker is streaming.</summary>
    public bool HealthWatched => Volatile.Read(ref _healthFeed) != null;

    /// <summary>The router answers ping, so <see cref="Health"/> can tell the PC's Wi-Fi hop from the speaker's.</summary>
    public bool RouterWatched => Volatile.Read(ref _healthFeed)?.Net?.WatchesRouter ?? false;

    /// <summary>
    /// A new connection streams: start over, and feed its ping spikes (<paramref name="net"/>; null in tests) and its
    /// sender's resend requests and misses into <see cref="Health"/> until disposed.
    /// </summary>
    internal IDisposable FeedHealth(NetworkWatch? net, Func<RtpSender?> sender)
    {
        Health.Reset();
        var feed = new HealthFeed(this, net, sender);
        Volatile.Write(ref _healthFeed, feed);
        return feed;
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

        public NetworkWatch? Net { get; }

        public HealthFeed(StreamController owner, NetworkWatch? net, Func<RtpSender?> sender)
        {
            _owner = owner;
            _sender = sender;
            Net = net;
            if (sender() is { } s) (_seen, _requests, _misses) = (s, s.RetransmitRequests, s.RetransmitMisses); // from now on
            if (net != null) net.Spike += OnSpike;
            _timer = new System.Threading.Timer(_ => Sample(), null, ResendSampleMs, ResendSampleMs);
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
