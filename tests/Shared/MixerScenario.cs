using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>The real coordinator, controller loops, RTP, local sink and DLNA stack, with exclusively owned fakes.</summary>
internal sealed class MixerScenario : IAsyncDisposable
{
    internal const string GameId = "02:50:36:00:00:01", MusicId = "02:50:36:00:00:02", QuietId = "02:50:36:00:00:03";
    internal const string OfflineId = "02:50:36:00:00:04";
    internal const string LocalId = "fake-render-output", CaptureId = "fake-capture-output", NativeId = "fake-native-default";
    internal const string MusicGroup = "ci-music", QuietGroup = "ci-quiet", OverlapGroup = "ci-overlap";
    internal const string MusicName = "CI music group: AirPlay, local and DLNA";
    internal const string QuietName = "CI separate room with a deliberately long saved destination name";
    private readonly Dictionary<string, FakeAirPlayReceiver> _airplay = [];
    private readonly FakeDlnaRenderer _dlna = new();
    private readonly ConcurrentBag<Pull> _pulls = [];
    private readonly ConcurrentDictionary<string, StreamController> _controllers = new();
    private readonly ConcurrentDictionary<string, long> _checked = new();
    private readonly ConcurrentDictionary<string, long> _nonzero = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _audit;
    private volatile MixerRoutingSettings _routing = new();
    private volatile bool _settling;
    private long _settleUntil;
    internal AppConfig Config { get; }
    internal MixerOutputCoordinator Coordinator { get; }
    internal SharedHost? Host { get; private set; }
    internal Func<IReadOnlyDictionary<string, int>> LiveLevels { get; set; }
    internal string? NativeOutputId { get; set; } = NativeId;
    internal int Saves, HubFactories;
    internal readonly ConcurrentQueue<string> OpenedRenderIds = new();
    internal IReadOnlyList<string> Errors => _errors.ToArray();
    internal IReadOnlyDictionary<string, long> Checked => _checked;
    internal IReadOnlyDictionary<string, long> Nonzero => _nonzero;
    internal IReadOnlyList<AirPlayDevice> Scan => _airplay.Select(p => new AirPlayDevice(p.Value.Name, p.Key, IPAddress.Loopback,
        p.Value.Port, "AudioAccessory6,1", new Dictionary<string, string> { ["model"] = "AudioAccessory6,1" })).ToArray();
    internal string GameKey => MixerRoutes.SpeakerKey("airplay:" + GameId);

    internal MixerScenario()
    {
        foreach (var (id, name) in new[] { (GameId, "CI game HomePod"), (MusicId, "CI music HomePod"), (QuietId, "CI separate HomePod") })
            _airplay[id] = new(false) { DeviceId = id, Name = name, ArrivalToRenderMs = 85 };
        Config = new AppConfig { DeviceId = GameId, DeviceName = "CI game HomePod", Host = "127.0.0.1", AutoConnect = false,
            Scene = Scene.Game, LatencyMs = 105, Volume = 10, VolumeCapPercent = 25, CaptureDeviceId = CaptureId,
            GroupSpeakers = _airplay.Select(p => new GroupSpeakerSettings { DeviceId = p.Key, Name = p.Value.Name, Host = "127.0.0.1" }).ToList(),
            GroupLocalOutputs = [new(LocalId, "CI fake local output") { Required = false }],
            GroupDlnaOutputs = [new("uuid:fake-renderer", "CI fake DLNA") { Required = false, EstimatedLatencyMs = 2000, PreferredFormat = "l16" }],
            AppSendVolumes = new() { ["game"] = 32 }, RoutingNoticeAccepted = false };
        Config.GroupSpeakers.Add(new() { DeviceId = OfflineId, Name = "CI saved offline HomePod" });
        OutputGroups.Save(Config, MusicName, new OutputGroupSettings { DeviceId = MusicId, DeviceName = "CI music HomePod",
            GroupCaptureDeviceIds = [MusicId], GroupLocalOutputs = Config.GroupLocalOutputs, GroupCaptureLocalDeviceIds = [LocalId],
            GroupDlnaOutputs = Config.GroupDlnaOutputs, GroupCaptureDlnaUdns = ["uuid:fake-renderer"], GroupDelayMs = 2000 }, MusicGroup);
        OutputGroups.Save(Config, QuietName, new OutputGroupSettings { DeviceId = QuietId, DeviceName = "CI separate HomePod",
            GroupCaptureDeviceIds = [QuietId], GroupDelayMs = 500 }, QuietGroup);
        OutputGroups.Save(Config, "CI overlapping group", new OutputGroupSettings { DeviceId = GameId,
            GroupCaptureDeviceIds = [GameId, MusicId], GroupDelayMs = 2000 }, OverlapGroup);
        foreach (string id in new[] { "airplay:" + GameId, "airplay:" + MusicId, "airplay:" + QuietId, "local:" + LocalId, "dlna:uuid:fake-renderer" })
            Config.MixerDeviceIdentities[MixerDeviceIdentities.Canonical(id)] = Guid.NewGuid().ToString("N");
        MixerRoutes.Write(Config, new() { Default = MixerRoute.Current, Apps = new()
            { ["music"] = Group(MusicGroup), ["voice"] = MixerRoute.Local, ["inactive"] = Group(QuietGroup) },
            DestinationSettings = new() { ["group:" + MusicGroup] = new(Scene.Custom, 2000), ["group:" + QuietGroup] = new(Scene.Custom, 500) } });
        LiveLevels = () => Config.AppSendVolumes;
        NotifyRoutingChanged();
        _dlna.OnPlayAsync = async (uri, ct) =>
        {
            var pull = new Pull(samples => Check("dlna", "group:" + MusicGroup, samples)); _pulls.Add(pull);
            await pull.Start(uri, ct).ConfigureAwait(false);
        };
        Coordinator = new(new(), Session, (d, fifo) => new LegacyCapture(this, d.Key, fifo),
            () => { HubFactories++; return Host = new SharedHost(this); }, () => Scan, () => NativeOutputId);
        _audit = Task.Run(AuditRtp);
    }

