using HomePodCast.Audio;
using HomePodCast.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HomePodCast.Net;

// experimental since 1.4
/// <summary>
/// The HomePod's 5.1 stream: paced type 103 AAC-LC 48 kHz 5.1 from the 6-channel FIFO over the data connection, with
/// SSRC 0x27000000. The experiment's verified sender (2026-10-28) with a FIFO for input; <see cref="BufferedSender"/>
/// stays as it is for the third-party path. Its worker thread owns the AAC encoder and the cipher.
/// </summary>
/// <remarks>
/// Blocks are paced on the HomePod's PTP time, not on QPC: block n goes out when the HomePod's clock reads the anchor's
/// start + n·1024/48000, so the lead stays exactly the anchor latency whatever QPC and the HomePod's clock drift apart
/// (20-50 ppm would be 72-180 ms an hour). The capture then steers the sound card to the FIFO's depth, which locks it to
/// the HomePod's clock as well. No re-anchoring.
/// </remarks>
internal sealed class SurroundSender : IDisposable
{
    public const int Channels = 6, Rate = 48000, Frames = 1024;
    private static readonly long LateLimit = MediaClock.FromMs(250);
    private readonly TcpClient _tcp;
    private readonly byte[] _key;
    private readonly uint _ssrc;
    private readonly SurroundSetup _setup;
    private readonly IPtpTimeline _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _start = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private long _packets, _bytes, _received, _maxLateTicks, _startAt;
    private ulong _startHp;
    private int _disposed, _started, _failed;

