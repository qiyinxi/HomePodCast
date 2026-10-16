using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>Third-party type 96 over PTP; independent of the HomePod RtpSender and NTP timeline.</summary>
internal sealed class PtpRealtimeSender : IDisposable
{
    private readonly UdpClient _audio, _control;
    private readonly IPEndPoint _controlPeer;
    private readonly CounterCipher _cipher;
    private readonly AudioFifo _fifo;
    private readonly IAudioEffect? _effects;
    private readonly PtpMaster _clock;
    private readonly uint _ssrc;
    private readonly int _latency;
    private readonly bool _alac;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ushort, byte[]> _history = new();
    private readonly Queue<ushort> _order = new();
    private Task[] _tasks = [];
    private long _packets;
    private int _disposed;
    private long? _startAt;
    private ReceiverPcmReader? _readAudio;
    private int _started;
    private int _stopping;
    public uint RtpBase { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public ushort FirstSeq { get; } = (ushort)RandomNumberGenerator.GetInt32(65536);
    public long StartedAt { get; private set; }
    public long PacketsSent => Interlocked.Read(ref _packets);
    public Action<long>? OnsetSent { get; set; }
    public event Action<Exception>? Failed;

    public PtpRealtimeSender(IPAddress local, IPAddress peer, int dataPort, int controlPort, UdpClient control,
        byte[] key, uint ssrc, int latency, bool alac, AudioFifo fifo, IAudioEffect? effects, PtpMaster clock)
    {
        _audio = new(new IPEndPoint(local, 0));
        try { _audio.Connect(peer, dataPort); _cipher = new(key); }
        catch { _audio.Dispose(); throw; }
        _control = control; _controlPeer = new(peer, controlPort); _ssrc = ssrc; _latency = latency;
        _alac = alac; _fifo = fifo; _effects = effects; _clock = clock;
    }

    public void Start(long? startAt = null, ReceiverPcmReader? readAudio = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Realtime sender already started");
        _startAt = startAt; _readAudio = readAudio;
        _tasks = [Task.Run(Send), Task.Run(Resends)];
    }

    internal static byte[] Sync(uint rtp, ulong nanoseconds, ulong clock, int latency)
    {
        var packet = new byte[28]; packet[0] = 0x80; packet[1] = 0xd7;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 7);
        // 2026-10-14: shairport-sync 4.3.7 subtracts the fixed 11035-frame pipeline from frame_1.
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), unchecked(rtp - (uint)latency + 11035));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), nanoseconds);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtp);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clock);
        return packet;
    }

    private async Task Send()
    {
        try
        {
            var audio = new float[704]; var pcm = new short[704];
            long start = StartedAt = _startAt ?? MediaClock.Now, n = 0, nextSync = start;
            int quiet = 0;
            while (!_stop.IsCancellationRequested)
            {
                long deadline = start + (long)(n * 352.0 * MediaClock.Frequency / 44100);
                if (_startAt != null)
                {
                    while (MediaClock.Now < deadline)
                        await Task.Delay(Math.Max(1, (int)MediaClock.ToMs(deadline - MediaClock.Now)), _stop.Token);
                }
                else
                {
                    int wait = (int)MediaClock.ToMs(deadline - MediaClock.Now);
                    if (wait > 0) await Task.Delay(wait, _stop.Token);
                }
                if (!_clock.IsMaster) throw new IOException("PTP master changed; reconnect is required");
                if (MediaClock.ToMs(MediaClock.Now - deadline) > 60) throw new IOException("PTP realtime sender missed its deadline");
                if (MediaClock.Now >= nextSync)
                {
                    long syncTicks = MediaClock.Now;
                    uint now = unchecked(RtpBase + (uint)Math.Round((syncTicks - start) * 44100.0 / MediaClock.Frequency));
                    await _control.SendAsync(Sync(now, _clock.TimeAt(syncTicks), _clock.ClockId, _latency), _controlPeer, _stop.Token);
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
                    new byte[payload.Length + 24], n == 0);
                ulong nonce = _cipher.Counter;
                _cipher.Encrypt(payload, packet.AsSpan(4, 8), packet.AsSpan(12, payload.Length + 16));
                BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(packet.Length - 8), nonce);
                lock (_history)
                {
                    _history[sequence] = packet; _order.Enqueue(sequence);
                    if (_order.Count > 512) _history.Remove(_order.Dequeue());
                }
                await _audio.SendAsync(packet, _stop.Token);
                n++; Interlocked.Increment(ref _packets);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); }
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
                for (int i = 0; i < count; i++)
                {
                    byte[]? packet; lock (_history) _history.TryGetValue(unchecked((ushort)(begin + i)), out packet);
                    if (packet == null) continue;
                    byte[] reply = [0x80, 0xd6, packet[2], packet[3], .. packet];
                    await _control.SendAsync(reply, request.RemoteEndPoint, _stop.Token);
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopSending();
        try { Task.WaitAll(_tasks, TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cipher.Dispose(); _stop.Dispose();
    }

    internal void StopSending()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _stop.Cancel(); _audio.Dispose();
    }
}
