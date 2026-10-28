using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HomePodCast.Net;

/// <summary>A clock the 5.1 sender paces on (experimental since 1.4): the HomePod's PTP timeline as seen from this PC.</summary>
internal interface IPtpTimeline
{
    /// <summary>networkTimeTimelineID for the rate anchor; null while unknown.</summary>
    ulong? TimelineId { get; }

    /// <summary>Timeline time minus <see cref="PtpClockObserver.LocalNs"/>, in nanoseconds; null while unknown.</summary>
    long? OffsetNs { get; }

    /// <summary>Non-null (translated): the session must end, because the timeline stopped or changed.</summary>
    string? Problem { get; }
}

/// <summary>
/// Follows a HomePod's own PTP clock for the 5.1 surround path (experimental since 1.4). Research and field test
/// 2026-10-28: a standalone OS 27 HomePod keeps announcing its own clock (priority1 248) and never follows a third-party
/// grandmaster, so the rate anchor is set in its timeline. Binds the event and general ports (319/320 in production)
/// and, exactly as in the verified test, sends a Delay_Req every 250 ms from the event port and every second an Announce
/// from the general port that loses every election (priority1/2 250, as a Mac announces). The HomePod never answered the
/// Delay_Req; whether either is needed at all (the firewall's reply paths, telling it a PTP port is here) is untested.
/// </summary>
internal sealed class PtpFollower : IPtpTimeline, IDisposable
{
    /// <summary>No Sync for this long after settling is worth a warning in the log; <see cref="Problem"/> waits for staleAfter.</summary>
    private static readonly long WarnAfterNs = 5_000_000_000;

    private readonly UdpClient _events, _general;
    private readonly IPEndPoint _peerEvents, _peerGeneral;
    private readonly PtpClockObserver _observer;
    private readonly ulong _clockId;
    private readonly long _staleAfterNs;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _tasks;
    private long _delayRequests, _announces;
    private int _disposed, _failed;
    private bool _warnedStale;

    public event Action<string>? Failed;

    private PtpFollower(UdpClient events, UdpClient general, IPAddress peer, ulong clockId, PtpPorts ports, TimeSpan staleAfter)
    {
        _events = events; _general = general; _clockId = clockId;
        _peerEvents = new(peer, ports.PeerEvent); _peerGeneral = new(peer, ports.PeerGeneral);
        _staleAfterNs = staleAfter.Ticks * 100;
        _observer = new PtpClockObserver(peer, clockId);
        _tasks = [Task.Run(SendLoop), Task.Run(() => ReceiveLoop(_events, LocalEventPort)), Task.Run(() => ReceiveLoop(_general, LocalGeneralPort))];
    }

    public int LocalEventPort => ((IPEndPoint)_events.Client.LocalEndPoint!).Port;
    public int LocalGeneralPort => ((IPEndPoint)_general.Client.LocalEndPoint!).Port;

