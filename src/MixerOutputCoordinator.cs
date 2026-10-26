using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

public interface IMixerDestinationSession : IDisposable
{
    bool WaitingForDiscovery => false;
    StreamController? Controller { get; }
    StreamState State { get; }
    string Status { get; }
    int EffectiveLatencyMs { get; }
    event Action? Changed;
    void Start(ConnectTrigger trigger);
    Task StopAsync(string? reason = null);
}

public sealed class MixerStreamSession(ResolvedMixerDestination destination, StreamController controller, Func<bool>? canStart = null) : IMixerDestinationSession
{
    public bool WaitingForDiscovery { get; private set; }
    public StreamController Controller => controller;
    public StreamState State => controller.State;
    public string Status => controller.StatusText;
    public int EffectiveLatencyMs => controller.EffectiveLatencyMs;
    public event Action? Changed { add => controller.Changed += value; remove => controller.Changed -= value; }
    public void Start(ConnectTrigger trigger)
    {
        WaitingForDiscovery = canStart?.Invoke() == false;
        if (WaitingForDiscovery) { controller.WaitForMixerDiscovery(); return; }
        var cfg = destination.Config!;
        double? volume = controller.Volume ?? cfg.Volume;
        int volumeAsOf = controller.VolumeChanges;
        if (GroupPlan.FromConfig(cfg) is { } group) controller.StartGroup(group, cfg.LatencyMs, volume, volumeAsOf, trigger: trigger);
        else
        {
            var receiver = cfg.DeviceId == null ? null : ReceiverLatencySelection.Create(controller.ReceiverLookup?.Invoke(cfg.DeviceId), controller.ReceiverSettings?.Invoke(cfg.DeviceId));
            controller.Start(cfg.DeviceId!, cfg.Host, receiver?.Value ?? cfg.LatencyMs, volume, volumeAsOf, trigger: trigger);
        }
    }
    public Task StopAsync(string? reason = null) => Task.Run(() => controller.StopFullyAsync(reason));
    public void Dispose() { controller.CheckMixerGroupLease = null; controller.Dispose(); }
}

public sealed record MixerDestinationMemberStatus(string Name, string Status);
public sealed record MixerDestinationStatus(string Key, string Name, StreamState State, string Status,
    int RequestedLatencyMs, int EffectiveLatencyMs, Scene Scene, int CaptureExtraMs)
{
    public IReadOnlyList<MixerDestinationMemberStatus> Members { get; init; } = [];
    public MixerLeaseConflict? Conflict { get; init; }
    public MixerRoutingError Error { get; init; }
    public bool Pending => Conflict != null || Error != MixerRoutingError.None;
}
public sealed record MixerSwitchPreview(string Revision, int Overrides);
public enum MixerEditResult { Applied, Stale, InvalidConfig, InvalidDestination, Conflict, SaveFailed }
public sealed record MixerEditOutcome(MixerEditResult Result, MixerLeaseConflict? Conflict = null, MixerRoutingError Error = MixerRoutingError.None);

