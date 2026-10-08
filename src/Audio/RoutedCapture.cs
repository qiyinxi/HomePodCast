using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Capture with per-app routing. Without routing rules it is exactly today's path: loopback of the
/// whole default output (lowest latency). With rules it captures each app that should reach the
/// speaker through its own process-loopback client (include tree), compensating the apps that the
/// router silences here, and mixes them. Extra sources (<see cref="MixSources"/>) are added after
/// resampling. Default-device and routing changes re-open the inputs on this thread; the FIFO and
/// therefore the speaker connection stay untouched.
/// </summary>
public sealed class RoutedCapture : ICaptureSource
{
    /// <summary>Measured with `proctest`: process loopback delivers ~31-40 ms later than endpoint loopback.</summary>
    public const int RoutedExtraLatencyMs = 35;

    private const long BufferDuration = 200_000; // 20 ms
    private static readonly long DeviceCheckInterval = Stopwatch.Frequency;
    private static readonly long OpenRetry = Stopwatch.Frequency * 5;
    private static readonly long GuardHold = Stopwatch.Frequency * 3 / 10;
    private static readonly long SourceClockGap = Stopwatch.Frequency / 50; // 20 ms without loopback data

    private readonly AudioFifo _fifo;
    private readonly int _outRate;
    private readonly MixSources _sources;
    private readonly SessionRouter _router;
    private readonly Thread _thread;
    private readonly AutoResetEvent _ready = new(false);
    private readonly ConcurrentDictionary<uint, byte> _capturing = new();
    private volatile bool _stop;
    private double _avgDepth;

    public string? DeviceName { get; private set; }
    public double DriftPpm { get; private set; }

    private long _maxGapTicks;

    public double TakeMaxGapMs() => Interlocked.Exchange(ref _maxGapTicks, 0) * 1000.0 / Stopwatch.Frequency;
    public float Peak { get; private set; }
    public double ProportionalGain { get; set; } = 0.02;

    /// <summary>Whether apps are captured one by one (process loopback) right now.</summary>
    public bool Routed { get; private set; }

    public int ExtraLatencyMs => Routed ? RoutedExtraLatencyMs : 0;

    public event Action<string>? DeviceChanged;