    /// <summary>
    /// Binds <paramref name="ports"/>' local event and general ports on <paramref name="local"/>. While they are held (our
    /// own previous session still letting go: Stop gives up waiting after 3 s; or another program) it retries every
    /// 100 ms for 3 s, then refuses 5.1 with a reason. WSAEACCES is what Windows answers for a port another program holds
    /// exclusively, so it counts as held too.
    /// </summary>
    /// <exception cref="SurroundUnavailableException">The ports stayed in use.</exception>
    public static PtpFollower Bind(IPAddress local, IPAddress peer, ulong clockId, PtpPorts ports, TimeSpan staleAfter)
    {
        long deadline = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
        while (true)
        {
            UdpClient? events = null, general = null;
            try
            {
                events = new UdpClient(new IPEndPoint(local, ports.LocalEvent)).IgnorePortUnreachable();
                general = new UdpClient(new IPEndPoint(local, ports.LocalGeneral)).IgnorePortUnreachable();
                return new PtpFollower(events, general, peer, clockId, ports, staleAfter);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                events?.Dispose(); general?.Dispose();
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    Log.Warn($"PTP: UDP {local}:{ports.LocalEvent} and :{ports.LocalGeneral} stayed in use for 3 s ({ex.SocketErrorCode}); no 5.1");
                    throw new SurroundUnavailableException(L.T("UDP 端口 319 和 320 被其他程序占用"));
                }
                Thread.Sleep(100);
            }
            catch
            {
                events?.Dispose(); general?.Dispose();
                throw;
            }
        }
    }

    public ulong? TimelineId => _observer.TimelineId;
    public long? OffsetNs => _observer.OffsetNs;
    public string? Problem => _observer.ProblemAt(PtpClockObserver.LocalNs(Stopwatch.GetTimestamp()), _staleAfterNs);

    /// <summary>The HomePod's time at a QPC instant, in nanoseconds; null while the offset is unknown.</summary>
    public ulong? TimeAt(long qpc) => _observer.OffsetNs is { } offset ? (ulong)(PtpClockObserver.LocalNs(qpc) + offset) : null;

    /// <summary>Change of the offset since settling, in ppm of the time since: QPC against the HomePod's clock (stats only).</summary>
    public double? DriftPpm => _observer.DriftPpm(PtpClockObserver.LocalNs(Stopwatch.GetTimestamp()));

    /// <summary>Milliseconds since the last Sync sample; null before the first.</summary>
    public long? SyncAgeMs => _observer.SampleAgeNs(PtpClockObserver.LocalNs(Stopwatch.GetTimestamp())) is { } age ? age / 1_000_000 : null;

    /// <summary>Counts, grandmaster, offset and age, for the log and the CLI.</summary>
    public string Describe =>
        $"{_observer.Describe(PtpClockObserver.LocalNs(Stopwatch.GetTimestamp()))}; sent delay_req {Interlocked.Read(ref _delayRequests)}, " +
        $"announce {Interlocked.Read(ref _announces)}";

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for an offset and a timeline; true once both are known, and from then on a
    /// stopped or changed timeline is a <see cref="Problem"/>.
    /// </summary>
    public async Task<bool> SettleAsync(TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (_observer.OffsetNs != null && _observer.TimelineId is { } timeline)
            {
                _observer.MarkSettled(PtpClockObserver.LocalNs(Stopwatch.GetTimestamp()));
                Log.Info($"PTP: following the HomePod's clock 0x{timeline:X16} after {watch.ElapsedMilliseconds} ms; {Describe}");
                return true;
            }
            if (watch.Elapsed >= timeout)
            {
                Log.Warn($"PTP: no Sync and Follow_Up from the HomePod within {timeout.TotalMilliseconds:F0} ms; {Describe}");
                return false;
            }
            await Task.Delay(50, ct);
        }
    }

    private async Task SendLoop()
    {
        ushort sequence = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                var request = PtpPackets.Header(PtpPackets.DelayRequest, 44, _clockId, sequence, 0, 0x7f);
                _observer.DelayRequestSent(sequence, PtpClockObserver.LocalNs(Stopwatch.GetTimestamp()));
                await _events.SendAsync(request, _peerEvents, _stop.Token);
                Interlocked.Increment(ref _delayRequests);
                if (sequence % 4 == 0)
                {
                    await _general.SendAsync(PtpPackets.AnnounceMessage(_clockId, (ushort)(sequence / 4), 0, priority: 250), _peerGeneral, _stop.Token);
                    Interlocked.Increment(ref _announces);
                }
                sequence++;
                WatchStaleness();
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>A warning once a settled clock has been quiet for 5 s, and a note when it comes back.</summary>
    private void WatchStaleness()
    {
        if (!_observer.Settled || _observer.SampleAgeNs(PtpClockObserver.LocalNs(Stopwatch.GetTimestamp())) is not { } age) return;
        if (age > WarnAfterNs && !_warnedStale)
        {
            _warnedStale = true;
            Log.Warn($"PTP: no Sync from the HomePod for {age / 1e9:F1} s (the session ends after {_staleAfterNs / 1e9:F0} s); {Describe}");
        }
        else if (age <= WarnAfterNs && _warnedStale)
        {
            _warnedStale = false;
            Log.Info("PTP: Sync from the HomePod again");
        }
    }

    private async Task ReceiveLoop(UdpClient socket, int localPort)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var datagram = await socket.ReceiveAsync(_stop.Token);
                _observer.Observe(datagram.RemoteEndPoint, datagram.Buffer, localPort);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        string reason = $"PTP {ex.GetType().Name}: {ex.Message}";
        Log.Warn($"PTP follower stopped: {reason}");
        Failed?.Invoke(reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _events.Dispose(); _general.Dispose();
        try { Task.WaitAll(_tasks, TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}

/// <summary>
/// The HomePod's timeline worked out from its PTP traffic to our ports: Sync with Follow_Up (two-step) or one-step Sync,
/// Announce for the grandmaster, Delay_Resp when it ever answers. Everything from other hosts, and anything that isn't
/// PTPv2, is counted and not used. Thread-safe; <see cref="Observe"/> never throws, and nothing is logged under the lock.
/// </summary>
internal sealed class PtpClockObserver(IPAddress peer, ulong ownClockId)
{
    private const int Window = 32, Pending = 64;
    private readonly object _lock = new();
    private readonly long[] _counts = new long[16];
    private long _otherHosts, _invalid;
    private readonly HashSet<int> _seenTypes = [];
    private readonly HashSet<IPAddress> _otherAddresses = [];
    // Pending two-step Syncs and our Delay_Reqs, evicted in the order they came: their u16 sequence wraps, so neither the
    // dictionary's order nor the smallest key says which is oldest.
    private readonly Dictionary<(ulong Clock, ushort Sequence), long> _syncs = [];
    private readonly Queue<(ulong Clock, ushort Sequence)> _syncOrder = new();
    private readonly Dictionary<ushort, long> _delayRequests = [];
    private readonly Queue<ushort> _delayOrder = new();
    private readonly Queue<(long OneWay, long LocalNs)> _samples = new();
    private readonly Queue<long> _pathDelays = new();
    private long? _lastOneWay, _lastSampleNs;
    private ulong? _grandmaster, _syncClock;
    private bool _settled;
    private ulong? _settledTimeline;
    private long _settledOffset, _settledAtNs;

    public static long LocalNs(long qpc) => (long)((Int128)qpc * 1_000_000_000 / Stopwatch.Frequency);

    public static long QpcAt(long localNs) => (long)((Int128)localNs * Stopwatch.Frequency / 1_000_000_000);

    /// <summary>The grandmaster from its Announce, else the clock its Sync comes from.</summary>
    public ulong? TimelineId { get { lock (_lock) return _grandmaster ?? _syncClock; } }

    /// <summary>
    /// The largest of the last 32 one-way samples (its send time minus our receive time, which is the offset less the
    /// path delay) plus the smallest measured path delay, when a Delay_Resp ever gave one. HomePods send none, so the
    /// offset is low by the one-way delay (0.3 ms to a few ms on a LAN), the same for every block: harmless.
    /// </summary>
    public long? OffsetNs { get { lock (_lock) return Offset(); } }

    public bool Settled { get { lock (_lock) return _settled; } }

    private long? Offset() =>
        _samples.Count == 0 ? null : _samples.Max(s => s.OneWay) + (_pathDelays.Count > 0 ? Math.Max(0, _pathDelays.Min()) : 0);

    public void DelayRequestSent(ushort sequence, long localNs)
    {
        lock (_lock)
        {
            _delayRequests[sequence] = localNs;
            _delayOrder.Enqueue(sequence);
            while (_delayOrder.Count > Pending) _delayRequests.Remove(_delayOrder.Dequeue());
        }
    }

    /// <summary>The settled state: from now on a changed grandmaster or a long silence is a problem.</summary>
    internal void MarkSettled(long nowNs)
    {
        lock (_lock)
        {
            _settled = true;
            _settledTimeline = _grandmaster ?? _syncClock;
            _settledOffset = Offset() ?? 0;
            _settledAtNs = nowNs;
        }
    }

    /// <summary>
    /// After settling: M3 as soon as the grandmaster differs from the settled one (the anchor's timeline is gone), M2 when
    /// no sample came for <paramref name="staleAfterNs"/>. Null before settling and while all is well.
    /// </summary>
    internal string? ProblemAt(long nowNs, long staleAfterNs)
    {
        bool changed, stale;
        lock (_lock)
        {
            if (!_settled) return null;
            changed = (_grandmaster ?? _syncClock) != _settledTimeline;
            stale = _lastSampleNs is not { } last || nowNs - last > staleAfterNs;
        }
        return changed ? L.T("HomePod 的 PTP 时钟变了") : stale ? L.T("HomePod 的 PTP 对时中断了") : null;
    }

    internal long? SampleAgeNs(long nowNs) { lock (_lock) return _lastSampleNs is { } last ? nowNs - last : null; }

    internal double? DriftPpm(long nowNs)
    {
        lock (_lock)
        {
            if (!_settled || Offset() is not { } offset || nowNs - _settledAtNs < 1_000_000_000) return null;
            return (offset - _settledOffset) * 1e6 / (nowNs - _settledAtNs);
        }
    }

    internal string Describe(long nowNs)
    {
        lock (_lock)
        {
            long? offset = Offset();
            return $"PTP rx sync {_counts[PtpPackets.Sync]}, follow_up {_counts[PtpPackets.FollowUp]}, announce {_counts[PtpPackets.Announce]}, " +
                   $"delay_resp {_counts[PtpPackets.DelayResponse]}, other hosts {_otherHosts}, invalid {_invalid}; " +
                   $"GM {(_grandmaster is { } gm ? $"0x{gm:X16}" : "none")}, sync clock {(_syncClock is { } sc ? $"0x{sc:X16}" : "none")}; " +
                   $"offset {(offset is { } o ? $"{o / 1e9:F6} s" : "none")}; sync age {(_lastSampleNs is { } last ? $"{(nowNs - last) / 1_000_000} ms" : "none")}";
        }
    }

    public void Observe(IPEndPoint from, byte[] packet, int localPort)
    {
        try { ObserveCore(from, packet, localPort, LocalNs(Stopwatch.GetTimestamp())); }
        catch (Exception ex) { Log.Debug($"PTP observer: {ex.GetType().Name}: {ex.Message}"); }
    }

    internal void ObserveCore(IPEndPoint from, byte[] packet, int localPort, long localNs)
    {
        string? note = null, change = null;
        lock (_lock)
        {
            if (!from.Address.Equals(peer))
            {
                _otherHosts++;
                if (_otherAddresses.Add(from.Address)) note = $"PTP from another host {from} to :{localPort} ({packet.Length} bytes): not used";
            }
            else if (packet.Length < PtpPackets.HeaderSize || (packet[1] & 15) != 2)
            {
                if (_invalid++ == 0) note = $"PTP from {from} to :{localPort}: not PTPv2 ({packet.Length} bytes)";
            }
            else note = ObservePtp(from, packet, localPort, localNs, out change);
        }
        if (note != null) Log.Debug(note);
        if (change != null) Log.Info(change);
    }

    /// <summary>Under the lock: the first packet of a type (Debug) and a grandmaster change (Info) come back for the log.</summary>
    private string? ObservePtp(IPEndPoint from, byte[] packet, int localPort, long localNs, out string? change)
    {
        change = null;
        int type = packet[0] & 15;
        _counts[type]++;
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(30));
        ulong clock = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(20));
        long correction = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(8)) >> 16;
        switch (type)
        {
            case PtpPackets.Sync when packet.Length >= 44:
                _syncClock = clock;
                if ((packet[6] & 2) != 0)
                {
                    _syncs[(clock, sequence)] = localNs;
                    _syncOrder.Enqueue((clock, sequence));
                    while (_syncOrder.Count > Pending) _syncs.Remove(_syncOrder.Dequeue());
                }
                else Sample(Timestamp(packet, 34) + correction, localNs);
                break;
            case PtpPackets.FollowUp when packet.Length >= 44:
                if (_syncs.Remove((clock, sequence), out long received)) Sample(Timestamp(packet, 34) + correction, received);
                break;
            case PtpPackets.DelayResponse when packet.Length >= 54:
                long t4 = Timestamp(packet, 34) + correction;
                ulong requester = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(44));
                if (requester == ownClockId && _delayRequests.Remove(sequence, out long t3) && _lastOneWay is { } oneWay)
                    Push(_pathDelays, ((t4 - t3) - oneWay) / 2);
                break;
            case PtpPackets.Announce when packet.Length >= 64:
                ulong grandmaster = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(53));
                if (_grandmaster != grandmaster)
                {
                    if (_grandmaster != null)
                        change = $"PTP: the HomePod's grandmaster changed from 0x{_grandmaster:X16} to 0x{grandmaster:X16} (priority1 {packet[47]})";
                    _grandmaster = grandmaster;
                }
                break;
        }
        return _seenTypes.Add(type)
            ? $"PTP first type {type} from {from} to :{localPort}: seq {sequence} clock 0x{clock:X16} length {packet.Length}"
            : null;
    }

    private void Sample(long master, long localNs)
    {
        _lastOneWay = master - localNs;
        _lastSampleNs = localNs;
        _samples.Enqueue((master - localNs, localNs));
        while (_samples.Count > Window) _samples.Dequeue();
    }

    private static void Push(Queue<long> queue, long value)
    {
        queue.Enqueue(value);
        while (queue.Count > Window) queue.Dequeue();
    }

    private static long Timestamp(byte[] packet, int offset) => (long)PtpPackets.ReadTimestamp(packet.AsSpan(offset));
}

/// <summary>5.1 surround can't be used with this speaker or on this PC right now; the message (translated) says why.</summary>
public sealed class SurroundUnavailableException(string message) : AirPlayException(message);
