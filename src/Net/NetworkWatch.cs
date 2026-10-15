using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HomePodCast.Net;

/// <summary>
/// While streaming, pings the speaker and the router every <see cref="IntervalMs"/> and logs spikes as they happen
/// (a round trip over <see cref="SpikeMs"/>, or no answer), at most once a second per target with the worst value of
/// that second. The speaker only tells us about lost packets (retransmit requests), never about late ones, so this is
/// how a stutter heard on a real machine can be matched to the network; the router ping tells the two Wi-Fi hops
/// apart (a speaker spike with a quiet router is the router → speaker hop). 2026-10-08: a stutter while someone raised
/// an arm coincided with the only retransmit request in 15 minutes, and one with a phone near the speaker with none.
/// Every spike is also raised as <see cref="Spike"/> for the 首页 network status (<see cref="NetworkHealth"/>).
/// Nothing is pinged before <see cref="Start"/>, so a handler subscribed before that gets every spike.
/// <para>
/// Each target has its own background thread that pings with the blocking <see cref="Ping.Send(IPAddress, int, byte[], PingOptions)"/>.
/// 2026-10-14: <see cref="Ping.SendPingAsync(IPAddress, TimeSpan, byte[], PingOptions, CancellationToken)"/> left one
/// kernel event per ping to the finalizer (.NET 10 on Windows: Ping unregisters its thread-pool wait after the wait
/// fired, and RegisteredWaitHandle then rents an AutoResetEvent it never returns), so a streaming app gained 20
/// handles a second until a garbage collection, 8565 at most on the maintainer's PC. The blocking call registers no wait.
/// </para>
/// </summary>
internal sealed class NetworkWatch : IDisposable
{
    public const int IntervalMs = 100;
    public const int SpikeMs = 30;

    /// <summary>An answer later than this counts as lost ("no answer within 500 ms").</summary>
    public const int TimeoutMs = 500;

    /// <summary>
    /// Windows ends ICMP echo requests that time out on a fixed tick of this length, not when their own timeout is up.
    /// Measured 2026-10-14 on the maintainer's PC (pings to a public DNS server answering in 32 ms, sent at random
    /// moments): with a 500 ms (also 510, 750 ms) timeout 6 % came back TimedOut after 1–32 ms, all at the same two
    /// instants of every second; with 1000 ms none. That was the HomePod "lost" in about 1 ping of 6 for hours
    /// (16139 of 514275 between 2026-10-13 and -14, the router 1 of 516600): every fifth 100 ms ping went out just
    /// before the tick, while the PC's ICMP counters show an answer for every request it sent.
    /// </summary>
    public const int WindowsIcmpTickMs = 500;

    /// <summary>
    /// The timeout Windows gets: two of its ticks, so that a request waits at least <see cref="TimeoutMs"/> (and at most
    /// a second) before it times out. Whether an answer counts as lost is decided by <see cref="TimeoutMs"/>.
    /// </summary>
    public const int IcmpTimeoutMs = 2 * WindowsIcmpTickMs;

    /// <summary>Pings that could not be sent in a row (<see cref="PingException"/>) before a probe asks only once a second.</summary>
    public const int ErrorsBeforeBackoff = 10;

    public const int BackoffMs = 1000;

    /// <summary>Tries without any answer after which a target is not watched (a router that does not answer ping).</summary>
    public const int TriesBeforeGivingUp = 20;

    private readonly ManualResetEventSlim _stop = new(); // no kernel handle: nothing ever asks for its WaitHandle
    private readonly Probe _speaker;
    private readonly Probe? _router;
    private int _started;

    public NetworkWatch(IPAddress speaker, IPAddress? router = null) : this(speaker, router, IntervalMs, SystemEchoFactory)
    {
    }

    /// <summary>Real pings (<see cref="Ping.Send(IPAddress, int, byte[], PingOptions)"/>).</summary>
    internal static readonly Func<IEcho> SystemEchoFactory = () => new SystemEcho();

    /// <param name="intervalMs">Between the starts of two pings to one target (0: back to back, for tests).</param>
    /// <param name="echo">Makes the echo sender each probe uses; a new one after a ping that could not be sent.</param>
    internal NetworkWatch(IPAddress speaker, IPAddress? router, int intervalMs, Func<IEcho> echo)
    {
        _speaker = new Probe("the speaker", speaker, PingTarget.Speaker, RaiseSpike, intervalMs, echo);
        router ??= RouterFor(speaker);
        if (router != null && !router.Equals(speaker)) _router = new Probe("the router", router, PingTarget.Router, RaiseSpike, intervalMs, echo);
    }

