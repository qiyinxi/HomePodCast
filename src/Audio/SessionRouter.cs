using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Keeps the routing plan current (rules x sessions on the captured output x process tree) and silences
/// "HomePod only" sessions there once their capture runs, restoring them when that ends. The captured
/// output is the Windows default output, or the device chosen in AppRouting.CaptureDeviceId (rules then
/// apply to the apps playing on that device; see <see cref="CaptureEndpoint"/>). Runs on its own
/// MTA thread so session enumeration never stalls the capture thread. Reacts to new sessions at once
/// (IAudioSessionNotification) and re-checks everything every second. While anything is silenced it
/// re-reads those volumes (and the default output) every timer tick, well inside the ~35-50 ms the
/// capture runs behind, so a volume raised in the Windows mixer or by the app closes the compensation
/// gate before that louder audio is captured.
/// </summary>
internal sealed class SessionRouter : IDisposable
{
    private static readonly long ConfirmDelay = Stopwatch.Frequency / 10;   // > the ~35-50 ms process-loopback delay
    private static readonly long RefreshInterval = Stopwatch.Frequency;

    /// <summary>Compensation needs a volume check at least this recent (a stalled router fails silent, not loud).</summary>
    internal static readonly long VerifyWindow = Stopwatch.Frequency / 10;

    /// <summary>Wait between volume checks while something is silenced (rounds up to the ~15.6 ms timer tick).</summary>
    internal const int WatchIntervalMs = 5;
    private const int IdleIntervalMs = 250;

    private static Guid _context = new("6d1b6a52-5c1a-4c39-9f1e-2a8f6f0b7d31"); // marks our own volume changes

    private readonly AppRouting _routing;
    private readonly Func<uint, bool> _isCapturing;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _firstPlan = new();
    private readonly Thread _thread;
    private readonly CompensationGate _gate = new(ConfirmDelay, VerifyWindow);
    private readonly HashSet<uint> _unverified = [];
    private readonly Dictionary<string, Tracked> _silenced = [];   // by session instance id
    private readonly object _lock = new();
    private volatile bool _stop;
    private volatile bool _refresh = true;
    private volatile RoutePlan _plan = RoutePlan.EndpointOnly;

    private sealed class Tracked
    {
        public required object Session;           // RCW; also ISimpleAudioVolume
        public uint Pid;
        public uint Root;
        public bool Seen;
        public ISimpleAudioVolume Volume => (ISimpleAudioVolume)Session;
    }

    private sealed record SessionInfo(string InstanceId, uint Pid, bool IsSystem, object Session);

