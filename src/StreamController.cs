using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

public enum StreamState { Idle, Connecting, Streaming, Retrying }

/// <summary>
/// Owns capture + FIFO + AirPlay session and keeps them alive: retries with backoff, re-discovers the
/// speaker if its address changed, and backs off (instead of fighting) when another sender takes over.
/// All events are raised on the thread pool; the UI marshals them.
/// </summary>
public sealed partial class StreamController : IDisposable
{
    private static readonly TimeSpan TakeoverGrace = TimeSpan.FromSeconds(15);

    private readonly AudioFifo _fifo;

    public StreamController(int fifoTargetMs = AppConfig.DefaultFifoTargetMs)
    {
        fifoTargetMs = Math.Clamp(fifoTargetMs, 5, 100);
        _fifo = new AudioFifo(RtpSender.SampleRate, targetMs: fifoTargetMs, capMs: fifoTargetMs * 3 + 10)
        {
            // Adaptive: each dropout adds 4 ms (up to 30), 10 clean minutes take 1 ms back. Measured 2026-10-08 on a
            // virtual sound card: a fixed 12 ms ran dry about once a minute while the network side was perfect.
            MaxTargetFrames = Math.Max(fifoTargetMs, MaxFifoTargetMs) * RtpSender.SampleRate / 1000,
            RelaxAfterReads = 600L * RtpSender.SampleRate / RtpSender.FramesPerPacket,
        };
    }

    private const int MaxFifoTargetMs = 30;
    private readonly object _lock = new();
    private ICaptureSource? _capture;
    private AirPlayClient? _client;
    private CancellationTokenSource? _run;
    private Task? _loop;

    public StreamState State { get; private set; } = StreamState.Idle;
    public string StatusText { get; private set; } = L.T("未连接");
    public AirPlayClient? Client => _client;
    public AudioFifo Fifo => _fifo;
    public ICaptureSource? Capture => _capture;

    /// <summary>Creates the capture that feeds the FIFO (the UI swaps in per-app routing).</summary>
    public Func<AudioFifo, ICaptureSource> CaptureFactory { get; set; } = fifo => new LoopbackCapture(fifo, RtpSender.SampleRate);

    /// <summary>Last volume confirmed on the speaker (percent).</summary>
    public double? Volume { get; private set; }

    /// <summary>The speaker's own arrival-to-playout time, remembered across connections.</summary>
    public int? ArrivalToRenderMs { get; set; }

    /// <summary>Latency actually requested for the current session (after the safety floor).</summary>
    public int EffectiveLatencyMs { get; private set; }

    /// <summary>Below the speaker's processing time + this margin, every bit of Wi-Fi jitter is audible.</summary>
    public const int SafetyMarginMs = 15;

    public int SafeLatency(int requestedMs) => Math.Max(requestedMs, (ArrivalToRenderMs ?? 85) + SafetyMarginMs);

    public event Action? Changed;

    /// <summary>Status text (translated with L.T) after another sender took the speaker over; TrayApp checks for it.</summary>
    public const string TakenOverText = "已断开（音箱可能被其他设备占用）";

    public void Start(string deviceId, string? host, int latencyMs, double? volume)
    {
        lock (_lifecycle)
        {
            Stop();
            var cts = new CancellationTokenSource();
            lock (_lock)
            {
                _run = cts;
                _loop = Task.Run(() => RunAsync(deviceId, host, latencyMs, volume, cts.Token));
            }
        }
    }

    /// <summary>
    /// Serializes Start/StartGroup/Stop: Disconnect stops on the thread pool, and a Connect right after it must
    /// not have its new capture or state torn down by that Stop finishing late.
    /// </summary>
    private readonly object _lifecycle = new();

    public void Stop()
    {
        lock (_lifecycle)
        {
            Task? loop;
            lock (_lock)
            {
                _run?.Cancel();
                loop = _loop;
                _run = null;
                _loop = null;
            }
            try { loop?.Wait(3000); } catch { }
            TearDown();
            TearDownGroup();
            DisposeCapture();
            Set(StreamState.Idle, L.T("未连接"));
        }
    }

    private void DisposeCapture()
    {
        ICaptureSource? capture;
        lock (_lock) (capture, _capture) = (_capture, null);
        capture?.Dispose(); // RoutedCapture: makes silenced apps audible here again
    }

    public void SetVolume(double percent)
    {
        Volume = VolumeLimit.Clamp(percent, VolumeCapPercent);
        Muted = false;
        PushVolume();
    }

