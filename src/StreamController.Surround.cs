using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

// experimental since 1.4
// 5.1 surround to one HomePod: RunAsync tries it once per connect attempt, before the stereo realtime session, while
// SurroundFor asks for it. Every refusal (SurroundPolicy, the encoder, the capture, the speaker) falls back to stereo in
// the same attempt, with the reason in the status, and is kept for the rest of that Start. Switched off, none of this
// runs: SurroundFor gives null, the capture is never a 5.1 one, and Fifo is the stereo FIFO.
public sealed partial class StreamController
{
    /// <summary>Whether a single speaker's connection tries 5.1 (<see cref="SurroundPolicy.Request"/>). Null: never (CLI, tests, groups).</summary>
    public Func<string, SurroundRequest?>? SurroundFor { get; set; }

    /// <summary>The 5.1 capture (tests: a fake that needs no 5.1 endpoint).</summary>
    internal Func<AudioFifo, SurroundRequest, ISurroundCapture> SurroundCaptureFactory { get; set; } =
        (fifo, r) => new SurroundCapture(fifo, r.CaptureDeviceId, r.Blocked);

    /// <summary>Tests: an AAC encoder without Media Foundation. Null: Windows' encoder.</summary>
    internal Func<IAacEncoder>? SurroundEncoder { get; set; }

    /// <summary>Checked before any network traffic: whether Windows' AAC encoder can do 5.1 (tests: null, it can).</summary>
    internal Func<string?> SurroundEncoderProblem { get; set; } = AacEncoder.SurroundProblem;

    /// <summary>
    /// 48 kHz, 6 channels; created by the first 5.1 attempt and kept across reconnects, as the stereo FIFO is. Not adaptive:
    /// the 40 ms target covers one 1024-frame read (21.3 ms) plus a 10 ms capture packet, and 1.8 s of latency hides more.
    /// </summary>
    private AudioFifo? _surroundFifo;

    private const int SurroundFifoTargetMs = 40, SurroundFifoCapMs = 250;

    /// <summary>The current session streams 5.1.</summary>
    public bool SurroundActive => _client?.SurroundSender != null;

    /// <summary>
    /// One 5.1 attempt: a session, or the refusal (translated) to stream stereo with. A broken connection, a cancellation or
    /// a pairing that is needed propagate, so the usual retry tries 5.1 again.
    /// </summary>
    private async Task<(AirPlayClient? Client, StreamOptions? Options, string? Refusal)> TrySurroundAsync(string deviceId,
        IPAddress address, SurroundRequest r, CancellationToken ct)
    {
        if (r.Refusal is { } refused) return (null, null, refused);
        if (SurroundEncoderProblem() is { } encoder) return (null, null, encoder);

        var capture = EnsureSurroundCapture(r, ct);
        if (await capture.WaitReadyAsync(TimeSpan.FromSeconds(2), ct) is { } notReady)
        {
            DisposeCapture(); // the stereo capture takes over
            return (null, null, notReady);
        }

        EffectiveLatencyMs = SurroundPolicy.LatencyMs + SurroundPolicy.EncoderDelayMs;
        var options = new StreamOptions(SurroundPolicy.LatencyMs, Muted ? 0 : Volume)
        {
            VolumeCapPercent = VolumeCapPercent, Pairings = Pairings, DeviceId = deviceId,
        };
        var setup = new SurroundSetup(_surroundFifo!)
        {
            // The stereo effects are bypassed in 5.1; 夜间自动断开 still sees every block.
            Activity = Effects.Effects.OfType<SoundActivity>().LastOrDefault(),
            SourceProblem = () => capture.Problem,
            Ports = ReceiverTimingPorts ?? PtpPorts.Standard,
            Encoder = SurroundEncoder,
        };
        try
        {
            return (await AirPlayClient.ConnectSurroundAsync(address, AirPlayPort, options, setup, ct), options, null);
        }
        catch (SurroundUnavailableException ex)
        {
            Log.Info($"surround: {AirPlayException.ForLog(ex)}");
            DisposeCapture();
            return (null, null, ex.Message);
        }
    }

    /// <summary>
    /// The 5.1 capture, started unless one runs already (it survives reconnects, as the stereo one does). The stereo capture
    /// goes first: the same output is never captured twice. A loop that Stop has already cancelled starts none.
    /// </summary>
    private ISurroundCapture EnsureSurroundCapture(SurroundRequest r, CancellationToken ct)
    {
        if (_capture is not ISurroundCapture) DisposeCapture();
        var fifo = LazyInitializer.EnsureInitialized(ref _surroundFifo,
            () => new AudioFifo(SurroundSender.Rate, SurroundFifoTargetMs, SurroundFifoCapMs, SurroundSender.Channels));
        fifo.BeginSession();
        lock (_lock)
        {
            if (_capture is ISurroundCapture running) return running;
            ct.ThrowIfCancellationRequested();
            var capture = SurroundCaptureFactory(fifo, r);
            _capture = capture;
            capture.DeviceChanged += _ => Changed?.Invoke();
            capture.Start();
            return capture;
        }
    }

    /// <summary>The 已连接 line: the home-theater takeover text wins; then 5.1, or why this Start streams stereo.</summary>
    private string StreamingText(object? name, AirPlayClient client, string? surroundRefusal) =>
        _takenFrom != null ? ConnectedText(name)
        : client.SurroundSender != null ? L.F("已连接 · {0} · 实验性 5.1 环绕声", name) // experimental since 1.4
        : surroundRefusal != null ? L.F("已连接 · {0} · 立体声。未用 5.1 环绕声：{1}", name, surroundRefusal)
        : ConnectedText(name);

    private void LogSurroundStats(AirPlayClient client, SurroundSender sender)
    {
        if (_surroundFifo is not { } fifo) return;
        var clock = client.SurroundClock;
        Log.Info($"stats: surround fifo={fifo.Depth * 1000.0 / fifo.SampleRate:F0}ms target={fifo.TargetMs:F0}ms " +
                 $"drift={_capture?.DriftPpm ?? 0:F0}ppm underruns={fifo.Underruns} idle={fifo.IdleGaps} " +
                 $"maxGap={_capture?.TakeMaxGapMs() ?? 0:F0}ms overflows={fifo.Overflows} sent={sender.PacketsSent} " +
                 $"maxLate={sender.MaxLateMs:F1}ms ptpDrift={clock?.DriftPpm:F1}ppm syncAge={clock?.SyncAgeMs}ms {_net?.TakeSummary()}");
    }
}