    public SessionRouter(AppRouting routing, Func<uint, bool> isCapturing)
    {
        _routing = routing;
        _isCapturing = isCapturing;
        _routing.Changed += Poke;
        _thread = new Thread(Run) { IsBackground = true, Name = "Session router", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    public RoutePlan Plan => _plan;

    /// <summary>Raised on the router thread when <see cref="Plan"/> changed.</summary>
    public event Action? PlanChanged;

    public void Start() => _thread.Start();

    public bool WaitFirstPlan(int ms) => _firstPlan.Wait(ms);

    /// <summary>Re-check sessions and volumes now (rules changed, new session, odd level captured).</summary>
    public void Poke()
    {
        _refresh = true;
        _wake.Set();
    }

    /// <summary>
    /// The target's sessions have been silenced long enough that captured audio is attenuated, and their
    /// volumes were read back attenuated within <see cref="VerifyWindow"/>.
    /// </summary>
    public bool IsConfirmed(uint root, long now) => _gate.IsOpen(root, now);

    private void Run()
    {
        IAudioSessionManager2? manager = null;
        string? managerDevice = null;
        var notifier = new Notifier(this);
        var devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        long nextRefresh = 0;
        bool first = true;
        try
        {
            while (!_stop)
            {
                long now = Stopwatch.GetTimestamp();
                if (_refresh || now >= nextRefresh)
                {
                    _refresh = false;
                    nextRefresh = now + RefreshInterval;
                    try
                    {
                        Refresh(ref manager, ref managerDevice, notifier, first);
                        first = false;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"routing: {ex.Message}");
                    }
                    _firstPlan.Set();
                }
                bool watching = CheckVolumes(devices, managerDevice);
                _wake.WaitOne(watching ? WatchIntervalMs : IdleIntervalMs);
            }
        }
        finally
        {
            RestoreAll();
            Unregister(ref manager, notifier);
            Marshal.ReleaseComObject(devices);
            SessionAttenuation.SilencedPids = new HashSet<uint>();
        }
    }

    private void Refresh(ref IAudioSessionManager2? manager, ref string? managerDevice, Notifier notifier, bool first)
    {
        var rules = _routing.Rules;
        bool routing = AppRouting.Supported && rules.NeedsRouting;
        var sessions = Enumerate(ref manager, ref managerDevice, notifier);
        var kept = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            var plan = routing
                ? RoutePlanner.Plan(rules, sessions.Select(s => new SessionEntry(s.Pid, s.IsSystem)), ProcessTree.Snapshot())
                : RoutePlan.EndpointOnly;
            if (!plan.SameAs(_plan))
            {
                _plan = plan;
                Log.Info(plan.Endpoint ? "routing: whole output" :
                    $"routing: capture {(plan.Targets.Count == 0 ? "nothing" : string.Join(", ", plan.Targets.Select(t =>
                        $"{t.Key}#{t.RootPid}{(t.Compensate ? " (HomePod only)" : "")}")))}");
                PlanChanged?.Invoke();
            }
            Apply(plan, sessions, first, kept);
        }
        finally
        {
            // Each enumeration hands out one RCW reference per session; a newly tracked session keeps it.
            foreach (var s in sessions)
                if (!kept.Contains(s.Session)) Marshal.ReleaseComObject(s.Session);
        }
    }

    /// <summary>Silence what should be silent (once captured), restore everything else we touched.</summary>
    private void Apply(RoutePlan plan, List<SessionInfo> sessions, bool first, HashSet<object> kept)
    {
        var want = plan.Silenced.Where(s => _isCapturing(s.TargetRoot)).ToDictionary(s => s.SessionPid, s => s.TargetRoot);
        long now = Stopwatch.GetTimestamp();
        var changed = new HashSet<uint>();
        lock (_lock)
        {
            if (_stop) return; // exiting: RestoreAll has run or is about to; silence nothing again
            foreach (var t in _silenced.Values) t.Seen = false;
            foreach (var s in sessions)
            {
                if (s.IsSystem) continue;
                var volume = (ISimpleAudioVolume)s.Session;
                if (volume.GetMasterVolume(out float raw) < 0) continue;
                if (want.TryGetValue(s.Pid, out uint root))
                {
                    if (!SessionAttenuation.IsAttenuated(raw) && raw > 0)
                    {
                        volume.SetMasterVolume(SessionAttenuation.Attenuated(raw), ref _context);
                        changed.Add(root);
                        Log.Info($"routing: silenced pid {s.Pid} here (volume {raw:P0} kept for the speaker)");
                    }
                    if (!_silenced.TryGetValue(s.InstanceId, out var t))
                    {
                        _silenced[s.InstanceId] = t = new Tracked { Session = s.Session, Pid = s.Pid, Root = root };
                        kept.Add(s.Session);
                    }
                    t.Root = root;
                    t.Seen = true;
                }
                else if (_silenced.Remove(s.InstanceId, out var t))
                {
                    Restore(t.Volume, s.Pid);
                    ReleaseTracked(t);
                }
                else if (first && SessionAttenuation.IsAttenuated(raw))
                {
                    Restore(volume, s.Pid); // left over from a run that did not exit cleanly
                }
            }
            foreach (var (id, t) in _silenced.Where(kv => !kv.Value.Seen).ToList())
            {
                // Session gone from the default output (expired or device switched): restore it anyway.
                try { Restore(t.Volume, t.Pid); } catch { }
                ReleaseTracked(t);
                _silenced.Remove(id);
            }
        }

        foreach (var root in plan.Targets.Where(t => t.Compensate).Select(t => t.RootPid))
        {
            bool all = want.Values.Contains(root);
            if (!all) _gate.Close(root);
            else if (changed.Contains(root)) _gate.Silenced(root, now);
            else _gate.Keep(root, now);
        }
        foreach (var root in _gate.Roots.Where(r => !plan.Targets.Any(t => t.Compensate && t.RootPid == r)))
            _gate.Close(root);
        SessionAttenuation.SilencedPids = want.Keys.ToHashSet();
    }

    /// <summary>
    /// Every tick while something is silenced: a silenced app's volume raised elsewhere (Windows mixer, the app
    /// itself) is silenced again with compensation paused; volumes read back attenuated keep it allowed. A new
    /// default output pauses all compensation until the next refresh has silenced the sessions there.
    /// Returns whether anything is being watched.
    /// </summary>
    private bool CheckVolumes(IMMDeviceEnumerator devices, string? managerDevice)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_silenced.Count == 0 || _stop) return false;

            // The apps' audio moves to the new device, where their sessions are not silenced yet (a new default
            // output while following it, or another capture device chosen).
            if (managerDevice != null && CapturedDeviceId(devices) is { } id && !CaptureEndpoint.Same(id, managerDevice))
            {
                _gate.CloseAll();
                Poke();
                return true;
            }

            _unverified.Clear();
            foreach (var t in _silenced.Values)
            {
                try
                {
                    if (t.Volume.GetMasterVolume(out float raw) < 0 || SessionAttenuation.IsAttenuated(raw) || raw == 0) continue;
                    _unverified.Add(t.Root);
                    _gate.Silenced(t.Root, now);
                    t.Volume.SetMasterVolume(SessionAttenuation.Attenuated(raw), ref _context);
                    Log.Info($"routing: pid {t.Pid} volume changed to {raw:P0} elsewhere; still HomePod only");
                }
                catch (Exception ex)
                {
                    _unverified.Add(t.Root);
                    Log.Debug($"routing: volume check: {ex.Message}");
                }
            }
            foreach (var t in _silenced.Values)
                if (!_unverified.Contains(t.Root)) _gate.Verified(t.Root, now);
            return true;
        }
    }

    /// <summary>The device whose sessions are planned: the chosen capture device, or the default output.</summary>
    private string? CapturedDeviceId(IMMDeviceEnumerator devices) =>
        _routing.CaptureDeviceId ?? AudioEndpoints.DefaultId(devices, EDataFlow.Render);

    private static void Restore(ISimpleAudioVolume volume, uint pid)
    {
        if (volume.GetMasterVolume(out float raw) < 0 || !SessionAttenuation.IsAttenuated(raw)) return;
        volume.SetMasterVolume(SessionAttenuation.Restored(raw), ref _context);
        Log.Info($"routing: pid {pid} audible here again ({SessionAttenuation.Restored(raw):P0})");
    }

    private static void ReleaseTracked(Tracked t)
    {
        try { Marshal.ReleaseComObject(t.Session); } catch { }
    }

    private void RestoreAll()
    {
        lock (_lock)
        {
            foreach (var t in _silenced.Values)
            {
                try { Restore(t.Volume, t.Pid); } catch (Exception ex) { Log.Warn($"routing: restore pid {t.Pid}: {ex.Message}"); }
                ReleaseTracked(t);
            }
            _silenced.Clear();
            _gate.CloseAll();
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _stop = true; // the router thread must not silence anything again after this restore
        try { RestoreAll(); } catch { }
    }

    private List<SessionInfo> Enumerate(ref IAudioSessionManager2? manager, ref string? managerDevice, Notifier notifier)
    {
        var result = new List<SessionInfo>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        try
        {
            // The captured device; a chosen one that is missing has no sessions to plan (never the default instead).
            device = AudioEndpoints.Open(enumerator, EDataFlow.Render, _routing.CaptureDeviceId);
            if (device == null) return result;
            device.GetId(out var id);
            if (manager == null || id != managerDevice)
            {
                if (managerDevice != null) _gate.CloseAll(); // nothing on the new device is silenced yet
                Unregister(ref manager, notifier);
                var iid = typeof(IAudioSessionManager2).GUID;
                CoreAudio.Check(device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "session manager");
                manager = (IAudioSessionManager2)obj;
                managerDevice = id;
                // Session notifications only start after the first GetSessionEnumerator call.
                if (manager.GetSessionEnumerator(out var warmup) >= 0) Marshal.ReleaseComObject(warmup);
                if (manager.RegisterSessionNotification(notifier) < 0) Log.Warn("routing: no session notifications; polling only");
            }
            CoreAudio.Check(manager.GetSessionEnumerator(out var sessions), "sessions");
            try
            {
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var raw) < 0) continue;
                    var control = (IAudioSessionControl2)raw;
                    if (control.GetState(out int state) < 0 || state == 2 || // expired
                        control.GetSessionInstanceIdentifier(out var instance) < 0)
                    {
                        Marshal.ReleaseComObject(raw);
                        continue;
                    }
                    control.GetProcessId(out uint pid);
                    result.Add(new SessionInfo(instance, pid, control.IsSystemSoundsSession() == 0, raw));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    private static void Unregister(ref IAudioSessionManager2? manager, Notifier notifier)
    {
        if (manager == null) return;
        try { manager.UnregisterSessionNotification(notifier); } catch { }
        Marshal.ReleaseComObject(manager);
        manager = null;
    }

    /// <summary>
    /// Undo silencing left behind by a run that did not exit cleanly (any thread; MTA). On every output: that run
    /// may have captured a chosen device that is no longer the one chosen.
    /// </summary>
    public static void RestoreLeftovers()
    {
        try
        {
            var outputs = AudioEndpoints.Outputs().Select(e => (string?)e.Id).DefaultIfEmpty(null);
            foreach (var output in outputs)
            foreach (var app in AppAudio.Enumerate(output))
            {
                try { app.RestoreIfSilenced(); } catch { }
                app.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"routing: restore leftovers: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _routing.Changed -= Poke;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(3000);
        else RestoreAll();
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Notifier(SessionRouter router) : IAudioSessionNotification, IAgileObject
    {
        public int OnSessionCreated(IntPtr newSession)
        {
            router.Poke();
            return 0;
        }
    }
}