    private async Task RunAsync(string deviceId, string? host, int latencyMs, double? volume, CancellationToken ct)
    {
        int attempt = 0;
        Volume = volume is { } v ? VolumeLimit.Clamp(v, VolumeCapPercent) : null;
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            string? lostReason = null;
            AirPlayClient? mine = null;
            try
            {
                Set(StreamState.Connecting, attempt == 0 ? L.T("正在连接…") : L.F("正在重连（第 {0} 次）…", attempt));
                var address = await ResolveAsync(deviceId, host, ct);
                host = address.ToString();
                HostResolved?.Invoke(host);

                EnsureCapture(ct);

                var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                EffectiveLatencyMs = SafeLatency(latencyMs);
                if (EffectiveLatencyMs != latencyMs)
                    Log.Warn($"latency {latencyMs} ms is below what the speaker can handle; using {EffectiveLatencyMs} ms");
                var options = new StreamOptions(EffectiveLatencyMs, Muted ? 0 : Volume) { VolumeCapPercent = VolumeCapPercent, Effects = Effects };
                var client = await AirPlayClient.ConnectAsync(address, 7000, options, _fifo, ct);
                client.Lost += r => lost.TrySetResult(r);
                // The RTSP setup ignores ct, so Stop may have given up waiting and a newer loop may own _client
                // by now: publish only while still current, and never touch another loop's client.
                lock (_lock)
                {
                    if (!ct.IsCancellationRequested) _client = mine = client;
                }
                if (mine == null)
                {
                    client.Dispose();
                    break;
                }
                client.VolumeCapPercent = VolumeCapPercent;
                if (options.VolumeCapPercent != VolumeCapPercent || options.VolumePercent != (Muted ? 0 : Volume))
                    PushVolume(); // cap or volume changed while connecting (PushVolume had no client then)
                if (client.ArrivalToRenderMs is { } a2r && a2r != ArrivalToRenderMs)
                {
                    ArrivalToRenderMs = a2r;
                    ArrivalToRenderChanged?.Invoke(a2r);
                }
                Volume ??= client.InitialVolumeDb is { } db ? VolumeLimit.Clamp(AirPlayClient.DbToPercent(db), VolumeCapPercent) : null;
                attempt = 0;
                Set(StreamState.Streaming, L.F("已连接 · {0}", client.Info.GetValueOrDefault("name")));

                using (var statsTimer = new System.Threading.Timer(_ => LogStats(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)))
                    lostReason = await lost.Task.WaitAsync(ct);
                LogStats();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lostReason = ex is FirewallBlockedException or AirPlayException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                Log.Warn($"stream: {lostReason}");
                if (ex is FirewallBlockedException && !ct.IsCancellationRequested) FirewallBlocked?.Invoke();
            }
            finally
            {
                // Only this loop's own session (Stop may already have taken and disposed it).
                if (mine != null && Interlocked.CompareExchange(ref _client, null, mine) == mine) mine.Dispose();
            }

            if (ct.IsCancellationRequested) break;

            if (State == StreamState.Streaming && started.Elapsed > TakeoverGrace &&
                lostReason == EventChannel.ClosedBySpeaker)
            {
                // The speaker ended a healthy session: most likely someone AirPlayed to it. Don't fight back.
                DisposeCapture();
                Set(StreamState.Idle, L.T(TakenOverText));
                return;
            }

            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            var shown = lostReason == EventChannel.ClosedBySpeaker ? L.T(EventChannel.ClosedBySpeaker) : lostReason;
            Set(StreamState.Retrying, L.F("{0}，{1:F0} 秒后重试", shown, delay.TotalSeconds));
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Capture runs across reconnects; shared by the single-speaker and the group loop. A loop that Stop has
    /// already cancelled (it gave up waiting for it) must not start one nobody would dispose.
    /// </summary>
    private void EnsureCapture(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_capture != null) return;
            ct.ThrowIfCancellationRequested();
            _capture = CaptureFactory(_fifo);
            _capture.DeviceChanged += _ => Changed?.Invoke();
            _capture.Start();
        }
    }

    /// <summary>Raised when the speaker's address was (re)discovered, so it can be remembered.</summary>
    public event Action<string>? HostResolved;

    public event Action? FirewallBlocked;

    public event Action<int>? ArrivalToRenderChanged;

    private static async Task<IPAddress> ResolveAsync(string deviceId, string? host, CancellationToken ct)
    {
        if (host != null && IPAddress.TryParse(host, out var known) && await IsReachable(known, ct))
            return known;
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct);
        var match = devices.FirstOrDefault(d => string.Equals(Normalize(d.DeviceId), Normalize(deviceId), StringComparison.OrdinalIgnoreCase));
        return match?.Address ?? throw new AirPlayException(L.T("找不到音箱（是否通电、和电脑在同一网络？）"));
    }

    public static string Normalize(string id) => id.Replace(":", "").Replace("-", "");

    private static async Task<bool> IsReachable(IPAddress ip, CancellationToken ct)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1500);
        try
        {
            await tcp.ConnectAsync(ip, 7000, timeout.Token);
            return true;
        }
        catch when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void LogStats()
    {
        var s = _client?.Sender;
        if (s == null) return;
        Log.Info($"stats: fifo={_fifo.Depth * 1000.0 / RtpSender.SampleRate:F0}ms target={_fifo.TargetMs:F0}ms " +
                 $"drift={_capture?.DriftPpm ?? 0:F0}ppm underruns={_fifo.Underruns} idle={_fifo.IdleGaps} " +
                 $"maxGap={_capture?.TakeMaxGapMs() ?? 0:F0}ms overflows={_fifo.Overflows} " +
                 $"sent={s.PacketsSent} late={s.LateWakeups} " +
                 $"maxLate={s.MaxLateMs:F1}ms skipped={s.SkippedPackets} rtx={s.Retransmitted}/{s.RetransmitRequests} " +
                 $"rtxMiss={s.RetransmitMisses}");
    }

    private void TearDown()
    {
        var client = Interlocked.Exchange(ref _client, null);
        client?.Dispose();
    }

    private void Set(StreamState state, string text)
    {
        State = state;
        StatusText = text;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