/// <summary>
/// Owns sessions outside their audio loops. A single homogeneous destination uses the legacy capture factory;
/// multiple destinations share one capture host and each owns its own FIFO, effects and protocol timeline.
/// Mutation is serialized, while network preparation and sending remain on each controller's own loop.
/// </summary>
public sealed class MixerOutputCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly object _viewGate = new();
    private readonly Dictionary<string, Active> _active = [];
    private IReadOnlyList<MixerDestinationStatus> _pending = [];
    private readonly Func<ResolvedMixerDestination, Func<AudioFifo, ICaptureSource>, IMixerDestinationSession> _session;
    private readonly Func<ResolvedMixerDestination, AudioFifo, ICaptureSource> _legacyCapture;
    private readonly Func<IMixerCaptureHost> _hostFactory;
    private readonly Func<IReadOnlyList<AirPlayDevice>> _scan;
    private readonly Func<string?> _nativeOutput;
    private volatile IReadOnlySet<string> _nativeCopyKeys = new HashSet<string>();
    private volatile IReadOnlyDictionary<string, IReadOnlySet<string>> _nativeDuplicateGuards = new Dictionary<string, IReadOnlySet<string>>();
    private IMixerCaptureHost? _host;
    private AppConfig? _config;
    private MixerRoutingSettings? _routing;
    private IReadOnlyList<RouteTarget> _roots = [];
    private bool _connected, _disposed, _hubMode;
    private string? _selected;
    private readonly HashSet<string> _stopped = [];
    private string? _calibrationKey;
    private sealed record Active(ResolvedMixerDestination Destination, string Signature, IMixerDestinationSession Session, IDisposable Lease);
    public MixerDeviceLeaseTable Leases { get; }
    public event Action? Changed;

    public MixerOutputCoordinator(MixerDeviceLeaseTable leases,
        Func<ResolvedMixerDestination, Func<AudioFifo, ICaptureSource>, IMixerDestinationSession> session,
        Func<ResolvedMixerDestination, AudioFifo, ICaptureSource> legacyCapture, Func<IMixerCaptureHost> host,
        Func<IReadOnlyList<AirPlayDevice>>? scan = null, Func<string?>? nativeOutput = null)
    { Leases = leases; _session = session; _legacyCapture = legacyCapture; _hostFactory = host; _scan = scan ?? (() => []); _nativeOutput = nativeOutput ?? (() => null); }

    public StreamController? SelectedController
    { get { lock (_viewGate) return _selected != null ? _active.GetValueOrDefault(_selected)?.Session.Controller : null; } }
    public AppConfig? SelectedConfig
    { get { lock (_viewGate) return _selected != null ? _active.GetValueOrDefault(_selected)?.Destination.Config : null; } }
    public bool IsConnected => _connected;
    public string? SelectedKey { get { lock (_viewGate) return _selected; } }
    public string? KeyForController(StreamController controller)
    { lock (_viewGate) return _active.FirstOrDefault(p => ReferenceEquals(p.Value.Session.Controller, controller)).Key; }
    public StreamController? ControllerForKey(string key)
    { lock (_viewGate) return _active.GetValueOrDefault(key)?.Session.Controller; }
    public ResolvedMixerDestination? DestinationForKey(string key)
    { lock (_viewGate) return _active.GetValueOrDefault(key)?.Destination; }
    public string? OwnerKeyForProcess(uint pid) => _host?.OwnerKeyFor(pid) ?? (SelectedController?.Capture as RoutedCapture)?.OwnerKeyFor(pid);
    public bool UsesHub => _hubMode;
    public bool HasMultipleDestinations { get { lock (_viewGate) return _active.Count > 1; } }
    public IReadOnlyList<MixerDestinationStatus> Statuses
    {
        get
        {
            lock (_viewGate) return _active.Values.Select(a => new MixerDestinationStatus(a.Destination.Key, a.Destination.Name,
                a.Session.State, a.Session.Status, RequestedLatency(a), a.Session.EffectiveLatencyMs,
                a.Destination.Config!.Scene, a.Session.Controller?.Capture?.ExtraLatencyMs ?? (_hubMode ? RoutedCapture.RoutedExtraLatencyMs : 0))
                {
                    Members = a.Session.Controller?.GroupOutputStatuses.Select(s => new MixerDestinationMemberStatus(s.Name,
                        (s.State == OutputState.Streaming ? L.T("已连接") : s.NeedsUserAction ? L.T("等待操作") :
                            s.Retrying ? L.T("等待重连") : s.State == OutputState.Preparing ? L.T("正在连接…") : L.T("未连接")) +
                            (string.IsNullOrWhiteSpace(s.Error) ? "" : " · " + s.Error))).ToArray() ?? [],
                }).Concat(_pending.Where(p => !_active.ContainsKey(p.Key))).ToArray();
        }
    }

    private static int RequestedLatency(Active active)
    {
        var config = active.Destination.Config!;
        if (GroupPlan.FromConfig(config) == null && config.DeviceId is { } id && active.Session.Controller is { } controller)
            return ReceiverLatencySelection.Create(controller.ReceiverLookup?.Invoke(id), controller.ReceiverSettings?.Invoke(id), controller.ActiveProtocol)?.Value ?? config.LatencyMs;
        return config.LatencyMs;
    }

    public void Select(string key)
    { lock (_viewGate) if (_active.ContainsKey(key)) _selected = key; Changed?.Invoke(); }

    public static bool NeedsHub(AppConfig config, MixerRoutingSettings routing)
    {
        var remote = new[] { routing.Default }.Concat(routing.Apps.Values).Where(r => r.Output.Kind != MixerOutputKind.Local)
            .Select(r => MixerDestinationResolver.Resolve(config, r).Key).Distinct().ToArray();
        return remote.Length > 1 || routing.Default.Output.Kind == MixerOutputKind.Local && remote.Length > 0;
    }

    public async Task<MixerEditOutcome> ConnectAsync(AppConfig config, ConnectTrigger trigger, string? takeOverKey = null)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            config = Snapshot(config);
            var read = MixerRoutes.Read(config);
            if (read.Settings == null) return new(MixerEditResult.InvalidConfig);
            var desired = Desired(config, read.Settings, _roots);
            if (desired.FirstOrDefault(d => !d.Available) is { } invalid) { RecordPending(desired); return new(MixerEditResult.InvalidDestination, Error: invalid.Error); }
            var conflict = Validate(desired, config);
            if (conflict != null) { RecordPending(desired, conflict); return new(MixerEditResult.Conflict, conflict); }
            _connected = true;
            if (trigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver) _stopped.Clear();
            await Reconcile(config, read.Settings, trigger, takeOverKey: takeOverKey).ConfigureAwait(false);
            return new(MixerEditResult.Applied);
        }
        catch (MixerLeaseException ex) { return new(MixerEditResult.Conflict, ex.Conflict); }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    public async Task RefreshRootsAsync(IReadOnlyList<RouteTarget> roots)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            _roots = roots;
            if (_connected && !_disposed && _config != null && _routing != null)
            {
                try { await Reconcile(_config, _routing, ConnectTrigger.Automatic).ConfigureAwait(false); }
                catch (MixerLeaseException ex) { RecordPending(Desired(_config, _routing, _roots), ex.Conflict); }
            }
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    private static AppConfig Snapshot(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

    private ResolvedMixerDestination Resolve(AppConfig config, MixerRoute route)
    {
        var destination = MixerDestinationResolver.Resolve(config, route, _scan());
        if (route.AlsoLocal && destination.Config is { } projection && _nativeOutput() is { } native &&
            LocalGroupSettings.Selected(projection).Any(o => SameLocalIdentity(o.DeviceId, native, config.MixerDeviceIdentities)))
            return destination with { Config = null, Error = MixerRoutingError.DuplicateLocal };
        return destination;
    }

    private static bool SameLocalIdentity(string a, string b, IReadOnlyDictionary<string, string> identities)
    {
        if (CaptureEndpoint.Same(a, b)) return true;
        string? Identity(string id) => identities.FirstOrDefault(p => MixerDeviceIdentities.Canonical(p.Key) == MixerDeviceIdentities.Canonical("local:" + id)).Value;
        return Identity(a) is { } physical && string.Equals(physical, Identity(b), StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshNativeDuplicateGuards()
    {
        var next = new Dictionary<string, IReadOnlySet<string>>();
        string? native = _nativeOutput();
        lock (_viewGate)
            foreach (var active in _active.Values)
            {
                var config = active.Destination.Config!;
                var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (native != null && _nativeCopyKeys.Contains(active.Destination.Key))
                    foreach (var output in LocalGroupSettings.Selected(config))
                        if (SameLocalIdentity(output.DeviceId, native, _config?.MixerDeviceIdentities ?? config.MixerDeviceIdentities)) blocked.Add(output.DeviceId);
                next.Add(active.Destination.Key, blocked);
            }
        _nativeDuplicateGuards = next;
    }

    private List<ResolvedMixerDestination> Desired(AppConfig config, MixerRoutingSettings routing, IReadOnlyList<RouteTarget> roots) =>
        new[] { routing.Default }.Concat(roots.Select(t => routing.For(t.Key)))
            .Where(r => r.Output.Kind != MixerOutputKind.Local).Select(r => Resolve(config, r))
            .Where(d => d.Error != MixerRoutingError.NoSelection)
            .Concat(_calibrationKey != null && _active.TryGetValue(_calibrationKey, out var measuring) ? new[] { measuring.Destination } : [])
            .DistinctBy(d => d.Key).ToList();

    private MixerLeaseConflict? Validate(IReadOnlyList<ResolvedMixerDestination> desired, AppConfig config)
    {
        if (_calibrationKey != null && _active.TryGetValue(_calibrationKey, out var measuring) &&
            (desired.FirstOrDefault(d => d.Key == _calibrationKey) is not { } same ||
                Signature(same, NeedsHub(config, MixerRoutes.Read(config).Settings!)) != measuring.Signature))
            return new(MixerLeaseError.Occupied, _calibrationKey, measuring.Destination.Name);
        var outputs = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var destination in desired)
        {
            var members = MixerDeviceIdentities.Outputs(destination, _scan());
            if (members == null) return new(MixerLeaseError.UnresolvedPair, destination.Key, destination.Name);
            outputs.Add(destination.Key, members);
        }
        HashSet<string> replacing;
        lock (_viewGate) replacing = _active.Keys.ToHashSet();
        return Leases.Validate(outputs, config.MixerDeviceIdentities, replacing);
    }

    private Dictionary<string, IDisposable> Reserve(IReadOnlyList<ResolvedMixerDestination> desired, AppConfig config)
    {
        HashSet<string> held; lock (_viewGate) held = _active.Keys.ToHashSet();
        return Leases.Reserve(desired.ToDictionary(d => d.Key,
            d => MixerDeviceIdentities.Outputs(d, _scan()) ?? throw new MixerLeaseException(new(MixerLeaseError.UnresolvedPair, d.Key, d.Name))), held, config.MixerDeviceIdentities);
    }

    private void RecordPending(IReadOnlyList<ResolvedMixerDestination> desired, MixerLeaseConflict? conflict = null)
    {
        lock (_viewGate) _pending = desired.Where(d => !_active.ContainsKey(d.Key) && !_stopped.Contains(d.Key)).Select(d =>
            new MixerDestinationStatus(d.Key, d.Name, StreamState.Idle, L.T("等待操作"), d.Config?.LatencyMs ?? 0, 0,
                d.Config?.Scene ?? Scene.Custom, 0) { Conflict = conflict, Error = d.Error }).ToArray();
    }

    private async Task Reconcile(AppConfig config, MixerRoutingSettings routing, ConnectTrigger trigger,
        Dictionary<string, IDisposable>? reserved = null, string? takeOverKey = null)
    {
        var desired = Desired(config, routing, _roots).Where(d => !_stopped.Contains(d.Key)).ToList();
        if (desired.Any(d => !d.Available)) { RecordPending(desired); return; }
        reserved ??= Reserve(desired, config);
        lock (_viewGate) _pending = [];
        bool hub = NeedsHub(config, routing);
        // Retarget capture roots before releasing the old capture lease. Other buses keep their queued samples.
        if (hub) _host?.Update(config, CaptureRouting(routing));
        var signatures = desired.ToDictionary(d => d.Key, d => Signature(d, hub));
        Active[] remove;
        lock (_viewGate) remove = _active.Values.Where(a => !signatures.TryGetValue(a.Destination.Key, out var signature) || a.Signature != signature).ToArray();
        var carried = remove.Where(a => signatures.ContainsKey(a.Destination.Key)).ToDictionary(a => a.Destination.Key, a => a.Lease);
        foreach (var active in remove) await Stop(active, release: !carried.ContainsKey(active.Destination.Key)).ConfigureAwait(false);
        if (_host != null && !hub)
        { _host.RootsChanged -= OnRootsChanged; _host.Dispose(); _host = null; }
        _config = config; _routing = routing; _hubMode = hub;
        _nativeCopyKeys = new[] { routing.Default }.Concat(_roots.Select(root => routing.For(root.Key))).Where(r => r.AlsoLocal)
            .Select(r => MixerDestinationResolver.Resolve(config, r, _scan()).Key).ToHashSet();
        RefreshNativeDuplicateGuards();
        if (hub && _host == null)
        { _host = _hostFactory(); _host.RootsChanged += OnRootsChanged; _host.Update(config, CaptureRouting(routing)); _host.Start(); }
        _host?.Update(config, CaptureRouting(routing));
        foreach (var destination in desired)
        {
            var startTrigger = trigger == ConnectTrigger.TakeOver && destination.Key != takeOverKey ? ConnectTrigger.Manual : trigger;
            Active? existing;
            lock (_viewGate) existing = _active.GetValueOrDefault(destination.Key);
            if (existing != null)
            {
                if (existing.Session.WaitingForDiscovery || startTrigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver && existing.Session.State is StreamState.Idle or StreamState.Retrying)
                {
                    await existing.Session.StopAsync().ConfigureAwait(false);
                    existing.Session.Start(startTrigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver ? startTrigger : ConnectTrigger.Automatic);
                }
                continue;
            }
            IDisposable lease;
            if (carried.Remove(destination.Key, out var previousLease)) lease = previousLease;
            else { lease = reserved[destination.Key]; reserved.Remove(destination.Key); }
            try
            {
                Func<AudioFifo, ICaptureSource> capture = hub
                    ? fifo => _host!.Subscribe(destination.Key, fifo)
                    : fifo => _legacyCapture(destination, fifo);
                var session = _session(destination, capture);
                if (session.Controller is { } controller)
                {
                    controller.MixerLocalDuplicate = id => _nativeDuplicateGuards.TryGetValue(destination.Key, out var blocked) && blocked.Contains(id);
                    controller.CheckMixerGroupLease = (plan, members) => Leases.Refresh(destination.Key,
                        members.Select(id => "airplay:" + id).Concat(plan.PairTsid == null ? [] : new[] { "pair:" + plan.PairTsid })
                            .Concat(plan.LocalOutputs.Select(o => "local:" + o.DeviceId))
                            .Concat(plan.DlnaOutputs.Select(o => "dlna:" + o.Udn)).ToArray(), _config?.MixerDeviceIdentities ?? config.MixerDeviceIdentities);
                }
                var active = new Active(destination, signatures[destination.Key], session, lease);
                session.Changed += OnSessionChanged;
                lock (_viewGate) _active.Add(destination.Key, active);
                RefreshNativeDuplicateGuards();
                // An automatic route is not authorization to take somebody else's speaker.
                session.Start(startTrigger);
            }
            catch { lease.Dispose(); throw; }
        }
        foreach (var unused in reserved.Values) unused.Dispose();
        lock (_viewGate)
        {
            var defaultKey = MixerDestinationResolver.Resolve(config, routing.Default, _scan()).Key;
            if (_selected == null || !_active.ContainsKey(_selected))
                _selected = _active.ContainsKey(defaultKey) ? defaultKey : _active.Keys.FirstOrDefault();
        }
    }

    private static string Signature(ResolvedMixerDestination d, bool hub) =>
        JsonSerializer.Serialize(OutputGroups.Capture(d.Config!)) + $"|{d.Config!.Scene}|{d.Config.LatencyMs}|{d.Config.FifoTargetMs}|{hub}";

    private void OnRootsChanged()
    {
        if (_host is not { } host) return;
        var roots = host.Roots;
        _ = Task.Run(() => RefreshRootsAsync(roots));
    }
    private void OnSessionChanged() => Changed?.Invoke();

    private async Task Stop(Active active, string? reason = null, bool release = true)
    {
        await active.Session.StopAsync(reason).ConfigureAwait(false);
        active.Session.Changed -= OnSessionChanged;
        active.Session.Dispose();
        if (release) active.Lease.Dispose();
        lock (_viewGate) _active.Remove(active.Destination.Key);
    }

    public async Task DisconnectAsync(string? key = null, string? reason = null)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Active[] stop;
            lock (_viewGate) stop = _active.Values.Where(a => key == null || a.Destination.Key == key).ToArray();
            if (key == null) _connected = false; else _stopped.Add(key);
            lock (_viewGate) _pending = key == null ? [] : _pending.Where(p => p.Key != key).ToArray();
            foreach (var active in stop) await Stop(active, reason).ConfigureAwait(false);
            lock (_viewGate) if (_selected != null && !_active.ContainsKey(_selected)) _selected = _active.Keys.FirstOrDefault();
            if (key == null && _host != null)
            { _host.RootsChanged -= OnRootsChanged; _host.Dispose(); _host = null; _roots = []; }
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    public MixerSwitchPreview PreviewSwitch(AppConfig config) => new(JsonSerializer.Serialize(config.MixerRouting),
        MixerRoutes.Read(config).Settings?.Apps.Count ?? 0);

    public async Task<MixerEditOutcome> EditAsync(AppConfig config, MixerRoutingSettings next, Action save,
        MixerSwitchPreview? expected = null, bool resumeDefault = false)
    {
        // Keep the caller's UI context for the in-memory mutation and its single save. Teardown itself runs off UI.
        await _changes.WaitAsync();
        try
        {
            if (!MixerRoutes.Read(config).Editable) return new(MixerEditResult.InvalidConfig);
            if (expected != null && expected != PreviewSwitch(config)) return new(MixerEditResult.Stale);
            var projection = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
            try { MixerRoutes.Write(projection, next); } catch (ArgumentException) { return new(MixerEditResult.InvalidConfig); }
            var desired = Desired(projection, next, _roots);
            if (desired.FirstOrDefault(d => !d.Available) is { } invalid) return new(MixerEditResult.InvalidDestination, Error: invalid.Error);
            var conflict = Validate(desired, projection);
            if (conflict != null) return new(MixerEditResult.Conflict, conflict);
            Dictionary<string, IDisposable>? reserved = null;
            if (_connected)
            {
                try { reserved = Reserve(desired, projection); }
                catch (MixerLeaseException ex) { return new(MixerEditResult.Conflict, ex.Conflict); }
            }
            var old = config.MixerRouting; var oldApps = config.AppRoutes; var oldDefault = config.RouteDefault;
            MixerRoutes.Write(config, next);
            try { save(); }
            catch
            {
                config.MixerRouting = old; config.AppRoutes = oldApps; config.RouteDefault = oldDefault;
                if (reserved != null) foreach (var lease in reserved.Values) lease.Dispose();
                return new(MixerEditResult.SaveFailed);
            }
            var previousRouting = MixerRoutes.Read(new AppConfig { MixerRouting = old }).Settings;
            if (previousRouting != null)
            {
                var chosen = new List<MixerRoute>();
                if (previousRouting.Default != next.Default) chosen.Add(next.Default);
                chosen.AddRange(next.Apps.Where(p => !previousRouting.Apps.TryGetValue(p.Key, out var previous) || previous != p.Value).Select(p => p.Value));
                foreach (var route in chosen) _stopped.Remove(MixerDestinationResolver.Resolve(config, route, _scan()).Key);
            }
            if (resumeDefault) _stopped.Remove(MixerDestinationResolver.Resolve(config, next.Default, _scan()).Key);
            if (_connected) await Reconcile(Snapshot(config), next, ConnectTrigger.Automatic, reserved).ConfigureAwait(false);
            return new(MixerEditResult.Applied);
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    public Task<MixerEditOutcome> SwitchAllAsync(AppConfig config, MixerRoute route, MixerSwitchPreview expected, Action save)
    {
        var settings = MixerRoutes.Read(config).Settings;
        return settings == null ? Task.FromResult(new MixerEditOutcome(MixerEditResult.InvalidConfig))
            : EditAsync(config, settings with { Default = route, Apps = [] }, save, expected, resumeDefault: true);
    }

    public ResolvedMixerDestination? DestinationForApp(string? app, uint? pid = null)
    {
        if (_config == null || _routing == null) return null;
        if (pid is { } process)
            app = _host?.OwnerKeyFor(process) ?? (SelectedController?.Capture as RoutedCapture)?.OwnerKeyFor(process) ?? app;
        if (string.IsNullOrWhiteSpace(app))
        {
            var routes = new[] { _routing.Default }.Concat(_routing.Apps.Values)
                .Select(r => Resolve(_config, r)).DistinctBy(d => d.Key).ToArray();
            return routes.Length == 1 ? routes[0] : null;
        }
        return Resolve(_config, _routing.For(app));
    }

    public StreamController? ControllerForApp(string? app, uint? pid = null)
    {
        var destination = DestinationForApp(app, pid);
        lock (_viewGate) return destination == null ? null : _active.GetValueOrDefault(destination.Key)?.Session.Controller;
    }

    public IReadOnlyList<StreamController> Controllers
    { get { lock (_viewGate) return _active.Values.Select(a => a.Session.Controller).OfType<StreamController>().ToArray(); } }

    public IReadOnlyList<string> LocalOutputIds
    { get { lock (_viewGate) return _active.Values.SelectMany(a => GroupPlan.FromConfig(a.Destination.Config!)?.LocalOutputs.Select(o => o.DeviceId) ?? []).Distinct().ToArray(); } }

    public MixerEditOutcome ValidateConfiguration(AppConfig config)
    {
        var settings = MixerRoutes.Read(config).Settings;
        if (settings == null) return new(MixerEditResult.InvalidConfig);
        var desired = Desired(config, settings, _roots);
        if (desired.FirstOrDefault(d => !d.Available) is { } invalid) return new(MixerEditResult.InvalidDestination, Error: invalid.Error);
        var conflict = Validate(desired, config);
        return conflict == null ? new(MixerEditResult.Applied) : new(MixerEditResult.Conflict, conflict);
    }

    public MixerEditOutcome PreviewRoute(AppConfig config, string? appKey, MixerRoute route, bool all = false)
    {
        var settings = MixerRoutes.Read(config).Settings;
        if (settings == null) return new(MixerEditResult.InvalidConfig);
        settings = settings with { Apps = new(settings.Apps) };
        if (all) settings = settings with { Default = route, Apps = [] };
        else if (appKey == null) settings = settings with { Default = route };
        else settings.Apps[RouteRules.KeyFor(appKey)] = route;
        var projection = Snapshot(config); MixerRoutes.Write(projection, settings);
        var roots = appKey != null && !_roots.Any(r => r.Key == RouteRules.KeyFor(appKey))
            ? _roots.Concat(new[] { new RouteTarget(uint.MaxValue, RouteRules.KeyFor(appKey), false) }).ToArray() : _roots;
        var desired = Desired(projection, settings, roots);
        if (desired.FirstOrDefault(d => !d.Available) is { } invalid) return new(MixerEditResult.InvalidDestination, Error: invalid.Error);
        var conflict = Validate(desired, projection);
        return conflict == null ? new(MixerEditResult.Applied) : new(MixerEditResult.Conflict, conflict);
    }

    public async Task<MixerEditOutcome> UpdateIdentitiesAsync(AppConfig source, Dictionary<string, string> identities, Action save)
    {
        await _changes.WaitAsync();
        try
        {
            var projection = Snapshot(_config ?? source); projection.MixerDeviceIdentities = new(identities);
            var settings = MixerRoutes.Read(projection).Settings;
            if (settings == null) return new(MixerEditResult.InvalidConfig);
            var desired = Desired(projection, settings, _roots);
            var conflict = Validate(desired, projection);
            if (conflict != null) return new(MixerEditResult.Conflict, conflict);
            var previous = source.MixerDeviceIdentities; source.MixerDeviceIdentities = new(identities);
            try { save(); } catch { source.MixerDeviceIdentities = previous; return new(MixerEditResult.SaveFailed); }
            if (_config != null) _config.MixerDeviceIdentities = new(identities);
            Active[] active; lock (_viewGate) active = _active.Values.ToArray();
            foreach (var output in active)
                Leases.Refresh(output.Destination.Key, MixerDeviceIdentities.Outputs(output.Destination, _scan())!, identities);
            RefreshNativeDuplicateGuards();
            return new(MixerEditResult.Applied);
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    public async Task ReconnectAsync(string key, ConnectTrigger trigger)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Active? active; lock (_viewGate) active = _active.GetValueOrDefault(key);
            if (active != null && _config != null)
            {
                var members = MixerDeviceIdentities.Outputs(active.Destination, _scan());
                if (members != null) Leases.Refresh(key, members, _config.MixerDeviceIdentities);
                await active.Session.StopAsync().ConfigureAwait(false);
                active.Session.Start(trigger);
            }
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    public async Task RefreshDiscoveryAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try { if (_connected && _config != null && _routing != null) await Reconcile(_config, _routing, ConnectTrigger.Reconnect).ConfigureAwait(false); }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    public async Task<IDisposable> AcquireExternalAsync(string owner, IReadOnlyList<string> outputs, bool replaceSelected = false)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Active? selected;
            lock (_viewGate) selected = replaceSelected && _selected != null ? _active.GetValueOrDefault(_selected) : null;
            if (selected?.Destination.Key == _calibrationKey && _calibrationKey != null)
                throw new MixerLeaseException(new(MixerLeaseError.Occupied, _calibrationKey, selected!.Destination.Name));
            var replacing = selected == null ? null : new HashSet<string> { selected.Destination.Key };
            var desired = new Dictionary<string, IReadOnlyList<string>> { [owner] = outputs };
            var identities = _config?.MixerDeviceIdentities ?? new Dictionary<string, string>();
            var conflict = Leases.Validate(desired, identities, replacing);
            if (conflict != null) throw new MixerLeaseException(conflict);
            if (selected != null)
            { _stopped.Add(selected.Destination.Key); await Stop(selected).ConfigureAwait(false); }
            return Leases.Acquire(owner, outputs, identities);
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    private MixerRoutingSettings CaptureRouting(MixerRoutingSettings routing)
    {
        if (_calibrationKey == null || _config == null) return routing;
        var route = new[] { routing.Default }.Concat(routing.Apps.Values)
            .FirstOrDefault(r => MixerDestinationResolver.Resolve(_config, r, _scan()).Key == _calibrationKey);
        if (route == null) return routing;
        var apps = new Dictionary<string, MixerRoute>(routing.Apps)
        { [RouteRules.KeyFor(System.Diagnostics.Process.GetCurrentProcess().ProcessName)] = route };
        return routing with { Apps = apps };
    }

    public IDisposable BeginCalibrationRoute(string? key)
    {
        _changes.Wait();
        try
        {
            if (key == null || !_active.ContainsKey(key)) return new Scope(() => { });
            if (_calibrationKey != null) throw new InvalidOperationException("A destination is already being calibrated");
            _calibrationKey = key;
            if (_config != null && _routing != null) _host?.Update(_config, CaptureRouting(_routing));
            return new Scope(() =>
            {
                _changes.Wait();
                try
                {
                    _calibrationKey = null;
                    if (_config != null && _routing != null) _host?.Update(_config, _routing);
                }
                finally { _changes.Release(); }
            });
        }
        finally { _changes.Release(); }
    }

    private sealed class Scope(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }

    public async ValueTask DisposeAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; }
        finally { _changes.Release(); }
        await DisconnectAsync().ConfigureAwait(false);
    }
}

public interface IMixerCaptureHost : IDisposable
{
    string? OwnerKeyFor(uint pid) => null;
    IReadOnlyList<RouteTarget> Roots { get; }
    event Action? RootsChanged;
    void Start();
    void Update(AppConfig config, MixerRoutingSettings routing);
    ICaptureSource Subscribe(string key, AudioFifo fifo);
}
