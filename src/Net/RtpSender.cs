using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>Which channels one speaker is sent: full stereo, or one channel copied to both sides.</summary>
public enum ChannelMode { Stereo, LeftOnly, RightOnly }

/// <summary>
/// Where one session's RTP goes, as negotiated by its stream SETUP: the session's own control socket,
/// the speaker's ports, the stream key and SSRC, and (in a group) which channels this speaker gets.
/// </summary>
public sealed record RtpTarget(UdpClient Control, IPAddress Remote, int DataPort, int ControlPort, byte[] StreamKey,
    uint Ssrc, ChannelMode Channels = ChannelMode.Stereo)
{
    /// <summary>Fixed first sequence number (tests only); random otherwise.</summary>
    internal ushort? FirstSeq { get; init; }
}

/// <summary>
/// Realtime (type 96) AirPlay 2 audio sender.
/// One timeline: packet n carries RTP time RtpBase + n*352 and is sent at T0 + n*352/rate; sync packets
/// map "now" onto the same line; the speaker adds the SETUP latency, so every packet plays
/// LatencyFrames after it was sent.
/// With several targets (stereo pair / multi-room, experimental) all speakers share that timeline: the
/// same RTP time, cadence and sync mapping. Each session keeps its own key, SSRC, sequence numbers and
/// resend buffer; every packet is encrypted per session and sent to the speakers back-to-back.
/// </summary>
public sealed class RtpSender : IDisposable
{
    public const int SampleRate = 44100;
    public const int FramesPerPacket = 352;
    internal const int PayloadBytes = FramesPerPacket * 4;
    internal const int PacketBytes = 12 + PayloadBytes + CounterCipher.TagSize + 8;
    private static readonly long SyncInterval = MediaClock.FromMs(1000);
    private static readonly long LateLimit = MediaClock.FromMs(60);

    private readonly RtpStream[] _streams;
    private readonly byte[]?[] _payloads = new byte[]?[3]; // one per ChannelMode in use, indexed by mode
    private readonly AudioFifo _fifo;
    private Thread? _sendThread;
    private volatile bool _stop;

    public int LatencyFrames { get; }

    /// <summary>Legacy (pyatv-style) sync packets that also subtract the latency. Off by default.</summary>
    public bool LatencyInSync { get; set; }

    /// <summary>In-place processing of each packet's PCM before encoding (EffectChain: EQ, night mode).</summary>
    public IAudioEffect? Effects { get; init; }

    public ushort FirstSeq => _streams[0].FirstSeq;
    public uint RtpBase { get; }
    public long T0 { get; private set; }

    /// <summary>One entry per speaker, in the order the targets were given.</summary>
    public IReadOnlyList<RtpStream> Streams => _streams;

    // stats (read from other threads; approximate is fine)
    public long PacketsSent;      // timeline packets (each one went to every speaker)
    public long SilentPackets;
    public long LateWakeups;      // woke > 2 ms after deadline
    public double MaxLateMs;
    public long SkippedPackets;
    public long RetransmitRequests => Sum(static s => s.RetransmitRequests);
    public long Retransmitted => Sum(static s => s.Retransmitted);
    public long RetransmitMisses => Sum(static s => s.RetransmitMisses);

    /// <summary>
    /// When set, report the QPC time at which a sound onset (after ≥300 ms of near-silence) leaves the
    /// PC. Used by the sync test to measure our own share of the latency.
    /// </summary>
    public Action<long>? OnsetSent;
    private int _quietFrames;

    /// <summary>One speaker (the normal case).</summary>
    public RtpSender(UdpClient control, IPAddress remote, int dataPort, int controlPort, byte[] streamKey,
        uint ssrc, int latencyFrames, AudioFifo fifo)
        : this([new RtpTarget(control, remote, dataPort, controlPort, streamKey, ssrc)], latencyFrames, fifo, null)
    {
    }

