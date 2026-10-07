using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// Realtime (type 96) AirPlay 2 audio sender.
/// One timeline: packet n carries RTP time RtpBase + n*352 and is sent at T0 + n*352/rate; sync packets
/// map "now" onto the same line, so every packet plays exactly LatencyFrames after it was sent.
/// </summary>
public sealed class RtpSender : IDisposable
{
    public const int SampleRate = 44100;
    public const int FramesPerPacket = 352;
    private const int PayloadBytes = FramesPerPacket * 4;
    private const int PacketBytes = 12 + PayloadBytes + CounterCipher.TagSize + 8;
    private const int BacklogSize = 1024;
    private static readonly long SyncInterval = MediaClock.FromMs(1000);
    private static readonly long LateLimit = MediaClock.FromMs(60);

    private readonly UdpClient _data;
    private readonly UdpClient _control;
    private readonly IPEndPoint _controlRemote;
    private readonly CounterCipher _cipher;
    private readonly AudioFifo _fifo;
    private readonly uint _ssrc;
    private readonly byte[][] _backlog = new byte[BacklogSize][];
    private readonly int[] _backlogSeq = new int[BacklogSize];
    private readonly object _backlogLock = new();
    private Thread? _sendThread, _controlThread;
    private volatile bool _stop;

    public int LatencyFrames { get; }
    public ushort FirstSeq { get; } = (ushort)RandomNumberGenerator.GetInt32(65536);
    public uint RtpBase { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public long T0 { get; private set; }

    // stats (read from other threads; approximate is fine)
    public long PacketsSent;
    public long SilentPackets;
    public long LateWakeups;      // woke > 2 ms after deadline
    public double MaxLateMs;
    public long SkippedPackets;
    public long RetransmitRequests;
    public long Retransmitted;
    public long RetransmitMisses;

    /// <summary>
    /// When set, report the QPC time at which a sound onset (after ≥300 ms of near-silence) leaves the
    /// PC. Used by the sync test to measure our own share of the latency.
    /// </summary>
    public Action<long>? OnsetSent;
    private int _quietFrames;

    public RtpSender(UdpClient control, IPAddress remote, int dataPort, int controlPort, byte[] streamKey,
        uint ssrc, int latencyFrames, AudioFifo fifo)
    {
        _control = control;
        _controlRemote = new IPEndPoint(remote, controlPort);
        _data = new UdpClient(new IPEndPoint(((IPEndPoint)control.Client.LocalEndPoint!).Address, 0));
        _data.Connect(remote, dataPort);
        _data.Client.SendBufferSize = 256 * 1024;
        _cipher = new CounterCipher(streamKey);
        _ssrc = ssrc;
        LatencyFrames = latencyFrames;
        _fifo = fifo;
        for (int i = 0; i < BacklogSize; i++) { _backlog[i] = new byte[PacketBytes]; _backlogSeq[i] = -1; }
    }

    /// <summary>Start syncing now; audio packet 0 goes out at t0 (a QPC time slightly in the future).</summary>
    public void Start(long t0)
    {
        T0 = t0;
        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "AirPlay control" };
        _controlThread.Start();
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "AirPlay sender", Priority = ThreadPriority.Highest };
        _sendThread.Start();
    }

    private long Deadline(long n) => T0 + (long)(n * (double)FramesPerPacket * MediaClock.Frequency / SampleRate);

    private void SendLoop()
    {
        using var mmcss = Native.EnterMmcss("Pro Audio");
        using var timer = new Native.PreciseTimer();
        var pcm = new float[FramesPerPacket * 2];
        var payload = new byte[PayloadBytes];
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
            if (OnsetSent is { } onset) DetectOnset(pcm, deadline, onset);
            ToS16BigEndian(pcm, payload);
            SendAudio(n, payload, firstPacket);
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

    private static void ToS16BigEndian(ReadOnlySpan<float> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float v = src[i] * 32767f;
            short s = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)MathF.Round(v);
            BinaryPrimitives.WriteInt16BigEndian(dst[(i * 2)..], s);
        }
    }

    private void SendAudio(long n, byte[] payload, bool first)
    {
        ushort seq = (ushort)(FirstSeq + n);
        uint rtp = unchecked(RtpBase + (uint)(n * FramesPerPacket));
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

    private void SendSync(long now, bool first)
    {
        long elapsedFrames = (long)Math.Round((now - T0) * (double)SampleRate / MediaClock.Frequency);
        uint rtpNow = unchecked(RtpBase + (uint)elapsedFrames);
        var p = new byte[20];
        p[0] = first ? (byte)0x90 : (byte)0x80;
        p[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), unchecked(rtpNow - (uint)LatencyFrames));
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(8), MediaClock.NtpAt(now));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), rtpNow);
        try { _control.Send(p, p.Length, _controlRemote); } catch (SocketException) { }
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
        _sendThread?.Join(500);
        _data.Dispose();
        _cipher.Dispose();
    }
}
