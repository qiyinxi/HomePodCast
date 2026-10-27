using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace HomePodCast.Net;

/// <summary>
/// Isolated unicast master for non-Apple sessions (experimental since 1.1).
/// Used only by third-party sessions. The HomePod NTP timing server remains independent.
/// </summary>
public sealed class PtpMaster : IDisposable
{
    private readonly UdpClient _events, _general;
    private sealed record Peer(IPEndPoint Events, IPEndPoint General);
    private readonly object _peersLock = new();
    private Peer[] _peers = [];
    private readonly bool _coordinatedClock;
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _ownAnnounce;
    private readonly Task[] _tasks;
    private long _betterUntil; // when we are (or were) master again: a better clock's hold ends or it withdrew
    private readonly Dictionary<(Peer Source, ulong Identity), long> _betterClocks = [];
    private readonly object _election = new();
    private int _disposed;

    public ulong ClockId { get; }
    public byte Domain { get; }
    public int EventPort => ((IPEndPoint)_events.Client.LocalEndPoint!).Port;
    public int GeneralPort => ((IPEndPoint)_general.Client.LocalEndPoint!).Port;
    public bool IsMaster => Stopwatch.GetTimestamp() >= Interlocked.Read(ref _betterUntil);
    public long SyncMessagesSent { get; private set; }
    public long DelayRequestsAnswered { get; private set; }
    public event Action<Exception>? Failed;
    internal int PeerCount => Volatile.Read(ref _peers).Length;
    internal int CompetingClockCount { get { lock (_election) return _betterClocks.Values.Count(t => t > Stopwatch.GetTimestamp()); } }
    /// <summary>
    /// How long one announce from a better clock holds us off: PTP's announce receipt timeout, three announce intervals
    /// of 1 s. Tests lengthen it so that only the clock's withdrawal ends the hold, however long the test process stalls.
    /// </summary>
    internal TimeSpan BetterClockHold { get; init; } = TimeSpan.FromSeconds(3);
    internal ulong SessionNanoseconds => TimeAt(MediaClock.Now);
    internal ulong TimeAt(long qpcTicks)
    {
        if (_coordinatedClock) return NanosecondsAt(qpcTicks);
        long delta = qpcTicks - ClockBase;
        ulong elapsed = (ulong)((UInt128)(ulong)Math.Abs(delta) * 1_000_000_000 / (ulong)Stopwatch.Frequency);
        return delta >= 0 ? checked(EpochBase + elapsed) : checked(EpochBase - elapsed);
    }
    private static readonly long ClockBase = Stopwatch.GetTimestamp();
    private static readonly ulong EpochBase = checked((ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1_000_000);

    /// <summary>One monotonic clock for PTP timestamps and future rate-anchor messages.</summary>
    public static ulong NowNanoseconds => EpochBase + (ulong)((UInt128)(ulong)(Stopwatch.GetTimestamp() - ClockBase)
        * 1_000_000_000 / (ulong)Stopwatch.Frequency);

    /// <summary>Coordinated receivers project the same QPC instant as the existing NTP outputs.</summary>
    internal static ulong NanosecondsAt(long qpcTicks) => NanosecondsFromNtp(MediaClock.NtpAt(qpcTicks));

    internal static ulong NanosecondsFromNtp(ulong timestamp)
    {
        const ulong unixEpoch = 2_208_988_800;
        ulong seconds = timestamp >> 32;
        // NTP's low seconds wrap in 2036. The sender's current Unix era spans 1970..2106.
        if (seconds < unixEpoch) seconds += 1UL << 32;
        return checked((seconds - unixEpoch) * 1_000_000_000
            + (ulong)((UInt128)(timestamp & uint.MaxValue) * 1_000_000_000 >> 32));
    }

    /// <summary>Let the receiver's wake-up election finish before anchoring or consuming live audio.</summary>
    internal async Task WaitForStableMasterAsync(CancellationToken ct)
    {
        long began = Stopwatch.GetTimestamp(), deadline = began + Stopwatch.Frequency * 2;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Stable since the last hold ended, as the election recorded it, not as earlier polls happened to see it.
            // 2026-10-15: a CI test process stalled for 2.9 s between two polls; the wait woke past its budget and
            // gave up on a clock nobody had disputed. A dispute that comes and goes between polls counts just the same.
            long now = Stopwatch.GetTimestamp();
            if (now - Math.Max(began, Interlocked.Read(ref _betterUntil)) >= Stopwatch.Frequency / 4) return;
            if (now >= deadline) throw new IOException("PTP could not establish a stable master clock");
            await Task.Delay(20, ct);
        }
    }

    /// <summary>Only an explicit third-party session may instantiate this; tests use loopback and private ports.</summary>
    public PtpMaster(IPAddress local, IPEndPoint peerEvents, IPEndPoint peerGeneral, bool thirdPartySession,
        int eventPort = 319, int generalPort = 320, ulong? clockId = null, byte domain = 0)
        : this(local, peerEvents, peerGeneral, thirdPartySession, eventPort, generalPort, clockId, domain, false) { }