    /// <summary>Several speakers on one timeline (stereo pair / multi-room).</summary>
    public RtpSender(IReadOnlyList<RtpTarget> targets, int latencyFrames, AudioFifo fifo)
        : this(targets, latencyFrames, fifo, null)
    {
    }

    internal RtpSender(IReadOnlyList<RtpTarget> targets, int latencyFrames, AudioFifo fifo, uint? rtpBase)
    {
        if (targets.Count == 0) throw new ArgumentException("no targets", nameof(targets));
        _streams = new RtpStream[targets.Count];
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                _streams[i] = new RtpStream(targets[i]);
                _payloads[(int)targets[i].Channels] ??= new byte[PayloadBytes];
            }
        }
        catch
        {
            foreach (var s in _streams) s?.Dispose();
            throw;
        }
        RtpBase = rtpBase ?? (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        LatencyFrames = latencyFrames;
        _fifo = fifo;
    }

    /// <summary>Start syncing now; audio packet 0 goes out at t0 (a QPC time slightly in the future).</summary>
    public void Start(long t0)
    {
        T0 = t0;
        foreach (var s in _streams) s.StartControl();
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "AirPlay sender", Priority = ThreadPriority.Highest };
        _sendThread.Start();
    }

    private long Deadline(long n) => T0 + (long)(n * (double)FramesPerPacket * MediaClock.Frequency / SampleRate);

    // Runs under MMCSS; nothing in the per-packet path allocates.
    private void SendLoop()
    {
        using var mmcss = Native.EnterMmcss("Pro Audio");
        using var timer = new Native.PreciseTimer();
        var pcm = new float[FramesPerPacket * 2];
        long n = 0;
        long nextSync = MediaClock.Now;
        bool firstSync = true, firstPacket = true;

        while (!_stop)
        {
            long now = MediaClock.Now;
            if (now >= nextSync)
            {
                SendSync(now, firstSync);
                firstSync = false;
                nextSync = now + SyncInterval;
            }

            long deadline = Deadline(n);
            if (now < deadline)
            {
                timer.Sleep(Math.Min(deadline, nextSync) - now);
                continue;
            }

            long late = now - deadline;
            if (late > LateLimit)
            {
                // Too far behind to be useful: jump the timeline forward instead of bursting.
                long skip = (long)(late * (double)SampleRate / FramesPerPacket / MediaClock.Frequency);
                n += skip;
                SkippedPackets += skip;
                _fifo.DiscardToTarget();
                Log.Warn($"sender stalled {MediaClock.ToMs(late):F0} ms, skipped {skip} packets");
                continue;
            }
            if (!firstPacket && late > MediaClock.FromMs(2))
            {
                LateWakeups++;
                MaxLateMs = Math.Max(MaxLateMs, MediaClock.ToMs(late));
            }

            if (!_fifo.Read(pcm)) SilentPackets++;
            Effects?.Process(pcm, FramesPerPacket); // ---- effects hook: the one place audio is processed before sending
            if (OnsetSent is { } onset) DetectOnset(pcm, deadline, onset);
            EncodePayloads(pcm);
            uint rtp = unchecked(RtpBase + (uint)(n * FramesPerPacket));
            for (int i = 0; i < _streams.Length; i++)
            {
                var s = _streams[i];
                s.SendAudio((ushort)(s.FirstSeq + n), rtp, _payloads[(int)s.Channels]!, firstPacket);
            }
            PacketsSent++;
            firstPacket = false;
            n++;
        }
    }

    private void DetectOnset(ReadOnlySpan<float> pcm, long deadline, Action<long> report)
    {
        for (int i = 0; i < FramesPerPacket; i++)
        {
            if (Math.Abs(pcm[i * 2]) > 0.02f)
            {
                if (_quietFrames > SampleRate * 3 / 10)
                    report(deadline + (long)(i * (double)MediaClock.Frequency / SampleRate));
                _quietFrames = 0;
            }
            else _quietFrames++;
        }
    }

    // TODO(ALAC): PCM (ct=1) only. If real stereo pairs turn out to accept only ALAC (ct=2, audioFormat
    // 0x40000 in the stream SETUP), encode here per mode; the payload (and PacketBytes) then become variable.
    private void EncodePayloads(ReadOnlySpan<float> pcm)
    {
        if (_payloads[(int)ChannelMode.Stereo] is { } stereo) ToS16BigEndian(pcm, stereo);
        if (_payloads[(int)ChannelMode.LeftOnly] is { } left) ToS16BigEndianOneChannel(pcm, left, 0);
        if (_payloads[(int)ChannelMode.RightOnly] is { } right) ToS16BigEndianOneChannel(pcm, right, 1);
    }

    internal static void ToS16BigEndian(ReadOnlySpan<float> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float v = src[i] * 32767f;
            short s = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)MathF.Round(v);
            BinaryPrimitives.WriteInt16BigEndian(dst[(i * 2)..], s);
        }
    }

    /// <summary>One channel of interleaved stereo (0 = left, 1 = right) written to both sides: (L,L) or (R,R).</summary>
    internal static void ToS16BigEndianOneChannel(ReadOnlySpan<float> src, Span<byte> dst, int channel)
    {
        for (int i = 0; i < src.Length / 2; i++)
        {
            float v = src[i * 2 + channel] * 32767f;
            short s = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)MathF.Round(v);
            BinaryPrimitives.WriteInt16BigEndian(dst[(i * 4)..], s);
            BinaryPrimitives.WriteInt16BigEndian(dst[(i * 4 + 2)..], s);
        }
    }

    private void SendSync(long now, bool first)
    {
        long elapsedFrames = (long)Math.Round((now - T0) * (double)SampleRate / MediaClock.Frequency);
        uint rtpNow = unchecked(RtpBase + (uint)elapsedFrames);
        ulong ntp = MediaClock.NtpAt(now);
        int latency = LatencyInSync ? LatencyFrames : 0;
        for (int i = 0; i < _streams.Length; i++) _streams[i].SendSync(rtpNow, ntp, first, latency);
    }

    /// <summary>
    /// 0xD4 sync packet: RTP time "now" ↔ NTP time. Measured on HomePod OS 27: the speaker adds the SETUP
    /// latency on top of whatever this packet says, so subtracting the latency here as well (as pyatv
    /// does) doubles the real delay — pass 0 unless reproducing that legacy behaviour.
    /// </summary>
    internal static byte[] BuildSyncPacket(uint rtpNow, ulong ntp, bool first, int latencyFrames)
    {
        var p = new byte[20];
        WriteSyncPacket(p, rtpNow, ntp, first, latencyFrames);
        return p;
    }

    internal static void WriteSyncPacket(Span<byte> p, uint rtpNow, ulong ntp, bool first, int latencyFrames)
    {
        p[0] = first ? (byte)0x90 : (byte)0x80;
        p[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], 7);
        BinaryPrimitives.WriteUInt32BigEndian(p[4..], unchecked(rtpNow - (uint)latencyFrames));
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], ntp);
        BinaryPrimitives.WriteUInt32BigEndian(p[16..], rtpNow);
    }

    private long Sum(Func<RtpStream, long> stat)
    {
        long total = 0;
        foreach (var s in _streams) total += stat(s);
        return total;
    }

    public void Dispose()
    {
        _stop = true;
        _sendThread?.Join(500);
        foreach (var s in _streams) s.Dispose();
    }
}

