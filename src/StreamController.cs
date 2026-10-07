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
public sealed class StreamController : IDisposable
{
    private static readonly TimeSpan TakeoverGrace = TimeSpan.FromSeconds(15);

    private readonly AudioFifo _fifo = new(RtpSender.SampleRate, targetMs: 20, capMs: 60);
    private readonly object _lock = new();
    private LoopbackCapture? _capture;
    private AirPlayClient? _client;
    private CancellationTokenSource? _run;
    private Task? _loop;

    public StreamState State { get; private set; } = StreamState.Idle;
    public string StatusText { get; private set; } = "未连接";
    public AirPlayClient? Client => _client;
    public AudioFifo Fifo => _fifo;
    public LoopbackCapture? Capture => _capture;

    /// <summary>Last volume confirmed on the speaker (percent).</summary>
    public double? Volume { get; private set; }

    public event Action? Changed;

    public void Start(string deviceId, string? host, int latencyMs, double? volume)
    {
        Stop();
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _run = cts;
            _loop = Task.Run(() => RunAsync(deviceId, host, latencyMs, volume, cts.Token));
        }
    }

    public void Stop()
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
        _capture?.Dispose();
        _capture = null;
        Set(StreamState.Idle, "未连接");
    }

    public void SetVolume(double percent)
    {
        Volume = percent;
        var client = _client;
        if (client == null) return;
        Task.Run(() =>
        {
            try { client.SetVolumePercent(percent); }
            catch (Exception ex) { Log.Warn($"set volume: {ex.Message}"); }
        });
    }

    private async Task RunAsync(string deviceId, string? host, int latencyMs, double? volume, CancellationToken ct)
    {
        int attempt = 0;
        Volume = volume;
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            string? lostReason = null;
            try
            {
                Set(StreamState.Connecting, attempt == 0 ? "正在连接…" : $"正在重连（第 {attempt} 次）…");
                var address = await ResolveAsync(deviceId, host, ct);
                host = address.ToString();
                HostResolved?.Invoke(host);

                if (_capture == null)
                {
                    _capture = new LoopbackCapture(_fifo, RtpSender.SampleRate);
                    _capture.DeviceChanged += _ => Changed?.Invoke();
                    _capture.Start();
                }

                var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var client = await AirPlayClient.ConnectAsync(address, 7000, new StreamOptions(latencyMs, Volume), _fifo, ct);
                client.Lost += r => lost.TrySetResult(r);
                _client = client;
                Volume ??= client.InitialVolumeDb is { } db ? AirPlayClient.DbToPercent(db) : null;
                attempt = 0;
                Set(StreamState.Streaming, $"已连接 · {client.Info.GetValueOrDefault("name")}");

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
                if (ex is FirewallBlockedException) FirewallBlocked?.Invoke();
            }
            finally
            {
                TearDown();
            }

            if (ct.IsCancellationRequested) break;

            if (State == StreamState.Streaming && started.Elapsed > TakeoverGrace &&
                lostReason == EventChannel.ClosedBySpeaker)
            {
                // The speaker ended a healthy session: most likely someone AirPlayed to it. Don't fight back.
                _capture?.Dispose();
                _capture = null;
                Set(StreamState.Idle, "已断开（音箱可能被其他设备占用）");
                return;
            }

            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            Set(StreamState.Retrying, $"{lostReason}，{delay.TotalSeconds:F0} 秒后重试");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Raised when the speaker's address was (re)discovered, so it can be remembered.</summary>
    public event Action<string>? HostResolved;

    public event Action? FirewallBlocked;

    private static async Task<IPAddress> ResolveAsync(string deviceId, string? host, CancellationToken ct)
    {
        if (host != null && IPAddress.TryParse(host, out var known) && await IsReachable(known, ct))
            return known;
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct);
        var match = devices.FirstOrDefault(d => string.Equals(Normalize(d.DeviceId), Normalize(deviceId), StringComparison.OrdinalIgnoreCase));
        return match?.Address ?? throw new AirPlayException("找不到音箱（是否通电、和电脑在同一网络？）");
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
        Log.Info($"stats: fifo={_fifo.Depth * 1000.0 / RtpSender.SampleRate:F0}ms drift={_capture?.DriftPpm ?? 0:F0}ppm " +
                 $"underruns={_fifo.Underruns} overflows={_fifo.Overflows} sent={s.PacketsSent} late={s.LateWakeups} " +
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