    public RoutedCapture(AudioFifo fifo, int outRate, AppRouting routing, MixSources? sources = null)
    {
        _fifo = fifo;
        _outRate = outRate;
        _sources = sources ?? new MixSources();
        _avgDepth = fifo.TargetFrames;
        _router = new SessionRouter(routing, root => _capturing.ContainsKey(root));
        _router.PlanChanged += () => _ready.Set();
        _thread = new Thread(Run) { IsBackground = true, Name = "Routed capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _router.Start();
        _thread.Start();
    }

    private sealed class Client(LoopbackStream stream, RouteTarget target)
    {
        public LoopbackStream Stream { get; } = stream;
        public RouteTarget Target { get; set; } = target;
        public long HoldUntil;
        public long LastWarning;
    }

    private void Run()
    {
        using var mmcss = Native.EnterMmcss("Audio");
        _router.WaitFirstPlan(1500); // start in the right mode instead of switching a moment later
        while (!_stop)
        {
            try
            {
                CaptureDevice();
            }
            catch (Exception ex) when (!_stop)
            {
                Log.Warn($"capture: {ex.Message}; retrying");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>Capture until stopped, the default device changes, or routing switches mode.</summary>
    private void CaptureDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        LoopbackStream? endpoint = null;
        var clients = new Dictionary<uint, Client>();
        var failed = new Dictionary<uint, long>();
        IntPtr ready = _ready.SafeWaitHandle.DangerousGetHandle();
        try
        {
            CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device), "default device");
            device.GetId(out var deviceId);
            string name = CoreAudio.FriendlyName(device);

            var plan = _router.Plan;
            bool routed = !plan.Endpoint;
            int rate;
            if (routed) rate = MixRate(device);
            else
            {
                endpoint = LoopbackStream.OpenEndpoint(device, ready, BufferDuration);
                rate = endpoint.Format.SampleRate;
            }
            var mixer = new StreamMixer(maxLagFrames: rate * 30 / 1000);
            const int EndpointId = -1;
            if (endpoint != null) mixer.Add(EndpointId);

            Routed = routed;
            DeviceName = routed ? L.F("{0}（按程序分流）", name) : name;
            Log.Info($"capturing \"{name}\" {rate} Hz, {(routed ? "per app (process loopback)" : "whole output")}");
            DeviceChanged?.Invoke(DeviceName);

            var resampler = new Resampler(rate, _outRate);
            var drained = new List<float>(rate / 10 * 2);
            var mixed = new List<float>(rate / 10 * 2);
            var resampled = new List<float>(_outRate / 10 * 2);
            var scratch = new float[_outRate / 10 * 2];
            var silence = new float[_outRate / 10 * 2];
            endpoint?.Start();
            long nextDeviceCheck = Stopwatch.GetTimestamp() + DeviceCheckInterval;
            long lastData = Stopwatch.GetTimestamp(), lastOutput = lastData;

            while (!_stop)
            {
                _ready.WaitOne(20); // loopback signals nothing while the endpoint is silent
                long now = Stopwatch.GetTimestamp();

                var current = _router.Plan;
                if (current.Endpoint != plan.Endpoint)
                {
                    Log.Info(current.Endpoint ? "routing off: capturing the whole output again" : "routing on: capturing apps one by one");
                    _fifo.DiscardToTarget();
                    return;
                }
                plan = current;
                if (routed) Reconcile(plan, clients, failed, mixer, rate, ready, now);

                if (endpoint != null)
                {
                    drained.Clear();
                    if (!endpoint.Drain(drained)) return; // device invalidated
                    if (drained.Count > 0) mixer.Push(EndpointId, CollectionsMarshal.AsSpan(drained));
                }
                uint? broken = null;
                foreach (var (root, c) in clients)
                {
                    drained.Clear();
                    try
                    {
                        if (!c.Stream.Drain(drained)) return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"routing: {c.Target.Key} (pid {root}): {ex.Message}");
                        broken = root;
                        continue;
                    }
                    if (drained.Count == 0) continue;
                    var span = CollectionsMarshal.AsSpan(drained);
                    mixer.Push(Id(root), span, Gain(c, root, span, now));
                }
                if (broken is { } b)
                {
                    _capturing.TryRemove(b, out _);
                    clients[b].Stream.Dispose();
                    clients.Remove(b);
                    mixer.Remove(Id(b));
                    failed[b] = now;
                    _router.Poke(); // un-silence it until it can be captured again
                }

                var sources = _sources.Snapshot;
                mixed.Clear();
                if (mixer.Mix(mixed) > 0)
                {
                    if (now - lastData > Stopwatch.Frequency / 5) resampler.Reset(); // resume after silence
                    else if (now - lastData > Volatile.Read(ref _maxGapTicks)) Volatile.Write(ref _maxGapTicks, now - lastData);
                    lastData = now;
                    resampled.Clear();
                    resampler.Process(CollectionsMarshal.AsSpan(mixed), resampled);
                    var output = CollectionsMarshal.AsSpan(resampled);
                    StreamMixer.AddSources(output, sources, ref scratch);
                    _fifo.Write(output);
                    TrackPeak(output);
                    SteerDrift(resampler);
                    lastOutput = now;
                }
                else if (sources.Count > 0 && now - lastOutput > SourceClockGap)
                {
                    // Nothing captured (silent endpoint, or no app routed to the speaker): keep extra
                    // sources flowing on the wall clock.
                    int frames = (int)Math.Min((now - lastOutput) * _outRate / Stopwatch.Frequency, _outRate / 5);
                    lastOutput = now;
                    if (silence.Length < frames * 2) silence = new float[frames * 2];
                    var output = silence.AsSpan(0, frames * 2);
                    output.Clear();
                    StreamMixer.AddSources(output, sources, ref scratch);
                    _fifo.Write(output);
                    TrackPeak(output);
                }

                if (now >= nextDeviceCheck)
                {
                    nextDeviceCheck = now + DeviceCheckInterval;
                    if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var currentDevice) >= 0)
                    {
                        currentDevice.GetId(out var currentId);
                        Marshal.ReleaseComObject(currentDevice);
                        if (currentId != deviceId)
                        {
                            Log.Info("default output device changed, switching");
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var (root, c) in clients)
            {
                _capturing.TryRemove(root, out _);
                c.Stream.Dispose();
            }
            endpoint?.Dispose();
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static int Id(uint root) => unchecked((int)root);

    /// <summary>Open clients for new targets (one per cycle: activation can take a few ms), close stale ones.</summary>
    private void Reconcile(RoutePlan plan, Dictionary<uint, Client> clients, Dictionary<uint, long> failed,
        StreamMixer mixer, int rate, IntPtr ready, long now)
    {
        bool opened = false;
        foreach (var target in plan.Targets)
        {
            if (clients.TryGetValue(target.RootPid, out var existing))
            {
                existing.Target = target;
                continue;
            }
            if (opened || failed.TryGetValue(target.RootPid, out var at) && now - at < OpenRetry) continue;
            opened = true;
            try
            {
                var stream = LoopbackStream.OpenProcess(target.RootPid, includeTree: true, rate, ready, BufferDuration);
                stream.Start();
                clients[target.RootPid] = new Client(stream, target);
                mixer.Add(Id(target.RootPid));
                _capturing[target.RootPid] = 0;
                failed.Remove(target.RootPid);
                Log.Info($"routing: capturing {target.Key} (pid {target.RootPid}){(target.Compensate ? ", HomePod only" : "")}");
                _router.Poke(); // now its sessions may be silenced here
            }
            catch (Exception ex)
            {
                failed[target.RootPid] = now;
                Log.Warn($"routing: cannot capture {target.Key} (pid {target.RootPid}): {ex.Message}");
            }
        }
        foreach (var root in clients.Keys.Where(r => !plan.Targets.Any(t => t.RootPid == r)).ToList())
        {
            _capturing.TryRemove(root, out _);
            clients[root].Stream.Dispose();
            clients.Remove(root);
            mixer.Remove(Id(root));
            Log.Info($"routing: stopped capturing pid {root}");
        }
    }

    /// <summary>
    /// Gain for one drained batch: 1/epsilon for an app silenced here once the router confirmed it, but
    /// never for audio that is too loud to be attenuated (a session we have not silenced yet, or a
    /// volume raised elsewhere): that would come out 100 dB too loud.
    /// </summary>
    private float Gain(Client c, uint root, ReadOnlySpan<float> samples, long now)
    {
        if (!c.Target.Compensate) return 1f;
        bool confirmed = now >= c.HoldUntil && _router.IsConfirmed(root, now);
        if (!confirmed) return 1f;
        float peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        float gain = SessionAttenuation.CompensationGain(true, true, peak);
        if (gain == 1f)
        {
            c.HoldUntil = now + GuardHold;
            if (now - c.LastWarning > Stopwatch.Frequency * 10)
            {
                c.LastWarning = now;
                Log.Warn($"routing: unattenuated audio from {c.Target.Key} (peak {peak:E1}); holding gain, re-checking volumes");
            }
            _router.Poke();
        }
        return gain;
    }

    private static int MixRate(IMMDevice device)
    {
        CoreAudio.Check(device.Activate(ref CoreAudio.IidAudioClient, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "activate");
        var client = (IAudioClient)obj;
        IntPtr mix = IntPtr.Zero;
        try
        {
            CoreAudio.Check(client.GetMixFormat(out mix), "mix format");
            return WaveFormat.FromPointer(mix).SampleRate;
        }
        finally
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(client);
        }
    }

    private void SteerDrift(Resampler resampler)
    {
        _avgDepth += 0.02 * (_fifo.Depth - _avgDepth);
        double errorSeconds = (_avgDepth - _fifo.TargetFrames) / _outRate;
        resampler.Adjust = Math.Clamp(errorSeconds * ProportionalGain, -500e-6, 500e-6);
        DriftPpm = resampler.Adjust * 1e6;
    }

    private void TrackPeak(ReadOnlySpan<float> samples)
    {
        float peak = Peak * 0.95f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        Peak = peak;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _ready.Set();
        if (_thread.IsAlive) _thread.Join(1000);
        _router.Dispose(); // makes silenced apps audible here again
    }
}