/// <summary>
/// One speaker's leg of an RtpSender: its own data socket, ChaCha20-Poly1305 key, SSRC, sequence numbers
/// and resend buffer, plus the thread that answers its resend requests on the session's control socket.
/// </summary>
public sealed class RtpStream : IDisposable
{
    private const int BacklogSize = 1024;
    private const int PayloadBytes = RtpSender.PayloadBytes;
    private const int PacketBytes = RtpSender.PacketBytes;

    private readonly UdpClient _data;
    private readonly UdpClient _control;
    private readonly IPEndPoint _controlRemote;
    private readonly CounterCipher _cipher;
    private readonly uint _ssrc;
    private readonly byte[][] _backlog = new byte[BacklogSize][];
    private readonly int[] _backlogSeq = new int[BacklogSize];
    private readonly object _backlogLock = new();
    private readonly byte[] _sync = new byte[20];
    private Thread? _controlThread;
    private volatile bool _stop;

    public ChannelMode Channels { get; }
    public ushort FirstSeq { get; }
    public uint Ssrc => _ssrc;
    public IPAddress Remote => _controlRemote.Address;

    // stats (read from other threads; approximate is fine)
    public long PacketsSent;
    public long RetransmitRequests;
    public long Retransmitted;
    public long RetransmitMisses;

