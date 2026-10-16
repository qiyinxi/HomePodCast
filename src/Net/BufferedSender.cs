using HomePodCast.Audio;
using HomePodCast.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HomePodCast.Net;

/// <summary>Independent, paced type 103 sender. Only its worker thread owns the Windows AAC transform.</summary>
internal sealed class BufferedSender : IDisposable
{
    private static readonly long LateLimit = MediaClock.FromMs(250);
    private readonly TcpClient _tcp;
    private readonly AudioFifo _fifo;
    private readonly IAudioEffect? _effects;
    private readonly PtpMaster _clock;
    private readonly byte[] _key;
    private readonly uint _ssrc;
    private readonly Func<IAacEncoder> _encoder;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _start = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private long _packets;
    private int _disposed;
    private long? _startAt;
    private ReceiverPcmReader? _readAudio;
    private int _started;
    private int _stopping;
    public uint RtpBase { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public long StartedAt { get; private set; }
    public long PacketsSent => Interlocked.Read(ref _packets);
    public Action<long>? OnsetSent { get; set; }
    public event Action<Exception>? Failed;

    private BufferedSender(TcpClient tcp, byte[] key, uint ssrc, AudioFifo fifo, IAudioEffect? effects,
        PtpMaster clock, Func<IAacEncoder>? encoder)
    {
        _tcp = tcp; _key = key; _ssrc = ssrc; _fifo = fifo; _effects = effects; _clock = clock;
        _encoder = encoder ?? (() => OperatingSystem.IsWindows() ? new AacEncoder() : throw new PlatformNotSupportedException("Live AAC encoding requires Windows"));
        _worker = new Thread(Run) { IsBackground = true, Name = "AirPlay buffered AAC" };
        _worker.Start();
    }

    public static async Task<BufferedSender> ConnectAsync(IPAddress host, int port, byte[] key, uint ssrc,
        AudioFifo fifo, IAudioEffect? effects, PtpMaster clock, Func<IAacEncoder>? encoder, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true, SendTimeout = 2000 };
        BufferedSender? sender = null;
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            sender = new(tcp, key, ssrc, fifo, effects, clock, encoder);
            await sender._ready.Task.WaitAsync(ct);
            return sender;
        }
        catch { if (sender != null) sender.Dispose(); else tcp.Dispose(); throw; }
    }

    public void Start(long? startAt = null, ReceiverPcmReader? readAudio = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Buffered sender already started");
        _startAt = startAt; _readAudio = readAudio; _start.Set();
    }

    private void Run()
    {
        try
        {
            using var encoder = _encoder();
            using var cipher = new CounterCipher(_key);
            _ready.TrySetResult();
            _start.Wait(_stop.Token);
            var audio = new float[2048]; var pcm = new short[2048];
            var wire = _tcp.GetStream();
            long t0 = StartedAt = _startAt ?? MediaClock.Now, inputFrames = 0, outputFrames = 0;
            int quiet = 0;
            uint sequence = 0;
            bool begun = false;
            while (!_stop.IsCancellationRequested)
            {
                long deadline = t0 + (long)(inputFrames * (double)MediaClock.Frequency / BufferedAudio.SampleRate);
                while (MediaClock.Now < deadline)
                    if (_stop.Token.WaitHandle.WaitOne(Math.Max(1, (int)MediaClock.ToMs(deadline - MediaClock.Now)))) return;
                // A master-only sender cannot follow a competing clock. Fail the session explicitly.
                if (!_clock.IsMaster) throw new IOException("PTP master changed; reconnect is required");
                long late = MediaClock.Now - deadline;
                if (late > LateLimit)
                {
                    if (begun) throw new IOException($"Buffered sender missed its audio deadline: block {inputFrames / 1024} {MediaClock.ToMs(late):F0} ms late");
                    // Before the first block the encoder holds nothing, so a late Start joins the common timeline where
                    // it is now, input and RTP time alike, which the rate anchor already maps to the epoch. As in
                    // PtpRealtimeSender: the group's RTSP round trips or a busy machine can start a leg late.
                    long skip = (long)(late * (double)BufferedAudio.SampleRate / 1024 / MediaClock.Frequency) * 1024;
                    Log.Warn($"Buffered sender {MediaClock.ToMs(late):F0} ms late for block {inputFrames / 1024} before sending any, joins at block {(inputFrames + skip) / 1024}");
                    inputFrames += skip; outputFrames += skip;
                    continue;
                }
                begun = true;
                if (_readAudio is { } read) read(deadline, audio);
                else { _fifo.Read(audio); _effects?.Process(audio, 1024); }
                float peak = 0;
                for (int i = 0; i < pcm.Length; i++)
                {
                    float value = float.IsFinite(audio[i]) ? audio[i] : 0;
                    peak = Math.Max(peak, Math.Abs(value));
                    pcm[i] = (short)Math.Clamp((int)Math.Round(value * 32767), short.MinValue, short.MaxValue);
                }
                if (peak < .001f) quiet += 1024;
                else { if (quiet >= BufferedAudio.SampleRate * 3 / 10) OnsetSent?.Invoke(deadline); quiet = 0; }
                foreach (var aac in encoder.Encode(pcm))
                {
                    var packet = BufferedAudio.Packet(cipher, sequence++, unchecked(RtpBase + (uint)outputFrames), _ssrc, aac);
                    wire.Write(packet); outputFrames += 1024; Interlocked.Increment(ref _packets);
                }
                inputFrames += 1024;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { _ready.TrySetCanceled(); }
        catch (Exception ex) { _ready.TrySetException(ex); Failed?.Invoke(ex); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopSending();
        if (_worker.Join(3000)) { _start.Dispose(); _stop.Dispose(); }
    }

    internal void StopSending()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _stop.Cancel(); _tcp.Dispose(); _start.Set();
    }
}