    internal PtpMaster(IPAddress local, IPEndPoint peerEvents, IPEndPoint peerGeneral, bool thirdPartySession,
        int eventPort, int generalPort, ulong? clockId, byte domain, bool coordinatedClock)
    {
        if (!thirdPartySession) throw new ArgumentException("PTP is restricted to third-party sessions", nameof(thirdPartySession));
        if (!peerEvents.Address.Equals(peerGeneral.Address)) throw new ArgumentException("PTP peer addresses must agree");
        _coordinatedClock = coordinatedClock;
        AddPeer(peerEvents, peerGeneral);
        ClockId = clockId ?? BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        Domain = domain;
        _ownAnnounce = PtpPackets.AnnounceMessage(ClockId, 0, Domain);
        // A receive error stops the clock for every receiver sharing it (PtpClockPool), so a peer whose 319/320 is closed
        // for a moment must not raise one: ReceiveLoop would end on the port unreachable that Windows reports.
        _events = new UdpClient(new IPEndPoint(local, eventPort)).IgnorePortUnreachable();
        try { _general = new UdpClient(new IPEndPoint(local, generalPort)).IgnorePortUnreachable(); }
        catch { _events.Dispose(); _stop.Dispose(); throw; }
        _tasks = [Task.Run(SendLoop), Task.Run(() => ReceiveLoop(_events)), Task.Run(() => ReceiveLoop(_general))];
    }

    internal void AddPeer(IPEndPoint events, IPEndPoint general)
    {
        if (!events.Address.Equals(general.Address)) throw new ArgumentException("PTP peer addresses must agree");
        var peer = new Peer(new(events.Address, events.Port), new(general.Address, general.Port));
        lock (_peersLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_peers.Contains(peer)) return;
            Volatile.Write(ref _peers, [.. _peers, peer]);
        }
    }

    internal void RemovePeer(IPEndPoint events, IPEndPoint general)
    {
        lock (_peersLock)
        {
            Volatile.Write(ref _peers, _peers.Where(p => !p.Events.Equals(events) || !p.General.Equals(general)).ToArray());
            lock (_election)
            {
                foreach (var key in _betterClocks.Keys.Where(k => k.Source.Events.Equals(events) && k.Source.General.Equals(general)).ToArray())
                    _betterClocks.Remove(key);
                UpdateHold(Stopwatch.GetTimestamp());
            }
        }
    }

    private Peer? FindPeer(IPEndPoint endpoint)
    {
        var peers = Volatile.Read(ref _peers);
        return peers.FirstOrDefault(p => p.Events.Equals(endpoint) || p.General.Equals(endpoint))
            ?? peers.FirstOrDefault(p => p.Events.Address.Equals(endpoint.Address));
    }

    private async Task SendLoop()
    {
        ushort sequence = 0;
        int ticks = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(125));
            do
            {
                if (!IsMaster) continue;
                bool announce = ticks++ % 8 == 0;
                foreach (var peer in Volatile.Read(ref _peers))
                {
                    if (announce)
                        await _general.SendAsync(PtpPackets.AnnounceMessage(ClockId, sequence, Domain), peer.General, _stop.Token);
                    var sync = PtpPackets.SyncMessage(ClockId, sequence, Domain);
                    ulong sent = SessionNanoseconds;
                    await _events.SendAsync(sync, peer.Events, _stop.Token);
                    await _general.SendAsync(PtpPackets.FollowUpMessage(ClockId, sequence, sent, Domain), peer.General, _stop.Token);
                    SyncMessagesSent++;
                }
                sequence++;
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); _stop.Cancel(); }
    }

    private async Task ReceiveLoop(UdpClient socket)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var datagram = await socket.ReceiveAsync(_stop.Token);
                ulong received = SessionNanoseconds;
                var peer = FindPeer(datagram.RemoteEndPoint);
                if (peer == null || !PtpPackets.Valid(datagram.Buffer, Domain)) continue;
                byte type = (byte)(datagram.Buffer[0] & 15);
                if (type == PtpPackets.Announce)
                {
                    ulong identity = BinaryPrimitives.ReadUInt64BigEndian(datagram.Buffer.AsSpan(53));
                    lock (_election)
                    {
                        if (!Volatile.Read(ref _peers).Contains(peer)) continue;
                        long now = Stopwatch.GetTimestamp();
                        var candidate = (peer, identity);
                        if (PtpPackets.CompareGrandmasters(datagram.Buffer, _ownAnnounce) < 0)
                            _betterClocks[candidate] = now + (long)(BetterClockHold.TotalSeconds * Stopwatch.Frequency);
                        // 2026-10-14: nqptp 1.2.4 wakes a master with a better announce, then a worse
                        // one 150 ms later. That same clock withdrawing its candidacy ends the hold now,
                        // which is when the wait for a stable master starts counting again.
                        else _betterClocks.Remove(candidate);
                        UpdateHold(now);
                    }
                }
                else if (type == PtpPackets.DelayRequest && IsMaster)
                {
                    await _general.SendAsync(PtpPackets.DelayResponseMessage(ClockId, datagram.Buffer, received, Domain), peer.General, _stop.Token);
                    DelayRequestsAnswered++;
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); _stop.Cancel(); }
    }

    private void UpdateHold(long now)
    {
        foreach (var key in _betterClocks.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _betterClocks.Remove(key);
        long until = _betterClocks.Count > 0 ? _betterClocks.Values.Max() : Math.Min(Interlocked.Read(ref _betterUntil), now);
        Interlocked.Exchange(ref _betterUntil, until);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _events.Dispose(); _general.Dispose();
        try { Task.WaitAll(_tasks, TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}

internal sealed record PtpPorts(int LocalEvent, int LocalGeneral, int PeerEvent, int PeerGeneral)
{
    public static PtpPorts Standard { get; } = new(319, 320, 319, 320);
}