    internal RtpStream(RtpTarget target)
    {
        _control = target.Control;
        _controlRemote = new IPEndPoint(target.Remote, target.ControlPort);
        _data = new UdpClient(new IPEndPoint(((IPEndPoint)target.Control.Client.LocalEndPoint!).Address, 0));
        try
        {
            _data.Connect(target.Remote, target.DataPort);
            _data.Client.SendBufferSize = 256 * 1024;
            _cipher = new CounterCipher(target.StreamKey);
        }
        catch
        {
            _data.Dispose();
            throw;
        }
        _ssrc = target.Ssrc;
        Channels = target.Channels;
        FirstSeq = target.FirstSeq ?? (ushort)RandomNumberGenerator.GetInt32(65536);
        for (int i = 0; i < BacklogSize; i++) { _backlog[i] = new byte[PacketBytes]; _backlogSeq[i] = -1; }
    }

    internal void StartControl()
    {
        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "AirPlay control" };
        _controlThread.Start();
    }

    internal void SendAudio(ushort seq, uint rtp, byte[] payload, bool first)
    {
        int slot = seq % BacklogSize;
        lock (_backlogLock)
        {
            var p = _backlog[slot];
            p[0] = 0x80;
            p[1] = first ? (byte)0xE0 : (byte)0x60;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), seq);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), rtp);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), _ssrc);
            ulong counter = _cipher.Counter;
            _cipher.Encrypt(payload, p.AsSpan(4, 8), p.AsSpan(12, PayloadBytes + CounterCipher.TagSize));
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(12 + PayloadBytes + CounterCipher.TagSize), counter);
            _backlogSeq[slot] = seq;
            try { _data.Send(p, PacketBytes); } catch (SocketException) { }
        }
        PacketsSent++;
    }

    internal void SendSync(uint rtpNow, ulong ntp, bool first, int latencyFrames)
    {
        RtpSender.WriteSyncPacket(_sync, rtpNow, ntp, first, latencyFrames);
        try { _control.Send(_sync, _sync.Length, _controlRemote); } catch (SocketException) { }
    }

    private void ControlLoop()
    {
        var resend = new byte[4 + PacketBytes];
        while (!_stop)
        {
            try
            {
                IPEndPoint? from = null;
                var msg = _control.Receive(ref from);
                if (msg.Length < 8 || (msg[1] & 0x7F) != 0x55) continue;
                ushort lost = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
                RetransmitRequests++;
                for (int i = 0; i < Math.Min((int)count, 64); i++)
                {
                    ushort seq = (ushort)(lost + i);
                    int slot = seq % BacklogSize;
                    lock (_backlogLock)
                    {
                        if (_backlogSeq[slot] != seq) { RetransmitMisses++; continue; }
                        resend[0] = 0x80;
                        resend[1] = 0xD6;
                        _backlog[slot].AsSpan(2, 2).CopyTo(resend.AsSpan(2));
                        _backlog[slot].CopyTo(resend, 4);
                    }
                    _control.Send(resend, resend.Length, from);
                    Retransmitted++;
                }
            }
            catch (SocketException) when (!_stop) { }
            catch (Exception) { return; }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _data.Dispose();
        _cipher.Dispose();
    }
}