    internal static MixerRoute Group(string id) => new(new MixerOutput(MixerOutputKind.Group, id));
    internal void NotifyRoutingChanged()
    {
        _routing = MixerRoutes.Read(Config).Settings!;
        Interlocked.Exchange(ref _settleUntil, MediaClock.Now + MediaClock.FromMs(2600));
        _settling = true;
    }
    internal void Save(string path) { Config.Save(path); Saves++; NotifyRoutingChanged(); }
    internal Task<MixerEditOutcome> Connect() => Coordinator.ConnectAsync(Config, ConnectTrigger.Manual);
    internal void LoseMusic() => _airplay[MusicId].EndSessions();
    internal StreamController? Controller(string key) => _controllers.GetValueOrDefault(key);

    private IMixerDestinationSession Session(ResolvedMixerDestination destination, Func<AudioFifo, ICaptureSource> capture)
    {
        var controller = new StreamController(destination.Config!.FifoTargetMs)
        {
            CaptureFactory = capture, ArrivalToRenderMs = 85, CapturedOutputId = () => CaptureId, TakeoverGrace = TimeSpan.Zero,
            AirPlayPort = destination.Config.DeviceId != null && _airplay.TryGetValue(destination.Config.DeviceId, out var primary) ? primary.Port : 7000,
            ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members.Select(m =>
                new StreamController.ResolvedMember(m.DeviceId, m.Name, IPAddress.Loopback, _airplay[m.DeviceId].Port)).ToList()),
            OpenLocalOutput = id =>
            {
                OpenedRenderIds.Enqueue(id);
                return id == LocalId ? new Render(samples => Check("local", destination.Key, samples))
                    : throw new InvalidOperationException("A non-fixture render endpoint was requested");
            },
            ResolveGroupDlnaOutput = (_, _) => Task.FromResult(new DlnaConnectionTarget(_dlna.Device, IPAddress.Loopback, () => true)),
            ConnectGroupDlnaOutput = (_, target, options, ct) => DlnaClient.ConnectAsync(target.Device,
                options with { PollInterval = TimeSpan.FromMilliseconds(50), PullTimeout = TimeSpan.FromSeconds(10) }, ct),
        };
        controller.SetVolumeCap(25);
        controller.SetVolume(10);
        _controllers[destination.Key] = controller;
        return new Owned(new MixerStreamSession(destination, controller,
            () => destination.Config.DeviceId != OfflineId), () => _controllers.TryRemove(destination.Key, out _));
    }

    private async Task AuditRtp()
    {
        var previousKeys = new Dictionary<string, byte[]>();
        var currentKeys = new Dictionary<string, byte[]>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (var (id, receiver) in _airplay)
                {
                    if (receiver.StreamKey is not { } key) continue;
                    if (currentKeys.TryGetValue(id, out var last) && !last.SequenceEqual(key)) previousKeys[id] = last;
                    currentKeys[id] = key;
                    while (receiver.Rtp.Audio.TryDequeue(out var packet))
                    {
                        byte[] pcm;
                        try { pcm = FakeReceiver.Decrypt(packet.Bytes, key); }
                        catch (AuthenticationTagMismatchException) when (previousKeys.TryGetValue(id, out var previous))
                        { pcm = FakeReceiver.Decrypt(packet.Bytes, previous); }
                        var values = new float[pcm.Length / 2];
                        for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadInt16BigEndian(pcm.AsSpan(i * 2)) / 32767f;
                        var target = _controllers.FirstOrDefault(p => p.Value.Client != null && p.Value.Client.Sender?.Streams.Any(s => s.Remote.Equals(IPAddress.Loopback)) == true &&
                            DestinationContains(p.Key, id)).Key ?? (id == GameId ? GameKey : id == MusicId ? "group:" + MusicGroup : "group:" + QuietGroup);
                        Check("rtp:" + id, target, values);
                    }
                }
                await Task.Delay(10, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _errors.Enqueue(ex.ToString()); }
    }

    private bool DestinationContains(string key, string id)
    {
        var route = key.StartsWith("group:") ? Group(key[6..]) : new MixerRoute(new MixerOutput(MixerOutputKind.Speaker, "airplay:" + id));
        var destination = MixerDestinationResolver.Resolve(Config, route, Scan);
        return destination.Key == key && MixerDeviceIdentities.Outputs(destination, Scan)?.Contains(MixerDeviceIdentities.Canonical("airplay:" + id)) == true;
    }
    private void Check(string sink, string destination, ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / 2;
        _checked.AddOrUpdate(sink, frames, (_, count) => count + frames);
        long heard = 0;
        var expected = Sum(destination, _routing);
        bool steady = !_settling || MediaClock.Now >= Interlocked.Read(ref _settleUntil);
        if (steady) _settling = false;
        for (int i = 0; i + 1 < samples.Length; i += 2)
        {
            float left = samples[i], right = samples[i + 1];
            if (!float.IsFinite(left) || !float.IsFinite(right)) { _errors.Enqueue("non-finite PCM at " + sink); break; }
            if (Math.Abs(left) + Math.Abs(right) < .00001f) continue;
            heard++;
            // Global sink volume is a scalar; the two-channel marker ratio identifies the selected app mixture.
            if (steady && (expected.Left == 0 && expected.Right == 0 || Math.Abs(left * expected.Right - right * expected.Left) > .0015f))
            { _errors.Enqueue($"wrong destination PCM at {sink}: {left},{right} expected ratio {expected.Left},{expected.Right}"); break; }
        }
        if (heard > 0) _nonzero.AddOrUpdate(sink, heard, (_, count) => count + heard);
    }

    private (float Left, float Right) Sum(string key, MixerRoutingSettings routing)
    {
        float left = 0, right = 0;
        foreach (string app in new[] { "game", "music", "voice" })
        {
            var route = routing.For(app);
            if (route.Output.Kind == MixerOutputKind.Local || MixerDestinationResolver.Resolve(Config, route, Scan).Key != key) continue;
            float level = LiveLevels().TryGetValue(app, out int percent) ? percent / 100f : 1;
            var marker = Marker(app); left += marker.Left * level; right += marker.Right * level;
        }
        return (left, right);
    }
    private static (float Left, float Right) Marker(string app) => app switch
    { "game" => (.113f, .031f), "music" => (.017f, .227f), "voice" => (.071f, .089f), _ => (0, 0) };

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        _stop.Cancel(); await _audit.ConfigureAwait(false);
        foreach (var pull in _pulls) await pull.DisposeAsync().ConfigureAwait(false);
        await _dlna.DisposeAsync().ConfigureAwait(false);
        foreach (var receiver in _airplay.Values) receiver.Dispose();
        _stop.Dispose();
    }

    private sealed class Owned(IMixerDestinationSession session, Action end) : IMixerDestinationSession
    {
        public StreamController? Controller => session.Controller;
        public StreamState State => session.State;
        public string Status => session.Status;
        public int EffectiveLatencyMs => session.EffectiveLatencyMs;
        public event Action? Changed { add => session.Changed += value; remove => session.Changed -= value; }
        public void Start(ConnectTrigger trigger) => session.Start(trigger);
        public Task StopAsync(string? reason = null) => session.StopAsync(reason);
        public void Dispose() { session.Dispose(); end(); }
    }
    private delegate void Samples(ReadOnlySpan<float> values);
    private sealed class Render(Samples receive) : ILocalRenderDevice
    {
        private readonly Native.PreciseTimer _timer = new();
        private long _next = MediaClock.Now, _frames;
        private int _padding;
        public int Rate => 48000;
        public int BufferFrames => 1440;
        public int PeriodFrames => 480;
        public double StreamLatencyMs => 2;
        public int PaddingFrames => _padding;
        public RenderPosition Position => new(_frames, MediaClock.Now);
        public void Start() { }
        public void WaitReady()
        {
            long now = MediaClock.Now;
            if (now < _next) _timer.Sleep(_next - now);
            _next = MediaClock.Now + MediaClock.FromMs(10); _frames += 480; _padding = Math.Max(0, _padding - 480);
        }
        public void Write(ReadOnlySpan<float> stereo) { _padding += stereo.Length / 2; receive(stereo); }
        public void Dispose() => _timer.Dispose();
    }
    private sealed class Pull(Samples receive) : IAsyncDisposable
    {
        private readonly HttpClient _http = DlnaHttp.CreateClient(IPAddress.Loopback);
        private readonly CancellationTokenSource _stop = new();
        private HttpResponseMessage? _response; private Task? _read;
        internal async Task Start(Uri uri, CancellationToken ct)
        {
            if (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)) throw new InvalidOperationException("non-loopback fake pull");
            _response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            _response.EnsureSuccessStatusCode(); var stream = await _response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            _read = Task.Run(async () =>
            {
                var bytes = new byte[1408]; var samples = new float[704];
                try
                {
                    while (true)
                    {
                        await stream.ReadExactlyAsync(bytes, _stop.Token).ConfigureAwait(false);
                        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2)) / 32767f;
                        receive(samples);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _response?.Dispose(); _http.Dispose(); if (_read != null) await _read.ConfigureAwait(false); _stop.Dispose(); }
    }

    internal sealed class SharedHost : IMixerCaptureHost
    {
        private readonly MixerScenario _owner;
        private readonly MixerCaptureHub _hub;
        private readonly Thread _thread;
        private volatile MixerRoutingSettings _routing = new();
        private volatile IReadOnlyList<RouteTarget> _roots = [];
        private readonly Dictionary<uint, RouteTarget> _cycleTargets = [];
        private readonly ConcurrentDictionary<uint, int> _live = new(), _opens = new();
        private volatile bool _stop;
        private int _revision, _applied = -1;
        internal IReadOnlyDictionary<uint, int> Live => _live;
        internal IReadOnlyDictionary<uint, int> Opens => _opens;
        public IReadOnlyList<RouteTarget> Roots => _roots;
        public event Action? RootsChanged;
        internal SharedHost(MixerScenario owner)
        {
            _owner = owner;
            _hub = new(44100, target =>
            {
                int live = _live.AddOrUpdate(target.RootPid, 1, (_, value) => value + 1);
                if (live != 1) owner._errors.Enqueue("duplicate process capture " + target.RootPid);
                _opens.AddOrUpdate(target.RootPid, 1, (_, value) => value + 1);
                return new Input(target.RootPid, target.Key, _cycleTargets, () => _live.AddOrUpdate(target.RootPid, 0, (_, value) => value - 1));
            }, gain: (target, data) =>
            {
                float peak = 0; foreach (float value in data) peak = Math.Max(peak, Math.Abs(value));
                return RoutedCapture.ComposeGain(target.SendVolumePercent.HasValue ? owner.LiveLevels().GetValueOrDefault(target.Key, 100) : null,
                    target.Compensate, true, 1, peak);
            });
            _thread = new Thread(Run) { IsBackground = true, Name = "fixture shared process captures" };
        }
        public string? OwnerKeyFor(uint pid) => _roots.FirstOrDefault(r => r.RootPid == pid)?.Key;
        public void Update(AppConfig config, MixerRoutingSettings routing)
        {
            _routing = routing;
            _hub.Retarget(_roots.Select(t => t.Key).Distinct().ToDictionary(key => key,
                key => routing.For(key).Output.Kind == MixerOutputKind.Local ? null : MixerDestinationResolver.Resolve(config, routing.For(key), _owner.Scan).Key));
            var processes = new Dictionary<uint, ProcessEntry> { [101] = new(101, 0, "game"), [102] = new(102, 0, "music"), [103] = new(103, 0, "voice") };
            var plan = MixerCapturePlanner.Plan(Interlocked.Increment(ref _revision), routing, config,
                [new(101, false), new(102, false), new(103, false)], processes);
            var roots = plan.Roots.Select(r => r.Target).ToArray();
            bool changed = !_roots.SequenceEqual(roots); _roots = roots;
            if (changed) RootsChanged?.Invoke();
        }
        public ICaptureSource Subscribe(string key, AudioFifo fifo)
        { var capture = _hub.Subscribe(key, fifo); Interlocked.Increment(ref _revision); return new Proxy(capture, this); }
        public void Start() { _thread.Start(); RootsChanged?.Invoke(); }
        private void Run()
        {
            using var timer = new Native.PreciseTimer();
            using var mmcss = Native.EnterMmcss("Audio");
            long next = MediaClock.Now;
            try
            {
                while (!_stop)
                {
                    int revision = Volatile.Read(ref _revision);
                    if (revision != _applied)
                    {
                        _cycleTargets.Clear(); foreach (var root in _roots) _cycleTargets[root.RootPid] = root;
                        _hub.Apply(new(revision, _roots.Select(root => new MixerRoot(root,
                            MixerDestinationResolver.Resolve(_owner.Config, _routing.For(root.Key), _owner.Scan).Key, root.RootPid)).ToArray()));
                        _applied = revision;
                    }
                    long now = MediaClock.Now;
                    if (now < next) { timer.Sleep(next - now); continue; }
                    _hub.Cycle(); next += MediaClock.FromMs(10);
                    if (next < now - MediaClock.FromMs(30)) next = now;
                }
            }
            catch (Exception ex) { _owner._errors.Enqueue(ex.ToString()); }
            finally { _hub.Dispose(); }
        }
        public void Dispose() { _stop = true; if (_thread.IsAlive) _thread.Join(); }
        private sealed class Input(uint pid, string app, Dictionary<uint, RouteTarget> targets, Action end) : IMixerProcessInput
        {
            private readonly float[] _samples = new float[882];
            public bool Drain(List<float> samples)
            {
                var marker = Marker(app); float gain = targets[pid].Compensate ? SessionAttenuation.Epsilon : 1;
                for (int i = 0; i < _samples.Length; i += 2) { _samples[i] = marker.Left * gain; _samples[i + 1] = marker.Right * gain; }
                samples.AddRange(_samples); return true;
            }
            public void Dispose() => end();
        }
        private sealed class Proxy(ICaptureSource capture, SharedHost owner) : ICaptureSource
        {
            public string? DeviceName => capture.DeviceName;
            public string? EndpointId => CaptureId;
            public double DriftPpm => capture.DriftPpm;
            public float Peak => capture.Peak;
            public int ExtraLatencyMs => capture.ExtraLatencyMs;
            public event Action<string>? DeviceChanged { add => capture.DeviceChanged += value; remove => capture.DeviceChanged -= value; }
            public void Start() => capture.Start();
            public void Dispose() { capture.Dispose(); Interlocked.Increment(ref owner._revision); }
        }
    }
    private sealed class LegacyCapture(MixerScenario owner, string key, AudioFifo fifo) : ICaptureSource
    {
        private readonly ManualResetEventSlim _stop = new(); private Thread? _thread;
        public string? DeviceName => "fake endpoint/process capture";
        public string? EndpointId => CaptureId;
        public double DriftPpm => 0;
        public float Peak => .3f;
        public int ExtraLatencyMs => owner.Config.AppSendVolumes.Count > 0 || owner._routing.Apps.Values.Any(r => r.Output.Kind == MixerOutputKind.Local)
            ? RoutedCapture.RoutedExtraLatencyMs : 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start()
        {
            _thread = new Thread(() =>
            {
                using var timer = new Native.PreciseTimer(); using var mmcss = Native.EnterMmcss("Audio");
                var samples = new float[882]; long next = MediaClock.Now;
                while (!_stop.IsSet)
                {
                    long now = MediaClock.Now; if (now < next) { timer.Sleep(next - now); continue; }
                    var value = owner.Sum(key, owner._routing);
                    for (int i = 0; i < samples.Length; i += 2) { samples[i] = value.Left; samples[i + 1] = value.Right; }
                    fifo.Write(samples); next += MediaClock.FromMs(10);
                    if (next < now - MediaClock.FromMs(30)) next = now;
                }
            }) { IsBackground = true, Name = "fixture legacy capture" };
            _thread.Start();
        }
        public void Dispose() { _stop.Set(); _thread?.Join(); _stop.Dispose(); }
    }
}
