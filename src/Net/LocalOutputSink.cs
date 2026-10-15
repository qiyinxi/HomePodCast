using HomePodCast.Audio;

namespace HomePodCast.Net;

/// <summary>An endpoint with its own render clock and thread. A lost device ends this attempt; the group retries it.</summary>
internal sealed class LocalOutputSink : IOutputSink
{
    public const string CapturedOutputText = "本机输出设备正被采集，请改用其他采集设备。";
    private readonly LocalOutputOptions _options;
    private readonly Func<string, ILocalRenderDevice> _open;
    private readonly Func<string?> _captured;
    private readonly Func<double> _cap;
    private readonly Thread _thread;
    private readonly AutoResetEvent _go = new(false);
    private readonly TaskCompletionSource _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stop;
    private int _disposed;
    private long _writes;
    private double _volume;
    private Func<double>? _volumeSource;
    private volatile LocalOutputRenderer? _renderer;
    private int _groupDelayMs;

    private LocalOutputSink(LocalOutputOptions options, Func<string, ILocalRenderDevice> open, Func<string?> captured,
        Func<double> cap)
    {
        _options = options; _open = open; _captured = captured; _cap = cap;
        _thread = new Thread(Run) { IsBackground = true, Name = "Group local output", Priority = ThreadPriority.Highest };
    }

    public string Id => _options.DeviceId;
    public string Name => _options.Name;
    public OutputKind Kind => OutputKind.Local;
    public int MinLatencyMs { get; private set; }
    public OutputState State => Volatile.Read(ref _disposed) != 0 ? OutputState.Stopped
        : _lost.Task.IsCompleted ? OutputState.Lost : _started.Task.IsCompletedSuccessfully ? OutputState.Streaming : OutputState.Preparing;
    public Task<string> Lost => _lost.Task;
    public string? LostReason { get; private set; }
    public OutputStats Stats => new(Interlocked.Read(ref _writes), 0, 0, 0);
    public TimelineBuffer? Timeline { get; private set; }
    public double DriftPpm => _renderer?.DriftPpm ?? 0;

    public static async Task<LocalOutputSink> PrepareAsync(LocalOutputOptions options, Func<string?> captured,
        Func<double> cap, CancellationToken ct, Func<string, ILocalRenderDevice>? open = null)
    {
        options.Validate();
        CheckCapture(options.DeviceId, captured());
        ct.ThrowIfCancellationRequested();
        var sink = new LocalOutputSink(options, open ?? (id => new WasapiOutputDevice(id)), captured, cap);
        try
        {
            sink._thread.Start();
            using var cancelled = ct.Register(sink.Dispose);
            await sink._prepared.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return sink;
        }
        catch { sink.Dispose(); throw; }
    }

    public async Task StartAsync(TimelineBuffer timeline, int groupDelayMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (groupDelayMs + _options.TrimMs < MinLatencyMs)
            throw new InvalidOperationException(L.T("分组延迟不足以播放这台输出设备，请提高分组延迟。"));
        if (Timeline != null) throw new InvalidOperationException("Local output already started");
        Timeline = timeline; _groupDelayMs = groupDelayMs;
        _go.Set();
        await _started.Task.WaitAsync(ct);
    }

    public static void CheckCapture(string endpointId, string? captured)
    {
        if (CaptureEndpoint.Same(endpointId, captured)) throw new AirPlayException(L.T(CapturedOutputText));
    }

    public void SetVolume(double percent, bool mute = false)
    {
        Volatile.Write(ref _volume, mute ? 0 : percent);
    }

    internal void BindVolumeSource(Func<double> source)
    {
        if (_volumeSource != null || Timeline != null) throw new InvalidOperationException("Volume source already bound or output started");
        Volatile.Write(ref _volumeSource, source);
    }

    private double ReadVolume() => Volatile.Read(ref _volumeSource)?.Invoke() ?? Volatile.Read(ref _volume);

    private void Run()
    {
        using var go = _go;
        using var mmcss = Native.EnterMmcss("Pro Audio");
        try
        {
            CheckCapture(Id, _captured());
            using var device = _open(Id);
            if (device.Rate <= 0 || device.BufferFrames <= 0 || device.PeriodFrames <= 0 ||
                !double.IsFinite(device.StreamLatencyMs) || device.StreamLatencyMs < 0)
                throw new InvalidOperationException("Invalid WASAPI render dimensions");
            int fill = Math.Min(device.BufferFrames, Math.Max(device.PeriodFrames * 2, device.Rate / 500));
            MinLatencyMs = (int)Math.Ceiling(device.StreamLatencyMs + fill * 1000.0 / device.Rate) + _options.ExtraLatencyMs;
            _prepared.TrySetResult();
            _go.WaitOne();
            if (_stop) return;
            CheckCapture(Id, _captured());
            var renderer = new LocalOutputRenderer(Timeline!, _options, device.Rate, device.BufferFrames, _groupDelayMs, _cap, ReadVolume);
            _renderer = renderer;
            var pcm = new float[device.BufferFrames * 2];
            device.Write(pcm.AsSpan(0, fill * 2)); // only silence until the clock is running and the source exists
            double written = fill;
            device.Start();
            _started.TrySetResult();
            while (!_stop)
            {
                device.WaitReady();
                if (_stop) break;
                CheckCapture(Id, _captured());
                int want = fill - device.PaddingFrames;
                if (want <= 0) continue;
                var position = device.Position;
                written = Math.Max(written, position.Frames); // an endpoint underrun must not replay an old queue position
                var block = pcm.AsSpan(0, want * 2);
                renderer.Render(written, position, block);
                device.Write(block);
                written += want;
                Interlocked.Increment(ref _writes);
            }
        }
        catch (Exception ex)
        {
            _prepared.TrySetException(ex);
            _started.TrySetException(ex);
            if (!_stop) { LostReason = ex.Message; _lost.TrySetResult(ex.Message); }
        }
        finally
        {
            _prepared.TrySetCanceled();
            _started.TrySetCanceled();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        try { _go.Set(); } catch (ObjectDisposedException) { }
        if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0) _go.Dispose();
        else if (Thread.CurrentThread != _thread) _thread.Join(2000);
    }
}