    public uint RtpBase { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public long PacketsSent => Interlocked.Read(ref _packets);
    public long BytesSent => Interlocked.Read(ref _bytes);

    /// <summary>The latest a block went out after its deadline, in ms (stats).</summary>
    public double MaxLateMs => MediaClock.ToMs(Interlocked.Read(ref _maxLateTicks));

    /// <summary>The encoder's accepted output type; empty with a test encoder.</summary>
    public string EncoderSettings { get; private set; } = "";

    /// <summary>11 B0 with Windows' encoder; empty with a test encoder.</summary>
    public byte[] AudioSpecificConfig { get; private set; } = [];

    public string? FailedReason { get; private set; }

    /// <summary>Once: (reason, translated when it is ours; whether the HomePod closed the data connection).</summary>
    public event Action<string, bool>? Failed;

    private SurroundSender(TcpClient tcp, byte[] key, uint ssrc, SurroundSetup setup, IPtpTimeline clock)
    {
        _tcp = tcp; _key = key; _ssrc = ssrc; _setup = setup; _clock = clock;
        _worker = new Thread(Run) { IsBackground = true, Name = "AirPlay surround AAC" };
        _worker.Start();
    }

    /// <summary>
    /// Connects the data connection and builds the encoder on the worker. A data connection that fails is a 5.1 refusal
    /// (R9), an encoder that fails is one too (R3): the caller then streams stereo.
    /// </summary>
    public static async Task<SurroundSender> ConnectAsync(IPAddress host, int port, byte[] key, uint ssrc, SurroundSetup setup,
        IPtpTimeline clock, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, SendTimeout = 2000 };
        SurroundSender? sender = null;
        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(host, port, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                string detail = ex is SocketException s ? $"data connection {host}:{port}: {s.SocketErrorCode}" : $"data connection {host}:{port}: no answer in 5 s";
                throw new SurroundUnavailableException(L.F("5.1 连接失败：{0}", detail)) { LogMessage = $"5.1 connection failed: {detail}" };
            }
            sender = new(tcp, key, ssrc, setup, clock);
            await sender._ready.Task.WaitAsync(ct);
            return sender;
        }
        catch { if (sender != null) sender.Dispose(); else tcp.Dispose(); throw; }
    }

    /// <summary>
    /// Starts pacing and the data connection's read loop (after the caller subscribed to <see cref="Failed"/>).
    /// </summary>
    /// <param name="startAtQpc">QPC instant of block 0, which the rate anchor maps to <see cref="RtpBase"/>.</param>
    /// <param name="startHpNs">The HomePod's time at <paramref name="startAtQpc"/>.</param>
    public void Start(long startAtQpc, ulong startHpNs)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("surround sender already started");
        _startAt = startAtQpc; _startHp = startHpNs;
        _ = ReadAsync();
        _start.Set();
    }

    private IAacEncoder CreateEncoder()
    {
        if (_setup.Encoder is { } test) return test();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AAC encoding requires Windows");
        var encoder = new AacEncoder(Channels, Rate);
        try
        {
            byte[] asc = encoder.AudioSpecificConfig();
            if (!asc.AsSpan().SequenceEqual(AacEncoder.Surround51Asc))
                throw new NotSupportedException($"AAC 5.1: AudioSpecificConfig {Convert.ToHexString(asc)}, not 11B0 ({encoder.Settings})");
            EncoderSettings = encoder.Settings;
            AudioSpecificConfig = asc;
            return encoder;
        }
        catch { encoder.Dispose(); throw; }
    }

    private void Run()
    {
        IAacEncoder encoder;
        try { encoder = CreateEncoder(); }
        catch (Exception ex)
        {
            Log.Warn($"5.1: the AAC encoder failed: {ex.GetType().Name}: {ex.Message}");
            _ready.TrySetException(new SurroundUnavailableException(L.T("Windows 的 AAC 编码器不支持 5.1"))
                { LogMessage = $"AAC 5.1 encoder: {ex.GetType().Name}: {ex.Message}" });
            return;
        }
        try
        {
            using (encoder)
            using (var cipher = new CounterCipher(_key))
            {
                _ready.TrySetResult();
                _start.Wait(_stop.Token);
                Send(encoder, cipher);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { _ready.TrySetCanceled(); }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            Fail(ex is IOException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", ex is IOException { InnerException: SocketException s } && Closed(s));
        }
    }

    private void Send(IAacEncoder encoder, CounterCipher cipher)
    {
        var audio = new float[Frames * Channels];
        var pcm = new short[Frames * Channels];
        var wire = _tcp.GetStream();
        long inputFrames = 0, outputFrames = 0;
        long offset = (long)_startHp - PtpClockObserver.LocalNs(_startAt);
        uint sequence = 0;
        bool begun = false;
        while (!_stop.IsCancellationRequested)
        {
            offset = _clock.OffsetNs ?? offset;
            long deadline = SurroundPacing.DeadlineQpc(_startHp, inputFrames, Rate, offset);
            while (MediaClock.Now < deadline)
                if (_stop.Token.WaitHandle.WaitOne(Math.Max(1, (int)MediaClock.ToMs(deadline - MediaClock.Now)))) return;
            if ((_clock.Problem ?? _setup.SourceProblem()) is { } problem) throw new IOException(problem);
            long late = MediaClock.Now - deadline;
            if (late > LateLimit)
            {
                // After a sleep or a stall the HomePod's buffer has run out: a lost session and a reconnect.
                if (begun) throw new IOException(L.T("音频没能及时发出，电脑可能太忙或刚从睡眠恢复"));
                // Before the first block the encoder holds nothing: join the anchored timeline where it is now, input and
                // RTP time alike.
                long skip = (long)(late * (double)Rate / Frames / MediaClock.Frequency) * Frames;
                Log.Warn($"5.1: {MediaClock.ToMs(late):F0} ms late for block {inputFrames / Frames} before sending any, joins at block {(inputFrames + skip) / Frames}");
                inputFrames += skip; outputFrames += skip;
                continue;
            }
            if (late > Interlocked.Read(ref _maxLateTicks)) Interlocked.Exchange(ref _maxLateTicks, late);
            begun = true;
            _setup.Fifo.Read(audio);
            _setup.Activity?.Observe(audio);
            for (int i = 0; i < pcm.Length; i++)
            {
                float value = float.IsFinite(audio[i]) ? audio[i] : 0;
                pcm[i] = (short)Math.Clamp((int)Math.Round(value * 32767), short.MinValue, short.MaxValue);
            }
            foreach (var aac in encoder.Encode(pcm))
            {
                var packet = BufferedAudio.Packet(cipher, sequence++, unchecked(RtpBase + (uint)outputFrames), _ssrc, aac);
                wire.Write(packet);
                outputFrames += Frames;
                Interlocked.Increment(ref _packets);
                Interlocked.Add(ref _bytes, packet.Length);
            }
            inputFrames += Frames;
        }
    }

    /// <summary>The HomePod sends nothing on the data connection: its closing it (a takeover, a reboot) is reported.</summary>
    private async Task ReadAsync()
    {
        var buffer = new byte[2048];
        try
        {
            var stream = _tcp.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, _stop.Token);
                if (n == 0) { Fail(L.T("HomePod 关闭了音频连接"), receiverClosed: true); return; }
                if (Interlocked.Add(ref _received, n) == n)
                    Log.Debug($"5.1: the HomePod sent {n} bytes on the data connection: {Convert.ToHexString(buffer.AsSpan(0, Math.Min(n, 32)))}");
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException { InnerException: SocketException s } && Closed(s))
        {
            Fail(L.T("HomePod 关闭了音频连接"), receiverClosed: true);
        }
        catch (Exception ex) { Fail($"data connection: {ex.GetType().Name}: {ex.Message}", receiverClosed: false); }
    }

    /// <summary>A reset or abort is the HomePod ending the connection as much as a FIN is.</summary>
    private static bool Closed(SocketException s) => s.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;

    private void Fail(string reason, bool receiverClosed)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        FailedReason = reason;
        Log.Warn($"5.1 sender stopped: {reason}{(receiverClosed ? " (the HomePod closed the data connection)" : "")}");
        Failed?.Invoke(reason, receiverClosed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _tcp.Dispose(); _start.Set();
        if (_worker.Join(3000)) { _start.Dispose(); _stop.Dispose(); }
    }
}

internal static class SurroundPacing
{
    /// <summary>
    /// QPC instant at which the HomePod's time is <paramref name="startHpNs"/> + <paramref name="frames"/>/<paramref name="rate"/>,
    /// given HomePod time = <see cref="PtpClockObserver.LocalNs"/> + <paramref name="offsetNs"/>.
    /// </summary>
    public static long DeadlineQpc(ulong startHpNs, long frames, int rate, long offsetNs)
    {
        Int128 homePod = (Int128)startHpNs + (Int128)frames * 1_000_000_000 / rate;
        return PtpClockObserver.QpcAt((long)(homePod - offsetNs));
    }
}
