using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// The PTP timeline a realtime stream's sync packets are written in: the time (ns) at a MediaClock tick, the clock ID
/// that names the timeline, and whether it can still be used (our own clock: while it is the master).
/// </summary>
internal sealed record PtpTimeline(Func<long, ulong> TimeAt, Func<ulong> ClockId, Func<bool> Usable);

/// <summary>Third-party type 96 over PTP; independent of the HomePod RtpSender and NTP timeline.</summary>
internal sealed class PtpRealtimeSender : IDisposable
{
    private static readonly long LateLimit = MediaClock.FromMs(60);

    /// <summary>A stall longer than this ends the session (see <see cref="Send"/>); shorter ones are jumped over.</summary>
    private static readonly long GiveUpLimit = MediaClock.FromMs(2000);
    private readonly UdpClient _audio, _control;
    private readonly IPEndPoint _controlPeer;
    private readonly CounterCipher _cipher;
    private readonly AudioFifo _fifo;
    private readonly IAudioEffect? _effects;
    private readonly PtpTimeline _clock;
    private readonly uint _ssrc;
    private readonly int _latency;
    private readonly bool _alac;
    private readonly bool _modernStream;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ushort, byte[]> _history = new();
    private readonly Queue<ushort> _order = new();
    private readonly object _lifecycle = new();
    private Thread? _sender;
    private Task _resends = Task.CompletedTask;
    private long _packets, _skipped, _lateWakeups, _maxLateTicks;
    private long _retransmitted, _retransmitRequests;
    private int _disposed;
    private long? _startAt;
    private ReceiverPcmReader? _readAudio;
    private int _started;
    private int _stopping;
    [ThreadStatic] private static PtpRealtimeSender? _failureCallbackSender;
    private bool _senderStarted, _senderExited, _resendsExited = true, _cancelFinished, _resourcesDisposed;
    // Test barriers run outside the gate, so they can hold either side of a real cleanup interleaving.
    internal Action? BeforeSenderStartForTest { get; set; }
    internal Action? BeforeStopCancellationForTest { get; set; }
    public uint RtpBase { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public ushort FirstSeq { get; } = (ushort)RandomNumberGenerator.GetInt32(65536);
    public long StartedAt { get; private set; }
    public long PacketsSent => Interlocked.Read(ref _packets);

    /// <summary>Packets never sent because the sender jumped its timeline over them after a stall on this PC.</summary>
    public long SkippedPackets => Interlocked.Read(ref _skipped);

    /// <summary>Packets after the first that went out more than 2 ms after their time, and the latest of them (as RtpSender counts).</summary>
    public long LateWakeups => Interlocked.Read(ref _lateWakeups);
    public double MaxLateMs => MediaClock.ToMs(Interlocked.Read(ref _maxLateTicks));
    public long Retransmitted => Interlocked.Read(ref _retransmitted);
    public long RetransmitRequests => Interlocked.Read(ref _retransmitRequests);
    public Action<long>? OnsetSent { get; set; }
    public event Action<Exception>? Failed;

    public PtpRealtimeSender(IPAddress local, IPAddress peer, int dataPort, int controlPort, UdpClient control,
        byte[] key, uint ssrc, int latency, bool alac, AudioFifo fifo, IAudioEffect? effects, PtpMaster clock,
        bool modernStream = false)
        : this(local, peer, dataPort, controlPort, control, key, ssrc, latency, alac, fifo, effects,
            new PtpTimeline(clock.TimeAt, () => clock.ClockId, () => clock.IsMaster), modernStream) { }

    /// <summary>The same stream on a timeline that is not this PC's own clock (Apple TV follows the receiver's).</summary>
    public PtpRealtimeSender(IPAddress local, IPAddress peer, int dataPort, int controlPort, UdpClient control,
        byte[] key, uint ssrc, int latency, bool alac, AudioFifo fifo, IAudioEffect? effects, PtpTimeline clock,
        bool modernStream = false)
    {
        _audio = new(new IPEndPoint(local, 0));
        try { _audio.Connect(peer, dataPort); _cipher = new(key); }
        catch { _audio.Dispose(); throw; }
        _control = control; _controlPeer = new(peer, controlPort); _ssrc = ssrc; _latency = latency;
        _alac = alac; _modernStream = modernStream; _fifo = fifo; _effects = effects; _clock = clock;
    }

    public void Start(long? startAt = null, ReceiverPcmReader? readAudio = null)
    {
        // Paced on a thread of its own, as RtpSender and BufferedSender are, not on the thread pool: a pool held up by
        // blocking work (synchronous RTSP in Task.Run, here or in other code) must not make it late. 2026-10-17: with the
        // pool sized for 4 cores, as on a CI runner, and blocking work queued on it, the pooled loop woke more than 60 ms
        // after its first deadline in 5 of 20 starts, although that deadline was 247 ms away when Start returned.
        lock (_lifecycle)
        {
            ThrowIfStopped();
            if (_started != 0) throw new InvalidOperationException("Realtime sender already started");
            _started = 1; _startAt = startAt; _readAudio = readAudio;
            _sender = new Thread(Send) { IsBackground = true, Name = "AirPlay PTP realtime sender", Priority = ThreadPriority.Highest };
        }
        try
        {
            BeforeSenderStartForTest?.Invoke();
            lock (_lifecycle)
            {
                // Stop may finish while this thread is constructed but not yet started. Only a started thread may
                // be joined, and publishing both workers here makes either all their work or none precede Stop.
                ThrowIfStopped();
                _sender.Start(); _senderStarted = true;
                _resendsExited = false;
                try { _resends = Task.Run(Resends); }
                catch { _resendsExited = true; throw; }
            }
        }
        catch { StopSending(); throw; }
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_stopping != 0) throw new InvalidOperationException("Realtime sender already stopped");
    }

