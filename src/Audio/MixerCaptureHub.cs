using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

public sealed record MixerRoot(RouteTarget Target, string Destination, long ProcessIdentity = 0);
public sealed record MixerCapturePlan(long Generation, IReadOnlyList<MixerRoot> Roots);

/// <summary>One include-tree capture, exclusively owned by the hub; fakes supply the same packet contract.</summary>
public interface IMixerProcessInput : IDisposable
{
    bool Drain(List<float> samples);
}

public delegate PacketGain MixerPacketGain(RouteTarget target, ReadOnlySpan<float> samples);

/// <summary>Pure planning reuses the established browser/outermost-tree rules and session attenuation semantics.</summary>
public static class MixerCapturePlanner
{
    public static MixerCapturePlan Plan(long generation, MixerRoutingSettings routing, AppConfig config,
        IEnumerable<SessionEntry> sessions, IReadOnlyDictionary<uint, ProcessEntry> processes,
        IReadOnlyDictionary<uint, long>? identities = null)
    {
        AudioRoute Legacy(MixerRoute r) => r.Output.Kind == MixerOutputKind.Local ? AudioRoute.Local :
            r.AlsoLocal ? AudioRoute.Both : AudioRoute.HomePod;
        var rules = new RouteRules(Legacy(routing.Default), routing.Apps.ToDictionary(p => p.Key, p => Legacy(p.Value)))
        { ForceRouting = true, SendVolumes = new Dictionary<string, int>(config.AppSendVolumes) };
        var plan = RoutePlanner.Plan(rules, sessions, processes);
        return new(generation, plan.Targets.Select(t => new MixerRoot(t,
            MixerDestinationResolver.Resolve(config, routing.For(t.Key)).Key, identities?.GetValueOrDefault(t.RootPid) ?? 0)).ToArray());
    }
}

/// <summary>
/// One capture per root, independent mixers/resamplers/FIFOs per destination. A slow receiver never runs on this
/// thread. Contributions are bounded before entering StreamMixer; route changes remove only the moving root,
/// including its unsent FIFO samples. All mutation and each complete capture cycle share a generation boundary.
/// </summary>
/// <remarks>
/// The extra sources (the mic) are read once for every bus, as far as the bus that has written the most needs them;
/// each bus takes the next frames from its own position, as many as it writes. 2026-10-27 review: a fixed 441 frames
/// per cycle (cycles run 10-20 ms apart) drained the mic below real time, so its tap dropped 60 ms at a time, and a
/// bus that wrote two packets in one cycle got a 10 ms hole in the voice.
/// </remarks>
public sealed class MixerCaptureHub : IDisposable
{
    public const int MaxPacketFrames = 4410;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Input> _inputs = [];
    private readonly Dictionary<string, Bus> _buses = [];
    private readonly Func<RouteTarget, IMixerProcessInput> _open;
    private readonly MixerPacketGain _gain;
    private readonly MixSources _sources;
    private readonly int _rate, _outRate;
    private readonly bool _steerClock;
    private readonly List<float> _drained = new(MaxPacketFrames * 2);
    private float[] _sourceScratch = new float[MaxPacketFrames * 2];
    private float[] _extras = new float[MaxPacketFrames * 3 * 2]; // read from the sources, not yet used by every bus
    private int _extrasFrames;
    private bool _disposed;

    /// <summary>Time source (QPC ticks) for the buses that carry the extras alone; tests replace it.</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;
    public long Generation { get; private set; } = -1;
    public long DroppedInputFrames { get; private set; }
    public int InputCount { get { lock (_gate) return _inputs.Count; } }
    public int BusCount { get { lock (_gate) return _buses.Count; } }
    public bool IsCapturing(uint pid)
    { lock (_gate) return _inputs.TryGetValue(pid, out var input) && _buses.TryGetValue(input.Root.Destination, out var bus) && bus.Started; }
    private sealed class Input(MixerRoot root, IMixerProcessInput capture)
    { public MixerRoot Root = root; public IMixerProcessInput Capture = capture; }

    public MixerCaptureHub(int rate, Func<RouteTarget, IMixerProcessInput> open, MixSources? sources = null,
        MixerPacketGain? gain = null, int outRate = 44100, bool steerClock = true)
    {
        _rate = rate; _outRate = outRate; _open = open; _sources = sources ?? new();
        _steerClock = steerClock;
        _gain = gain ?? ((_, _) => new PacketGain(1, 1));
    }