    /// <summary>
    /// Starts pinging (once; a second call does nothing) and returns this watch. Subscribe to <see cref="Spike"/> first:
    /// a spike raised before that is gone. Not done by the constructor: 2026-10-15, a test that subscribed right after
    /// it found no spike at all (CI run 38710133779), as its fake answered so fast that the probe had made all its pings
    /// before the subscription; with every core busy that happened in about 1 run of 4.
    /// </summary>
    public NetworkWatch Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return this;
        StartThread(_speaker);
        if (_router != null) StartThread(_router);
        return this;
    }

    private void StartThread(Probe probe) =>
        new Thread(() => probe.Run(_stop)) { IsBackground = true, Name = $"HomePodCast ping ({probe.Target})" }.Start();

    /// <summary>
    /// Every spike as it happens, on the probe's thread: the round trip in ms (over <see cref="SpikeMs"/>), or -1 for no
    /// answer. Only once that target has answered at all, so a router that never answers ping raises nothing.
    /// </summary>
    public event Action<PingTarget, long>? Spike;

    /// <summary>The router answers ping (and has not been given up on), so a spike can be pinned to a hop.</summary>
    public bool WatchesRouter => _router is { Watching: true };

    private void RaiseSpike(PingTarget target, long rttMs) => Spike?.Invoke(target, rttMs);

    /// <summary>"ping=3/9/41ms lost=0/600 router=1/2/5ms lost=0/600" (median/p99/max since the last call).</summary>
    public string TakeSummary() =>
        "ping=" + _speaker.TakeSummary() + (_router is { } r ? " router=" + r.TakeSummary() : "");

    /// <summary>Stops both probes (or keeps them from starting); each ends after the ping it is waiting for (a second at most).</summary>
    public void Dispose() => _stop.Set();

    /// <summary>The round trip a probe counts for <paramref name="reply"/>: its own, or -1 for no answer within <see cref="TimeoutMs"/>.</summary>
    internal static long RoundTrip(EchoResult reply) =>
        reply.Status == IPStatus.Success && reply.RoundTripMs <= TimeoutMs ? reply.RoundTripMs : -1;

    /// <summary>The wait between two pings to one target after <paramref name="errorsInARow"/> pings that could not be sent.</summary>
    internal static int IntervalAfter(int errorsInARow, int intervalMs) =>
        errorsInARow >= ErrorsBeforeBackoff ? Math.Max(intervalMs, BackoffMs) : intervalMs;

    /// <summary>The IPv4 default gateway of the interface whose subnet holds the speaker.</summary>
    internal static IPAddress? RouterFor(IPAddress target)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var ip = nic.GetIPProperties();
                bool local = ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                          SameSubnet(a.Address, target, a.IPv4Mask));
                if (!local) continue;
                return ip.GatewayAddresses.Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }

    internal static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes(), m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    /// <summary>One answer to an ICMP echo request, or why there was none.</summary>
    internal readonly record struct EchoResult(IPStatus Status, long RoundTripMs);

    /// <summary>Sends ICMP echo requests, blocking until the answer or the timeout. Throws <see cref="PingException"/> when it cannot send.</summary>
    internal interface IEcho : IDisposable
    {
        EchoResult Send(IPAddress target, int timeoutMs);
    }

    private sealed class SystemEcho : IEcho
    {
        private readonly Ping _ping = new();
        private readonly byte[] _payload = new byte[32];
        private readonly PingOptions _options = new() { DontFragment = true };

        public EchoResult Send(IPAddress target, int timeoutMs)
        {
            var reply = _ping.Send(target, timeoutMs, _payload, _options);
            return new EchoResult(reply.Status, reply.RoundtripTime);
        }

        public void Dispose() => _ping.Dispose();
    }

    private sealed class Probe(string name, IPAddress address, PingTarget target, Action<PingTarget, long> spike, int intervalMs, Func<IEcho> newEcho)
    {
        private readonly object _lock = new();
        private readonly List<long> _rtts = new();
        private int _sent, _lost, _failed;
        private volatile bool _watching;

        public PingTarget Target => target;

        /// <summary>Answered at least once and still pinged.</summary>
        public bool Watching => _watching;

        public void Run(ManualResetEventSlim stop)
        {
            IEcho? echo = null;
            int answered = 0, tries = 0, errorsInARow = 0;
            long lastSpikeLog = 0, lastErrorLog = 0;
            long worstSince = -1; // worst spike since the last spike log (-1: none; long.MaxValue: a lost ping)
            IPStatus? lostBecause = null; // a lost ping's status other than TimedOut, since the last spike log
            var next = Stopwatch.GetTimestamp();
            try
            {
                while (!stop.IsSet)
                {
                    long rtt = -1;
                    bool sent = false;
                    try
                    {
                        echo ??= newEcho();
                        var reply = echo.Send(address, IcmpTimeoutMs);
                        sent = true;
                        rtt = RoundTrip(reply);
                        if (rtt < 0 && reply.Status is not (IPStatus.Success or IPStatus.TimedOut)) lostBecause = reply.Status;
                        if (errorsInARow >= ErrorsBeforeBackoff) Log.Info($"network watch: pinging {name} works again");
                        errorsInARow = 0;
                    }
                    catch (PingException ex)
                    {
                        // Not sent at all: nothing the network did. Start over with a new ICMP handle.
                        echo?.Dispose();
                        echo = null;
                        errorsInARow++;
                        lock (_lock) _failed++;
                        long t = Stopwatch.GetTimestamp();
                        if (errorsInARow == ErrorsBeforeBackoff)
                            Log.Warn($"network watch: {errorsInARow} pings to {name} in a row could not be sent ({Describe(ex)}); trying once a second");
                        else if (errorsInARow < ErrorsBeforeBackoff && t - lastErrorLog >= 10 * Stopwatch.Frequency)
                        {
                            Log.Warn($"network watch: a ping to {name} could not be sent: {Describe(ex)}");
                            lastErrorLog = t;
                        }
                    }
                    tries++;
                    if (rtt >= 0 && answered++ == 0) _watching = true;
                    if (tries == TriesBeforeGivingUp && answered == 0)
                    {
                        Log.Info($"network watch: {name} ({address}) does not answer ping; not watching it");
                        return;
                    }
                    if (sent)
                    {
                        lock (_lock)
                        {
                            _sent++;
                            if (rtt < 0) _lost++;
                            else _rtts.Add(rtt);
                        }

                        if (rtt < 0 || rtt > SpikeMs)
                        {
                            worstSince = Math.Max(worstSince, rtt < 0 ? long.MaxValue : rtt);
                            if (answered > 0) spike(target, rtt);
                        }
                    }
                    long now = Stopwatch.GetTimestamp();
                    if (worstSince >= 0 && now - lastSpikeLog >= Stopwatch.Frequency)
                    {
                        Log.Info(worstSince != long.MaxValue ? $"network: ping to {name} took {worstSince} ms"
                            : lostBecause is { } status ? $"network: ping to {name} lost ({status})"
                            : $"network: ping to {name} lost (no answer within {TimeoutMs} ms)");
                        lastSpikeLog = now;
                        worstSince = -1;
                        lostBecause = null;
                    }

                    next += IntervalAfter(errorsInARow, intervalMs) * Stopwatch.Frequency / 1000;
                    long wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    if (wait > 0)
                    {
                        if (stop.Wait((int)wait)) break;
                    }
                    else next = Stopwatch.GetTimestamp(); // a slow reply: don't burst to catch up
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"network watch ({name}): {ex.Message}");
            }
            finally
            {
                _watching = false;
                echo?.Dispose();
            }
        }

        private static string Describe(Exception ex) =>
            ex.InnerException is { } inner ? $"{inner.GetType().Name}: {inner.Message}" : ex.Message;

        public string TakeSummary()
        {
            long[] rtts;
            int sent, lost, failed;
            lock (_lock)
            {
                rtts = _rtts.ToArray();
                (sent, lost, failed) = (_sent, _lost, _failed);
                _rtts.Clear();
                _sent = _lost = _failed = 0;
            }
            string notSent = failed > 0 ? $" unsent={failed}" : "";
            if (sent == 0) return "-" + notSent;
            if (rtts.Length == 0) return $"- lost={lost}/{sent}" + notSent;
            Array.Sort(rtts);
            long Pct(double p) => rtts[Math.Min(rtts.Length - 1, (int)(p * rtts.Length))];
            return $"{Pct(0.5)}/{Pct(0.99)}/{rtts[^1]}ms lost={lost}/{sent}" + notSent;
        }
    }
}
