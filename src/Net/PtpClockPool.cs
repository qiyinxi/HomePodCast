using System.Net;
using System.Security.Cryptography;

namespace HomePodCast.Net;

/// <summary>One group's sessions share the PTP sockets for each selected local interface.</summary>
internal sealed class PtpClockPool : IDisposable
{
    private sealed record Peer(IPEndPoint Events, IPEndPoint General);
    private sealed class Entry(PtpMaster clock)
    {
        public readonly PtpMaster Clock = clock;
        public readonly Dictionary<Peer, int> Peers = [];
        public int References;
    }
    private readonly object _lock = new();
    private readonly Dictionary<(IPAddress Local, int Event, int General), Entry> _entries = [];
    private readonly ulong _clockId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    private bool _disposed;

    public PtpClockLease Acquire(IPAddress local, IPAddress peerAddress, PtpPorts? ports = null)
    {
        ports ??= PtpPorts.Standard;
        var key = (local, ports.LocalEvent, ports.LocalGeneral);
        var peer = new Peer(new(peerAddress, ports.PeerEvent), new(peerAddress, ports.PeerGeneral));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new(new PtpMaster(local, peer.Events, peer.General, true,
                    ports.LocalEvent, ports.LocalGeneral, _clockId, 0, coordinatedClock: true));
                _entries.Add(key, entry);
            }
            else if (!entry.Peers.ContainsKey(peer)) entry.Clock.AddPeer(peer.Events, peer.General);
            entry.Peers[peer] = entry.Peers.GetValueOrDefault(peer) + 1;
            entry.References++;
            return new(entry.Clock, () => Release(key, peer));
        }
    }

    private void Release((IPAddress Local, int Event, int General) key, Peer peer)
    {
        PtpMaster? close = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            int remaining = entry.Peers[peer] - 1;
            if (remaining == 0)
            {
                entry.Peers.Remove(peer);
                entry.Clock.RemovePeer(peer.Events, peer.General);
            }
            else entry.Peers[peer] = remaining;
            if (--entry.References == 0) { _entries.Remove(key); close = entry.Clock; }
        }
        close?.Dispose();
    }

    public void Dispose()
    {
        PtpMaster[] clocks;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            clocks = _entries.Values.Select(e => e.Clock).ToArray();
            _entries.Clear();
        }
        foreach (var clock in clocks) clock.Dispose();
    }
}

/// <summary>A session releases its own peer and subscription; other receivers keep their shared clock.</summary>
internal sealed class PtpClockLease : IDisposable
{
    private Action? _release;
    private Action<Exception>? _failed;
    public PtpMaster Clock { get; }
    public ulong ClockId => Clock.ClockId;
    public event Action<Exception>? Failed { add => _failed += value; remove => _failed -= value; }
    public PtpClockLease(PtpMaster clock, Action release)
    {
        Clock = clock; _release = release;
        Clock.Failed += OnFailed;
    }
    public static PtpClockLease Own(PtpMaster clock) => new(clock, clock.Dispose);
    public Task WaitForStableMasterAsync(CancellationToken ct) => Clock.WaitForStableMasterAsync(ct);
    private void OnFailed(Exception error) => _failed?.Invoke(error);
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release == null) return;
        Clock.Failed -= OnFailed;
        _failed = null;
        release();
    }
}
