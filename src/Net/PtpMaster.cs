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
    private readonly IPEndPoint _peerEvents, _peerGeneral;
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _ownAnnounce;
    private readonly Task[] _tasks;
    private long _betterUntil;
    private ulong _betterGrandmaster;
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
    private static readonly long ClockBase = Stopwatch.GetTimestamp();
    private static readonly ulong EpochBase = checked((ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1_000_000);

    /// <summary>One monotonic clock for PTP timestamps and future rate-anchor messages.</summary>
    public static ulong NowNanoseconds => EpochBase + (ulong)((UInt128)(ulong)(Stopwatch.GetTimestamp() - ClockBase)
        * 1_000_000_000 / (ulong)Stopwatch.Frequency);

    /// <summary>Let the receiver's wake-up election finish before anchoring or consuming live audio.</summary>
    internal async Task WaitForStableMasterAsync(CancellationToken ct)
    {
        long began = Stopwatch.GetTimestamp(), stable = began;
        while (Stopwatch.GetTimestamp() - began < Stopwatch.Frequency * 2)
        {
            ct.ThrowIfCancellationRequested();
            long now = Stopwatch.GetTimestamp();
            if (!IsMaster) stable = now;
            else if (now - stable >= Stopwatch.Frequency / 4) return;
            await Task.Delay(20, ct);
        }
        throw new IOException("PTP could not establish a stable master clock");
    }

    /// <summary>Only an explicit third-party session may instantiate this; tests use loopback and private ports.</summary>
    public PtpMaster(IPAddress local, IPEndPoint peerEvents, IPEndPoint peerGeneral, bool thirdPartySession,
        int eventPort = 319, int generalPort = 320, ulong? clockId = null, byte domain = 0)
    {
        if (!thirdPartySession) throw new ArgumentException("PTP is restricted to third-party sessions", nameof(thirdPartySession));
        if (!peerEvents.Address.Equals(peerGeneral.Address)) throw new ArgumentException("PTP peer addresses must agree");
        _peerEvents = peerEvents; _peerGeneral = peerGeneral;
        ClockId = clockId ?? BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        Domain = domain;
        _ownAnnounce = PtpPackets.AnnounceMessage(ClockId, 0, Domain);
        _events = new UdpClient(new IPEndPoint(local, eventPort));
        try { _general = new UdpClient(new IPEndPoint(local, generalPort)); }
        catch { _events.Dispose(); _stop.Dispose(); throw; }
        _tasks = [Task.Run(SendLoop), Task.Run(() => ReceiveLoop(_events)), Task.Run(() => ReceiveLoop(_general))];
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
                if (ticks++ % 8 == 0)
                    await _general.SendAsync(PtpPackets.AnnounceMessage(ClockId, sequence, Domain), _peerGeneral, _stop.Token);
                var sync = PtpPackets.SyncMessage(ClockId, sequence, Domain);
                ulong sent = NowNanoseconds; // software timestamp immediately before transmission
                await _events.SendAsync(sync, _peerEvents, _stop.Token);
                await _general.SendAsync(PtpPackets.FollowUpMessage(ClockId, sequence++, sent, Domain), _peerGeneral, _stop.Token);
                SyncMessagesSent++;
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
                ulong received = NowNanoseconds;
                if (!datagram.RemoteEndPoint.Address.Equals(_peerEvents.Address) || !PtpPackets.Valid(datagram.Buffer, Domain)) continue;
                byte type = (byte)(datagram.Buffer[0] & 15);
                if (type == PtpPackets.Announce)
                {
                    ulong identity = BinaryPrimitives.ReadUInt64BigEndian(datagram.Buffer.AsSpan(53));
                    lock (_election)
                    {
                        if (PtpPackets.CompareGrandmasters(datagram.Buffer, _ownAnnounce) < 0)
                        {
                            _betterGrandmaster = identity;
                            Interlocked.Exchange(ref _betterUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3);
                        }
                        // 2026-10-14: nqptp 1.2.4 wakes a master with a better announce, then a worse
                        // one 150 ms later. That same clock withdrawing its candidacy ends the hold.
                        else if (identity == _betterGrandmaster) Interlocked.Exchange(ref _betterUntil, 0);
                    }
                }
                else if (type == PtpPackets.DelayRequest && IsMaster)
                {
                    await _general.SendAsync(PtpPackets.DelayResponseMessage(ClockId, datagram.Buffer, received, Domain), _peerGeneral, _stop.Token);
                    DelayRequestsAnswered++;
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); _stop.Cancel(); }
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
