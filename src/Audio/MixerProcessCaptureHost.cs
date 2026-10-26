using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace HomePodCast.Audio;

/// <summary>Production adapter, started only by an explicit mixer connection. All process clients live on one MTA.</summary>
public sealed class MixerProcessCaptureHost : IMixerCaptureHost
{
    private readonly object _settingsGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly MixerCaptureHub _hub;
    private readonly SessionRouter _router;
    private readonly AppRouting _legacyRouting;
    private readonly AppConfig _legacyConfig = new() { AutoConnect = false };
    private readonly ConcurrentQueue<LoopbackStream> _disposeInputs = new();
    private AppConfig _config = new() { AutoConnect = false };
    private MixerRoutingSettings _routing = new();
    private volatile IReadOnlyList<RouteTarget> _roots = [];
    private volatile bool _stop;
    private bool _started;
    private long _generation;
    private int _busRevision;
    private volatile string? _endpoint;
    private volatile bool _missing;
    private readonly Dictionary<uint, long> _holdUntil = [];
    private readonly Func<IReadOnlyDictionary<string, int>>? _sendVolumes;

    public MixerProcessCaptureHost(MixSources sources, Func<IReadOnlyDictionary<string, int>>? sendVolumes = null, AppRouting? displayRouting = null)
    {
        _sendVolumes = sendVolumes;
        _legacyRouting = new(_legacyConfig, () => { }, forceProcessCapture: true);
        _legacyRouting.SendStatusTarget = displayRouting;
        _hub = new(44100, Open, sources, Gain);
        _router = new(_legacyRouting, _hub.IsCapturing);
        _router.PlanChanged += () => _wake.Set();
        _thread = new Thread(Run) { IsBackground = true, Name = "Mixer shared capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public IReadOnlyList<RouteTarget> Roots => _roots;
    public string? OwnerKeyFor(uint pid) => _router.OwnerKeyFor(pid);
    public event Action? RootsChanged;
    public void Update(AppConfig config, MixerRoutingSettings routing)
    {
        var snapshot = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        lock (_settingsGate) { _config = snapshot; _routing = routing; _generation++; }
        _wake.Set();
    }
    public ICaptureSource Subscribe(string key, AudioFifo fifo)
    {
        var lease = new EndpointLease(_hub.Subscribe(key, fifo), this);
        Interlocked.Increment(ref _busRevision); _wake.Set();
        return lease;
    }
    public void Start() { if (_started) return; _started = true; _thread.Start(); }

    private IMixerProcessInput Open(RouteTarget target)
    {
        var stream = LoopbackStream.OpenProcess(target.RootPid, true, 44100, _wake.SafeWaitHandle.DangerousGetHandle(), 200_000);
        try { stream.Start(); _router.Poke(); return new Input(stream, _disposeInputs); }
        catch { stream.Dispose(); throw; }
    }

    private PacketGain Gain(RouteTarget target, ReadOnlySpan<float> samples)
    {
        long now = Stopwatch.GetTimestamp();
        float peak = 0;
        foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        bool confirmed = target.Compensate && now >= _holdUntil.GetValueOrDefault(target.RootPid) && _router.IsConfirmed(target.RootPid, now);
        int? send = (_sendVolumes?.Invoke() ?? _legacyRouting.Rules.SendVolumes).TryGetValue(target.Key, out int level) ? level : null;
        float normalization = send != null ? _router.SendNormalization(target.RootPid, now) : 1;
        var gain = RoutedCapture.ComposeGain(send, target.Compensate, confirmed, normalization, peak);
        if (confirmed && gain.Gain == 1)
        { _holdUntil[target.RootPid] = now + Stopwatch.Frequency * 3 / 10; _router.Poke(); }
        return gain;
    }

    private void Run()
    {
        using var mmcss = Native.EnterMmcss("Audio");
        long applied = -1, nextRefresh = 0, nextCycle = Stopwatch.GetTimestamp();
        RoutePlan? previous = null;
        long plannedGeneration = -1;
        int plannedBuses = -1;
        var identities = new Dictionary<uint, long>();
        try
        {
            _router.Start();
            while (!_stop)
            {
                long now = Stopwatch.GetTimestamp();
                bool refreshing = now >= nextRefresh;
                if (refreshing)
                {
                    nextRefresh = now + Stopwatch.Frequency;
                    AppConfig config; MixerRoutingSettings routing; long generation;
                    lock (_settingsGate) { config = _config; routing = _routing; generation = _generation; }
                    if (generation != applied)
                    {
                        AudioRoute Legacy(MixerRoute r) => r.Output.Kind == MixerOutputKind.Local ? AudioRoute.Local : r.AlsoLocal ? AudioRoute.Both : AudioRoute.HomePod;
                        _legacyConfig.RouteDefault = Legacy(routing.Default);
                        _legacyConfig.AppRoutes = routing.Apps.ToDictionary(p => p.Key, p => Legacy(p.Value));
                        _legacyConfig.AppSendVolumes = new(config.AppSendVolumes);
                        _legacyConfig.CaptureDeviceId = config.CaptureDeviceId;
                        _legacyConfig.CaptureDeviceName = config.CaptureDeviceName;
                        _legacyRouting.Reload(); applied = generation;
                    }
                    _endpoint = CaptureEndpoint.Captured(config.CaptureDeviceId, AudioEndpoints.DefaultOutputId());
                    _missing = _endpoint == null || !AudioEndpoints.Outputs().Any(d => CaptureEndpoint.Same(d.Id, _endpoint));
                    foreach (var root in _router.Plan.Targets)
                    {
                        try { using var process = Process.GetProcessById((int)root.RootPid); identities[root.RootPid] = process.StartTime.ToFileTimeUtc(); }
                        catch { identities[root.RootPid] = long.MinValue; }
                    }
                    foreach (uint pid in identities.Keys.Where(pid => !_router.Plan.Targets.Any(t => t.RootPid == pid)).ToArray()) identities.Remove(pid);
                }
                var plan = _router.Plan;
                AppConfig current; MixerRoutingSettings settings; long version;
                lock (_settingsGate) { current = _config; settings = _routing; version = _generation; }
                var roots = _missing ? Array.Empty<RouteTarget>() : plan.Targets;
                bool planChanged = !ReferenceEquals(plan, previous) || !_roots.SequenceEqual(roots);
                if (planChanged) { previous = plan; _roots = roots; RootsChanged?.Invoke(); }
                // Apply is pure ownership reconciliation. Native clients are activated/drained/disposed on this thread.
                int buses = Volatile.Read(ref _busRevision);
                if (refreshing || planChanged || version != plannedGeneration || plannedBuses != buses)
                {
                    _hub.Apply(new(version, roots.Where(t => identities.GetValueOrDefault(t.RootPid) != long.MinValue).Select(t => new MixerRoot(t,
                        MixerDestinationResolver.Resolve(current, settings.For(t.Key)).Key, identities.GetValueOrDefault(t.RootPid))).ToArray()));
                    plannedGeneration = version; plannedBuses = buses;
                    _router.Poke();
                }
                while (_disposeInputs.TryDequeue(out var disposed)) disposed.Dispose();
                if (now >= nextCycle)
                {
                    _hub.Cycle(); nextCycle = now + Stopwatch.Frequency / 100;
                }
                _wake.WaitOne(5);
            }
        }
        catch (Exception ex) { Log.Warn($"shared mixer capture stopped: {ex.Message}"); }
        finally
        {
            _hub.Dispose();
            while (_disposeInputs.TryDequeue(out var disposed)) disposed.Dispose();
            _router.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true; _wake.Set();
        if (_thread.IsAlive) _thread.Join();
        if (!_started) { _hub.Dispose(); _router.Dispose(); }
        _wake.Dispose();
    }
    private sealed class Input(LoopbackStream stream, ConcurrentQueue<LoopbackStream> disposal) : IMixerProcessInput
    {
        public bool Drain(List<float> samples) => stream.Drain(samples);
        public void Dispose() => disposal.Enqueue(stream);
    }
    private sealed class EndpointLease(ICaptureSource bus, MixerProcessCaptureHost owner) : ICaptureSource
    {
        public string? DeviceName => bus.DeviceName;
        public double DriftPpm => bus.DriftPpm;
        public float Peak => bus.Peak;
        public int ExtraLatencyMs => bus.ExtraLatencyMs;
        public string? EndpointId => owner._endpoint;
        public bool NoOutputDevice => owner._missing;
        public bool CaptureDeviceMissing => owner._missing;
        public event Action<string>? DeviceChanged { add => bus.DeviceChanged += value; remove => bus.DeviceChanged -= value; }
        public void Start() => bus.Start();
        public void Dispose() { bus.Dispose(); Interlocked.Increment(ref owner._busRevision); owner._wake.Set(); }
    }
}