    public ICaptureSource Subscribe(string destination, AudioFifo fifo)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_buses.ContainsKey(destination)) throw new InvalidOperationException("A destination already owns its capture bus");
            var bus = new Bus(destination, fifo, _rate, _outRate, _steerClock, () => Unsubscribe(destination), Clock())
            { ExtrasPosition = _extrasFrames };
            _buses.Add(destination, bus);
            foreach (var input in _inputs.Values.Where(i => i.Root.Destination == destination)) bus.Add(input.Root.Target.RootPid);
            return bus;
        }
    }

    private void Unsubscribe(string destination)
    {
        lock (_gate)
        {
            if (!_buses.TryGetValue(destination, out var bus)) return;
            // One controller's disposal must not restore or stop another destination's roots.
            foreach (var pid in _inputs.Where(p => p.Value.Root.Destination == destination).Select(p => p.Key).ToArray()) Remove(pid);
            bus.ClearExtras();
            _buses.Remove(destination);
        }
    }

    public void Apply(MixerCapturePlan plan)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (plan.Generation < Generation) return;
            if (plan.Roots.Select(r => r.Target.RootPid).Distinct().Count() != plan.Roots.Count)
                throw new ArgumentException("Duplicate process capture root", nameof(plan));
            foreach (var (pid, input) in _inputs.ToArray())
            {
                var next = plan.Roots.FirstOrDefault(r => r.Target.RootPid == pid && r.ProcessIdentity == input.Root.ProcessIdentity);
                if (next == null) { Remove(pid); continue; }
                if (next.Destination != input.Root.Destination || !next.Target.SameCapture(input.Root.Target))
                {
                    if (_buses.TryGetValue(input.Root.Destination, out var old)) old.Remove(pid);
                    if (_buses.TryGetValue(next.Destination, out var nextBus)) nextBus.Add(pid);
                }
                input.Root = next;
            }
            foreach (var root in plan.Roots)
            {
                if (_inputs.ContainsKey(root.Target.RootPid) || !_buses.TryGetValue(root.Destination, out var bus)) continue;
                try
                {
                    var capture = _open(root.Target);
                    _inputs.Add(root.Target.RootPid, new(root, capture));
                    bus.Add(root.Target.RootPid);
                }
                catch (Exception ex) { Log.Warn($"mixer capture {root.Target.RootPid}: {ex.Message}"); }
            }
            Generation = plan.Generation;
        }
    }

    /// <summary>Move existing capture ownership before old sessions stop; opening the replacement bus may await a connection.</summary>
    internal void Retarget(IReadOnlyDictionary<string, string?> destinations)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var (pid, input) in _inputs.ToArray())
            {
                if (!destinations.TryGetValue(input.Root.Target.Key, out var next)) continue;
                if (next == null) { Remove(pid); continue; }
                if (next == input.Root.Destination) continue;
                if (_buses.TryGetValue(input.Root.Destination, out var previous)) previous.Remove(pid);
                input.Root = input.Root with { Destination = next };
                if (_buses.TryGetValue(next, out var bus)) bus.Add(pid);
            }
        }
    }

    private void Remove(uint pid)
    {
        if (!_inputs.Remove(pid, out var input)) return;
        if (_buses.TryGetValue(input.Root.Destination, out var bus)) bus.Remove(pid);
        input.Capture.Dispose();
    }

    public void Cycle()
    {
        lock (_gate)
        {
            if (_disposed) return;
            uint? broken = null;
            foreach (var (pid, input) in _inputs)
            {
                _drained.Clear();
                try { if (!input.Capture.Drain(_drained)) { broken = pid; continue; } }
                catch (Exception ex) { Log.Warn($"mixer capture {pid}: {ex.Message}"); broken = pid; continue; }
                int frames = _drained.Count / 2;
                if (frames == 0) continue;
                if (frames > MaxPacketFrames)
                {
                    DroppedInputFrames += frames - MaxPacketFrames;
                    _drained.RemoveRange(0, (frames - MaxPacketFrames) * 2);
                }
                var samples = CollectionsMarshal.AsSpan(_drained);
                // The capture list's storage is reused; no packet-sized array is allocated here.
                if (_buses.TryGetValue(input.Root.Destination, out var destination))
                    destination.Push(pid, samples, _gain(input.Root.Target, samples));
            }
            if (broken is { } failed) Remove(failed);
            if (_buses.Count == 0) return;
            long now = Clock();
            var sources = _sources.Snapshot;
            bool hasExtras = MixSources.HasAudio(sources);
            int need = 0;
            foreach (var bus in _buses.Values) need = Math.Max(need, bus.Mix(now, hasExtras));
            if (!hasExtras) _extrasFrames = 0;
            else if (need > _extrasFrames)
            {
                if (_extras.Length < need * 2) Array.Resize(ref _extras, need * 2);
                var fresh = _extras.AsSpan(_extrasFrames * 2, (need - _extrasFrames) * 2);
                fresh.Clear();
                StreamMixer.AddSources(fresh, sources, ref _sourceScratch);
                _extrasFrames = need;
            }
            int used = _extrasFrames;
            foreach (var bus in _buses.Values) used = Math.Min(used, bus.Write(_extras, _extrasFrames, hasExtras));
            if (used > 0)
            {
                // Every bus is past these frames.
                _extrasFrames -= used;
                Array.Copy(_extras, used * 2, _extras, 0, _extrasFrames * 2);
                foreach (var bus in _buses.Values) bus.ExtrasPosition -= used;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pid in _inputs.Keys.ToArray()) Remove(pid);
            _buses.Clear();
        }
    }

    private sealed class Bus : ICaptureSource
    {
        /// <summary>As RoutedCapture: a bus that captured nothing for this long carries the extras alone.</summary>
        private static readonly long SourceClockGap = Stopwatch.Frequency / 50; // 20 ms

        private readonly AudioFifo _fifo;
        private readonly StreamMixer _mixer;
        private readonly Dictionary<uint, Resampler> _resamplers = [];
        private readonly Dictionary<uint, List<float>> _resampled = [];
        private readonly Dictionary<uint, float[]> _contributions = [];
        private readonly Dictionary<uint, int> _counts = [];
        private readonly List<float> _mix = new(MaxPacketFrames * 2);
        private readonly float[] _padded;
        private readonly int _contributionLength;
        private readonly Action _release;
        private readonly Action<int, float[], int, int> _observe;
        private readonly int _rate, _outRate;
        private readonly int _trimLag;
        private readonly long _maxFillTicks;
        private readonly bool _steerClock;
        private double _averageDepth;
        private int _frames;
        private long _lastOutput;
        private bool _extrasOnly;
        private bool _started, _disposed;
        internal bool Started => _started && !_disposed;

        /// <summary>The bus's next frame in the hub's extras.</summary>
        internal int ExtrasPosition;

        public Bus(string name, AudioFifo fifo, int rate, int outRate, bool steerClock, Action release, long now)
        {
            DeviceName = name; _fifo = fifo; _rate = rate; _outRate = outRate; _release = release;
            _steerClock = steerClock; _averageDepth = fifo.TargetFrames; _lastOutput = now;
            _mixer = new(rate * 30 / 1000); _observe = Observe;
            _padded = new float[(MaxPacketFrames + rate * 30 / 1000) * 2];
            _contributionLength = _padded.Length * Math.Max(1, (_outRate + _rate - 1) / _rate);
            _trimLag = outRate * 30 / 1000;
            _maxFillTicks = MaxPacketFrames * Stopwatch.Frequency / outRate;
        }
        public string? DeviceName { get; }
        public double DriftPpm { get; private set; }
        public float Peak { get; private set; }
        public int ExtraLatencyMs => RoutedCapture.RoutedExtraLatencyMs;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() => _started = true;
        public void Dispose() { if (_disposed) return; _disposed = true; _release(); }
        public void Add(uint pid)
        {
            _mixer.Add(unchecked((int)pid));
            _resamplers[pid] = new(_rate, _outRate);
            _resampled[pid] = new(MaxPacketFrames * 2);
            _contributions[pid] = new float[_contributionLength];
        }
        public void Remove(uint pid)
        {
            _mixer.Remove(unchecked((int)pid)); _resamplers.Remove(pid); _resampled.Remove(pid); _contributions.Remove(pid);
            _counts.Remove(pid);
            _fifo.RemoveMixerSource(pid);
        }
        public void ClearExtras() => _fifo.RemoveMixerSource(0);
        public void Push(uint pid, ReadOnlySpan<float> samples, PacketGain gain)
        {
            // StreamMixer trims backlog after each cycle; cap the incoming packet as well.
            _mixer.Push(unchecked((int)pid), samples, gain);
            _mixer.Limit(unchecked((int)pid), MaxPacketFrames);
        }
        private void Observe(int id, float[] input, int take, int count)
        {
            uint pid = unchecked((uint)id);
            var padded = _padded.AsSpan(0, count * 2);
            padded.Clear(); input.AsSpan(0, take * 2).CopyTo(padded);
            var resampled = _resampled[pid]; resampled.Clear();
            _resamplers[pid].Process(padded, resampled);
            _counts[pid] = resampled.Count / 2;
            _frames = Math.Max(_frames, resampled.Count / 2);
        }
        /// <summary>
        /// Mixes and resamples what this cycle captured for the bus and decides how many frames it writes. Returns
        /// how far into the hub's extras it reads (its position plus those frames).
        /// </summary>
        public int Mix(long now, bool hasExtras)
        {
            _mix.Clear(); _frames = 0;
            _mixer.Mix(_mix, _observe);
            if (!_started || !hasExtras || _frames > 0)
            {
                _lastOutput = now;
                _extrasOnly = false;
            }
            else if (_extrasOnly || now - _lastOutput > SourceClockGap)
            {
                // Nothing captured (no app is playing to this destination): carry the extras alone on the wall
                // clock, as RoutedCapture does. Only after a real gap, never on every empty cycle: an input that is
                // merely a cycle late would get silence in front of its audio. Whole frames are counted from the
                // last one written, so the fractions add up instead of getting lost.
                _extrasOnly = true;
                long elapsed = now - _lastOutput;
                if (elapsed >= _maxFillTicks)
                {
                    _frames = MaxPacketFrames;
                    _lastOutput = now;
                }
                else
                {
                    _frames = (int)(elapsed * _outRate / Stopwatch.Frequency);
                    _lastOutput += _frames * Stopwatch.Frequency / _outRate;
                }
            }
            return _started ? ExtrasPosition + _frames : ExtrasPosition;
        }

        /// <summary>
        /// Writes the frames <see cref="Mix"/> decided on, with the next of them from the extras (frames up to
        /// <paramref name="head"/> are valid). Returns the bus's new position in the extras.
        /// </summary>
        public int Write(float[] extras, int head, bool hasExtras)
        {
            if (!hasExtras) ExtrasPosition = 0;
            if (!_started) return ExtrasPosition = head; // nothing sent: no claim on the extras
            if (_frames > 0) WriteFrames(extras, hasExtras);
            int lag = head - ExtrasPosition;
            // Behind the bus that used the most extras: a stall skips at once; otherwise one frame per cycle trims
            // inaudibly what drift between two receivers' clocks adds up, so this destination's mic delay stays low.
            if (lag > MaxPacketFrames) ExtrasPosition = head - MaxPacketFrames;
            else if (lag > _trimLag) ExtrasPosition++;
            return ExtrasPosition;
        }

        private void WriteFrames(float[] extras, bool hasExtras)
        {
            foreach (var (pid, resampled) in _resampled)
            {
                var dest = _contributions[pid].AsSpan(0, _frames * 2);
                dest.Clear();
                if (_counts.TryGetValue(pid, out int count) && count > 0 && _mix.Count > 0)
                    CollectionsMarshal.AsSpan(resampled).CopyTo(dest[((_frames - count) * 2)..]);
            }
            if (hasExtras)
            {
                if (!_contributions.TryGetValue(0, out var mic)) _contributions[0] = mic = new float[_contributionLength];
                extras.AsSpan(ExtrasPosition * 2, _frames * 2).CopyTo(mic);
                ExtrasPosition += _frames;
            }
            else _contributions.Remove(0);
            _fifo.WriteMixer(_contributions, _frames);
            if (_steerClock)
            {
                // Same depth controller as RoutedCapture, independently per destination; receiver backlog never
                // steers the resamplers of another destination or changes anyone else's FIFO target.
                _averageDepth += .02 * (_fifo.Depth - _averageDepth);
                double adjust = Math.Clamp((_averageDepth - _fifo.TargetFrames) / _outRate * .02, -500e-6, 500e-6);
                foreach (var resampler in _resamplers.Values) resampler.Adjust = adjust;
                DriftPpm = adjust * 1e6;
            }
            float peak = 0;
            foreach (var samples in _contributions.Values)
                for (int i = 0; i < _frames * 2; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
            Peak = peak;
        }
    }
}
