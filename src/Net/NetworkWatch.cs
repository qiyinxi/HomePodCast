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
/// </summary>
internal sealed class NetworkWatch : IDisposable
{
    public const int IntervalMs = 100;
    public const int SpikeMs = 30;
    public const int TimeoutMs = 500;

    private readonly CancellationTokenSource _cts = new();
    private readonly Probe _speaker;
    private readonly Probe? _router;

    public NetworkWatch(IPAddress speaker, IPAddress? router = null)
    {
        _speaker = new Probe("the speaker", speaker);
        router ??= RouterFor(speaker);
        if (router != null && !router.Equals(speaker)) _router = new Probe("the router", router);
        _ = Task.Run(() => _speaker.RunAsync(_cts.Token));
        if (_router != null) _ = Task.Run(() => _router.RunAsync(_cts.Token));
    }

    /// <summary>"ping=3/9/41ms lost=0/600 router=1/2/5ms lost=0/600" (median/p99/max since the last call).</summary>
    public string TakeSummary() =>
        "ping=" + _speaker.TakeSummary() + (_router is { } r ? " router=" + r.TakeSummary() : "");

    public void Dispose() => _cts.Cancel(); // not disposed: the loops may still be reading its token

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

    private sealed class Probe(string name, IPAddress address)
    {
        private readonly object _lock = new();
        private readonly List<long> _rtts = new();
        private int _sent, _lost;

        public async Task RunAsync(CancellationToken ct)
        {
            using var ping = new Ping();
            var payload = new byte[32];
            var options = new PingOptions { DontFragment = true };
            int answered = 0, tries = 0;
            long lastSpikeLog = 0;
            long worstSince = -1; // worst spike since the last spike log (-1: none; long.MaxValue: a lost ping)
            var next = Stopwatch.GetTimestamp();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    long rtt;
                    try
                    {
                        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(TimeoutMs), payload, options, ct);
                        rtt = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
                    }
                    catch (PingException)
                    {
                        rtt = -1;
                    }
                    tries++;
                    if (rtt >= 0) answered++;
                    if (tries == 20 && answered == 0)
                    {
                        Log.Info($"network watch: {name} ({address}) does not answer ping; not watching it");
                        return;
                    }
                    lock (_lock)
                    {
                        _sent++;
                        if (rtt < 0) _lost++;
                        else _rtts.Add(rtt);
                    }

                    if (rtt < 0 || rtt > SpikeMs) worstSince = Math.Max(worstSince, rtt < 0 ? long.MaxValue : rtt);
                    long now = Stopwatch.GetTimestamp();
                    if (worstSince >= 0 && now - lastSpikeLog >= Stopwatch.Frequency)
                    {
                        Log.Info(worstSince == long.MaxValue
                            ? $"network: ping to {name} lost (no answer within {TimeoutMs} ms)"
                            : $"network: ping to {name} took {worstSince} ms");
                        lastSpikeLog = now;
                        worstSince = -1;
                    }

                    next += IntervalMs * Stopwatch.Frequency / 1000;
                    long wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    if (wait > 0) await Task.Delay((int)wait, ct);
                    else next = Stopwatch.GetTimestamp(); // a slow reply: don't burst to catch up
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"network watch ({name}): {ex.Message}");
            }
        }

        public string TakeSummary()
        {
            long[] rtts;
            int sent, lost;
            lock (_lock)
            {
                rtts = _rtts.ToArray();
                (sent, lost) = (_sent, _lost);
                _rtts.Clear();
                _sent = _lost = 0;
            }
            if (sent == 0) return "-";
            if (rtts.Length == 0) return $"- lost={lost}/{sent}";
            Array.Sort(rtts);
            long Pct(double p) => rtts[Math.Min(rtts.Length - 1, (int)(p * rtts.Length))];
            return $"{Pct(0.5)}/{Pct(0.99)}/{rtts[^1]}ms lost={lost}/{sent}";
        }
    }
}