    internal static byte[] Sync(uint rtp, ulong nanoseconds, ulong clock, int latency, bool first, bool modern)
    {
        var packet = new byte[28]; packet[0] = modern && first ? (byte)0x90 : (byte)0x80; packet[1] = 0xd7;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), modern ? (ushort)6 : (ushort)7);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), modern ? unchecked(rtp - (uint)latency) : unchecked(rtp - (uint)latency + 11035));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), modern ? FixedPointTime(nanoseconds) : nanoseconds);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtp);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clock);
        return packet;
    }

    // Keep the legacy test and harness surface stable while the modern Apple TV stream adds its two flags.
    internal static byte[] Sync(uint rtp, ulong nanoseconds, ulong clock, int latency) =>
        Sync(rtp, nanoseconds, clock, latency, first: false, modern: false);

    private static ulong FixedPointTime(ulong nanoseconds)
    {
        ulong seconds = nanoseconds / 1_000_000_000;
        ulong fraction = (ulong)((UInt128)(nanoseconds % 1_000_000_000) * ((UInt128)1 << 32) / 1_000_000_000);
        return (seconds << 32) | fraction;
    }

    // 2026-10-17: runtime events locate synchronous OSR on this thread around packet 14 (38 ms on a fast CI run;
    // other cold runs fail at packet 15, 68-70 ms late). Compile before entering, while startup can still join late,
    // rather than spending the active stream's unchanged 60 ms budget optimizing the loop on its own thread.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Send()
    {
        try
        {
            using var mmcss = Native.EnterMmcss("Pro Audio");
            // This loop waited with WaitHandle.WaitOne(ms), which ends on the system timer's tick. 2026-11-27, the
            // maintainer's PC, a plain process: WaitOne(7) took 15.5 ms (median of 300; 13.9 to 30.3), two packets' time.
            // So packets went out in pairs, each up to a tick late, and the FIFO was read two packets at once, which a
            // 16 ms target cannot feed: an Apple TV session began with "fifo dropout … target 20 ms", "… target 24 ms".
            using var timer = new Native.PreciseTimer();
            var audio = new float[704]; var pcm = new short[704];
            long stopCheck = MediaClock.FromMs(20); // a wait for a far start is cut up, so that stopping never waits for it
            long start = StartedAt = _startAt ?? MediaClock.Now, n = 0, nextSync = start;
            int quiet = 0;
            bool sent = false, firstSync = true;
            while (!_stop.IsCancellationRequested)
            {
                long deadline = start + (long)(n * 352.0 * MediaClock.Frequency / 44100);
                for (long left; (left = deadline - MediaClock.Now) > 0;)
                {
                    timer.Sleep(Math.Min(left, stopCheck));
                    if (_stop.IsCancellationRequested) return;
                }
                if (!_clock.Usable()) throw new IOException("PTP master changed; reconnect is required");
                long late = MediaClock.Now - deadline;
                if (late > LateLimit)
                {
                    // Too long for a stall of this PC (a suspend, a debugger): the receiver has given the session up by now.
                    if (sent && late > GiveUpLimit)
                        throw new IOException($"PTP realtime sender missed its deadline: packet {n} {MediaClock.ToMs(late):F0} ms late");
                    // Join the timeline where it is now, as RtpSender does after a stall: a later packet with the RTP time
                    // the epoch gives it, and still none sent more than 60 ms late. The receiver hears the packets jumped
                    // over as a hole of that length, and nothing else changes.
                    // Before the first packet the receiver loses nothing at all: this leg's Start came late (the group's
                    // RTSP round trips, a busy machine) or it woke late for packet 0. 2026-10-17: with forced GCs running
                    // on this PC, the test's Start returned 425-648 ms after the epoch it had chosen, and failing cost the
                    // session.
                    // After it, a stall used to end the session. 2026-11-26, the maintainer's log (a stream to an Apple TV):
                    // "packet 189431 236 ms late" was followed by a reconnect, about 4 s without sound for a hole of a
                    // quarter of a second.
                    long skip = (long)(late * 44100.0 / 352 / MediaClock.Frequency);
                    if (sent)
                    {
                        Interlocked.Add(ref _skipped, skip);
                        if (_readAudio == null) _fifo.DiscardToTarget(); // what piled up meanwhile would play late
                        Log.Warn($"PTP realtime sender stalled {MediaClock.ToMs(late):F0} ms at packet {n}, skipped {skip} packets");
                    }
                    else Log.Warn($"PTP realtime sender {MediaClock.ToMs(late):F0} ms late for packet {n} before sending any, joins at packet {n + skip}");
                    n += skip;
                    continue;
                }
                if (sent && late > MediaClock.FromMs(2))
                {
                    Interlocked.Increment(ref _lateWakeups);
                    if (late > Interlocked.Read(ref _maxLateTicks)) Interlocked.Exchange(ref _maxLateTicks, late);
                }
                if (MediaClock.Now >= nextSync)
                {
                    long syncTicks = MediaClock.Now;
                    uint now = unchecked(RtpBase + (uint)Math.Round((syncTicks - start) * 44100.0 / MediaClock.Frequency));
                    byte[] sync = Sync(now, _clock.TimeAt(syncTicks), _clock.ClockId(), _latency, firstSync, _modernStream);
                    _control.Send(sync, sync.Length, _controlPeer);
                    firstSync = false;
                    nextSync = MediaClock.Now + MediaClock.FromMs(125);
                }
                if (_readAudio is { } read) read(deadline, audio);
                else { _fifo.Read(audio); _effects?.Process(audio, 352); }
                float peak = 0;
                for (int i = 0; i < pcm.Length; i++)
                {
                    float value = float.IsFinite(audio[i]) ? audio[i] : 0;
                    peak = Math.Max(peak, Math.Abs(value));
                    pcm[i] = (short)Math.Clamp((int)Math.Round(value * 32767), short.MinValue, short.MaxValue);
                }
                if (peak < .001f) quiet += 352;
                else { if (quiet >= 44100 * 3 / 10) OnsetSent?.Invoke(deadline); quiet = 0; }
                byte[] payload = _alac ? RaopAudio.Alac(pcm) : RaopAudio.L16(pcm);
                ushort sequence = unchecked((ushort)(FirstSeq + n));
                var packet = RaopPackets.Audio(sequence, unchecked(RtpBase + (uint)(n * 352)), _ssrc,
                    new byte[payload.Length + 24], !sent && !_modernStream);
                ulong nonce = _cipher.Counter;
                _cipher.Encrypt(payload, packet.AsSpan(4, 8), packet.AsSpan(12, payload.Length + 16));
                BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(packet.Length - 8), nonce);
                lock (_history)
                {
                    _history[sequence] = packet; _order.Enqueue(sequence);
                    if (_order.Count > 512) _history.Remove(_order.Dequeue());
                }
                _audio.Send(packet, packet.Length);
                sent = true; n++; Interlocked.Increment(ref _packets);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { ReportFailure(ex); }
        finally
        {
            lock (_lifecycle) _senderExited = true;
            TryDisposeResources();
        }
    }

    private async Task Resends()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var request = await _control.ReceiveAsync(_stop.Token);
                if (!request.RemoteEndPoint.Address.Equals(_controlPeer.Address) || request.Buffer.Length < 8
                    || (request.Buffer[1] & 0x7f) != 0x55) continue;
                ushort begin = BinaryPrimitives.ReadUInt16BigEndian(request.Buffer.AsSpan(4));
                int count = Math.Min(64, (int)BinaryPrimitives.ReadUInt16BigEndian(request.Buffer.AsSpan(6)));
                Interlocked.Add(ref _retransmitRequests, count);
                for (int i = 0; i < count; i++)
                {
                    byte[]? packet; lock (_history) _history.TryGetValue(unchecked((ushort)(begin + i)), out packet);
                    if (packet == null) continue;
                    byte[] reply = [0x80, 0xd6, packet[2], packet[3], .. packet];
                    await _control.SendAsync(reply, request.RemoteEndPoint, _stop.Token);
                    Interlocked.Increment(ref _retransmitted);
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { ReportFailure(ex); }
        finally
        {
            lock (_lifecycle) _resendsExited = true;
            TryDisposeResources();
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed != 0) return;
            _disposed = 1;
        }
        StopSending();
        Thread? sender;
        Task resends;
        bool cancelFinished;
        lock (_lifecycle) { sender = _senderStarted ? _sender : null; resends = _resends; cancelFinished = _cancelFinished; }
        // A reader or failure callback may dispose its sender. It cannot wait for its own exit, and the exit path
        // below retains the cipher/token until that callback and all other users have actually returned. Nor may a
        // cancellation callback wait for the Cancel which invoked it: that Stop owns the deferred cleanup instead.
        if (cancelFinished && sender != Thread.CurrentThread && _failureCallbackSender != this)
        {
            sender?.Join(1000);
            try { resends.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        }
        TryDisposeResources();
    }

    internal void StopSending()
    {
        lock (_lifecycle)
        {
            if (_stopping != 0) return;
            _stopping = 1;
        }
        try
        {
            try { BeforeStopCancellationForTest?.Invoke(); }
            finally { _stop.Cancel(); }
        }
        finally
        {
            _audio.Dispose();
            lock (_lifecycle) _cancelFinished = true;
            TryDisposeResources();
        }
    }

    private void TryDisposeResources()
    {
        lock (_lifecycle)
        {
            // In particular, another Dispose must not free the token while Stop is outside the gate in Cancel.
            if (_disposed == 0 || !_cancelFinished || _senderStarted && !_senderExited || !_resendsExited || _resourcesDisposed) return;
            _resourcesDisposed = true;
        }
        _cipher.Dispose(); _stop.Dispose();
    }

    private void ReportFailure(Exception error)
    {
        var previous = _failureCallbackSender;
        _failureCallbackSender = this;
        try { Failed?.Invoke(error); }
        finally { _failureCallbackSender = previous; }
    }
}
