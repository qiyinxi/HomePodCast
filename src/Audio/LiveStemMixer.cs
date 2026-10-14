using System.Diagnostics;

namespace HomePodCast.Audio;

public sealed class StemSettings
{
    public string? ModuleDirectory { get; set; }
    public int HomePodVocals { get; set; }
    public int HomePodAccompaniment { get; set; } = 100;
    public int MonitorVocals { get; set; } = 100;
    public int MonitorAccompaniment { get; set; }
    public string? MonitorDeviceId { get; set; }
    // Enabling separation and local playback is deliberately never persisted.
}

/// <summary>Why separation stopped by itself. The UI says it in the user's language; the log has the module's own words.</summary>
public enum StemError
{
    None,

    /// <summary>The module's exe could not be started.</summary>
    StartFailed,

    /// <summary>The module refused its model: missing, or not the pinned StemgenRT build (worker exit code 2).</summary>
    ModelRejected,

    /// <summary>The module speaks another protocol: it is not the module of this version.</summary>
    Incompatible,

    /// <summary>No answer in time (20 s to load the model, 2 s per block).</summary>
    TimedOut,

    /// <summary>The module exited, crashed or sent something unusable.</summary>
    Failed,
}

/// <summary>
/// A music-only experimental effect BEFORE mic mix-in/EQ/compression. The sender only copies into a bounded SPSC
/// queue and reads tagged results; ONNX and pipe IO run in a separate optional process and on a worker thread.
/// Missing vocals mute an unequal-gain mix, never leak the original into a karaoke/isolated-vocal destination; the
/// mutes, slider moves and turning separation on or off are faded (<see cref="StemBus"/>, <see cref="SwitchFadeFrames"/>).
/// </summary>
public sealed class LiveStemMixer : IAudioEffect, ITapSource, IDisposable
{
    public const int DelayFrames = 8192;
    public const int ExtraLatencyMs = (DelayFrames * 1000 + 44099) / 44100;
    internal const int BlockFrames = 1024, Hop = 128;

    /// <summary>
    /// Turning separation on or off jumps between the live sound and the sound <see cref="DelayFrames"/> late: the path
    /// that plays fades out over this many frames (10 ms), then the other fades in. On, nothing is heard twice (the
    /// pipeline starts after the live fade); off, the 186 ms still in the pipeline are skipped (catching up needs it).
    /// </summary>
    internal const int SwitchFadeFrames = 441;

    private const float SwitchStep = 1f / SwitchFadeFrames;

    /// <summary>
    /// A pause in Process calls this long means the stream stopped in between (a reconnect, a new session): a pending
    /// switch is made at once, since nothing plays that could click, and no stale delayed audio is faded out.
    /// </summary>
    private static readonly long GapTicks = Stopwatch.Frequency / 5;

    private readonly Func<string, IStemEngine> _engines;
    private readonly bool _testEngine;
    private volatile Pipeline? _pipeline; // what the UI turned on
    private Pipeline? _active;            // what the audio thread plays; it fades from one to the other
    private float _liveGain = 1, _delayedGain = 1;
    private long _lastProcess;
    private volatile AudioTap[] _taps = [];
    private readonly object _tapLock = new();
    private LocalMonitor? _monitor;
    private bool _monitorWanted;
    private volatile bool _monitorBlocked;
    private volatile string? _captured;
    private float _homeVocal, _homeMusic = 1, _localVocal = 1, _localMusic;
    private int _monitorDelayMs;
    private volatile StemError _error;
    private volatile string? _errorDetail;

    /// <summary>Time source (QPC ticks) for the pause rule; tests replace it.</summary>
    internal Func<long> Clock = Stopwatch.GetTimestamp;

    /// <summary>Every block of local (monitor) output as it goes to the taps; tests read it.</summary>
    internal Action<ReadOnlySpan<float>>? LocalSink;

    public StemSettings Settings { get; }
    public bool Enabled => _pipeline != null;
    public bool Ready => _pipeline?.Ready == true;
    internal Thread? WorkerThread => _pipeline?.Thread;
    internal bool Idle => _pipeline?.Idle ?? true;

    /// <summary>Why separation last stopped by itself (None after it was turned on again).</summary>
    public StemError Error => _error;

    /// <summary>The module's own words for <see cref="Error"/> (English, for the log).</summary>
    public string? ErrorDetail => _errorDetail;

    public long MissedFrames => _pipeline?.MissedFrames ?? 0;
    public long DroppedBlocks => _pipeline?.DroppedBlocks ?? 0;
    public double InferenceMs => _pipeline?.InferenceMs ?? 0;

    /// <summary>The wait separation adds to the sound (0 while off): 首页, the local API and the players add it.</summary>
    public int AddedLatencyMs => Enabled ? ExtraLatencyMs : 0;

    public bool MonitorOn => _monitorWanted;
    public bool MonitorBlocked => _monitorBlocked;
    public string? MonitorError => _monitor?.Error;
    public int Rate => 44100;
    public double LatencyMs => Enabled ? DelayFrames * 1000.0 / Rate + _monitorDelayMs : 0;
    public double ChunkMs => 352 * 1000.0 / Rate;
    public int MonitorDelayMs { set => Volatile.Write(ref _monitorDelayMs, Math.Clamp(value, 0, 500)); }

    /// <summary>
    /// The module failed while on (worker thread): the app turns separation off and says why, so the HomePod doesn't
    /// stay silent and the user isn't left guessing.
    /// </summary>
    public event Action? Failed;

    public LiveStemMixer(StemSettings settings) : this(settings, directory => new ProcessStemEngine(directory), false) { }

    /// <summary>Tests: separation by <paramref name="engines"/> instead of the module process; no module folder needed.</summary>
    internal LiveStemMixer(StemSettings settings, Func<string, IStemEngine> engines) : this(settings, engines, true) { }

    private LiveStemMixer(StemSettings settings, Func<string, IStemEngine> engines, bool testEngine)
    {
        Settings = settings;
        _engines = engines;
        _testEngine = testEngine;
        ApplyGains();
    }

    public void ApplyGains()
    {
        Volatile.Write(ref _homeVocal, StemMix.Gain(Settings.HomePodVocals));
        Volatile.Write(ref _homeMusic, StemMix.Gain(Settings.HomePodAccompaniment));
        Volatile.Write(ref _localVocal, StemMix.Gain(Settings.MonitorVocals));
        Volatile.Write(ref _localMusic, StemMix.Gain(Settings.MonitorAccompaniment));
    }

    public static bool ModuleExists(string? directory) => !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, ProcessStemEngine.ExeName)) && File.Exists(Path.Combine(directory, "vocals.onnx"));

    /// <summary>Turns separation on or off (UI thread). <paramref name="why"/> goes into the log line.</summary>
    public void SetEnabled(bool enabled, string why = "by the user")
    {
        if (enabled == Enabled) return;
        if (enabled)
        {
            string? directory = Settings.ModuleDirectory;
            if (!_testEngine && !ModuleExists(directory)) throw new FileNotFoundException("Select an installed stem module first.");
            _error = StemError.None;
            _errorDetail = null;
            _pipeline = new Pipeline(_engines(directory ?? ""), OnPipelineFailed);
            Log.Info($"stems: on {why}, HomePod vocals {Settings.HomePodVocals} % accompaniment {Settings.HomePodAccompaniment} %, " +
                     $"local vocals {Settings.MonitorVocals} % accompaniment {Settings.MonitorAccompaniment} %");
        }
        else
        {
            Interlocked.Exchange(ref _pipeline, null)?.Stop();
            SetMonitor(false);
            Log.Info($"stems: off {why}");
        }
    }

    private void OnPipelineFailed(Pipeline pipeline, StemError kind, string detail)
    {
        if (pipeline != _pipeline) return; // already turned off
        _errorDetail = detail;
        _error = kind;
        Log.Warn($"stems: module failed ({kind}): {detail}");
        Failed?.Invoke();
    }

    public void SetCaptured(string? endpoint)
    {
        _captured = endpoint;
        if (MonitorBlockedBy(_monitorWanted, Settings.MonitorDeviceId, endpoint) != _monitorBlocked) SetMonitor(_monitorWanted);
    }

    /// <summary>
    /// The monitor must not play on the device the stream captures: its stems would go to the HomePod again (feedback).
    /// A null monitor device is the default output.
    /// </summary>
    internal static bool MonitorBlockedBy(bool wanted, string? monitorDeviceId, string? captured, Func<string?>? defaultOutput = null) =>
        wanted && captured != null && CaptureEndpoint.Same(monitorDeviceId ?? (defaultOutput ?? AudioEndpoints.DefaultOutputId)(), captured);

    public void SetMonitor(bool enabled)
    {
        _monitor?.Dispose(); _monitor = null;
        _monitorWanted = enabled && Enabled;
        _monitorBlocked = MonitorBlockedBy(_monitorWanted, Settings.MonitorDeviceId, _captured);
        if (!_monitorWanted || _monitorBlocked) return;
        _monitor = new LocalMonitor(this, Settings.MonitorDeviceId)
        {
            Refuse = id => _captured != null && CaptureEndpoint.Same(id, _captured),
        };
        _monitor.Start();
    }

    public void Attach(AudioTap tap) { lock (_tapLock) { tap.Clear(); _taps = [.. _taps, tap]; } }
    public void Detach(AudioTap tap) { lock (_tapLock) _taps = _taps.Where(t => t != tap).ToArray(); }

    public void Process(Span<float> interleaved, int frames)
    {
        long now = Clock();
        var wanted = _pipeline;
        var active = _active;
        if (active != wanted && (_lastProcess == 0 || now - _lastProcess > GapTicks))
        {
            _active = active = wanted;
            _liveGain = wanted == null ? 1 : 0;
            _delayedGain = 1;
        }
        _lastProcess = now;
        if (active == null && wanted == null && _liveGain >= 1) return; // off: exact pass-through
        frames = Math.Min(frames, interleaved.Length / 2);
        float hv = Volatile.Read(ref _homeVocal), hm = Volatile.Read(ref _homeMusic);
        float lv = Volatile.Read(ref _localVocal), lm = Volatile.Read(ref _localMusic);
        int localDelay = Volatile.Read(ref _monitorDelayMs) * Rate / 1000;
        var taps = _taps;
        // The sender's packet has a fixed 352-frame maximum. Larger standalone calls are split below.
        Span<float> local = stackalloc float[704];
        for (int at = 0; at < frames; at += 352)
        {
            int take = Math.Min(352, frames - at), written = 0;
            for (int f = 0; f < take; f++)
            {
                if (active != wanted)
                {
                    // Fade out whatever plays, then switch; the switched-to path fades in from silence. The frame that
                    // reaches 0 is the first one of the other path (on: the delayed path starts with it, no repeat).
                    if (active == null)
                    {
                        _liveGain -= SwitchStep;
                        if (_liveGain <= SwitchStep / 2) { _liveGain = 0; _active = active = wanted; _delayedGain = 1; }
                    }
                    else
                    {
                        _delayedGain -= SwitchStep;
                        if (_delayedGain <= SwitchStep / 2) { _delayedGain = 1; _liveGain = 0; _active = active = wanted; }
                    }
                }
                else if (active == null) _liveGain = Math.Min(1, _liveGain + SwitchStep);

                int i = (at + f) * 2;
                float left = interleaved[i] * _liveGain, right = interleaved[i + 1] * _liveGain;
                if (active != null)
                {
                    long homeAt = active.Push(interleaved[i], interleaved[i + 1]) - DelayFrames;
                    if (!active.Home.Mix(active.Original, active.Vocals, homeAt, hv, hm, out float homeLeft, out float homeRight) &&
                        active.Counts(homeAt))
                        active.MissedFrames++;
                    left += homeLeft * _delayedGain; right += homeRight * _delayedGain;
                    // Local stems also wait the AirPlay playout delay, to be heard about when the HomePod plays them.
                    active.Local.Mix(active.Original, active.Vocals, homeAt - localDelay, lv, lm, out float localLeft, out float localRight);
                    local[written * 2] = localLeft * _delayedGain; local[written * 2 + 1] = localRight * _delayedGain;
                    written++;
                }
                interleaved[i] = left; interleaved[i + 1] = right;
            }
            if (written == 0) continue;
            var block = local[..(written * 2)];
            foreach (var tap in taps) tap.Write(block);
            LocalSink?.Invoke(block);
        }
    }

    public void Dispose() => SetEnabled(false, "at exit");

    private sealed class Pipeline
    {
        private const int Slots = 4;
        private readonly float[][] _requests = Enumerable.Range(0, Slots).Select(_ => new float[BlockFrames * 2]).ToArray();
        private readonly long[] _sequences = new long[Slots];
        private readonly float[] _pending = new float[BlockFrames * 2];
        private readonly AutoResetEvent _signal = new(false);
        private readonly IStemEngine _engine;
        private readonly Action<Pipeline, StemError, string> _failed;
        private int _read, _write, _pendingFrames;
        private long _firstResult = -1;
        private volatile bool _stopped;
        public volatile bool Ready;
        public StemTimeline Original { get; } = new();
        public StemTimeline Vocals { get; } = new();
        public StemBus Home { get; } = new();
        public StemBus Local { get; } = new();
        public long Position, MissedFrames, DroppedBlocks;
        public double InferenceMs;
        public Thread Thread { get; }
        public bool Idle => Volatile.Read(ref _read) == Volatile.Read(ref _write);

        public Pipeline(IStemEngine engine, Action<Pipeline, StemError, string> failed)
        {
            _engine = engine;
            _failed = failed;
            // Redirected anonymous pipes use blocking handles. A pool thread waiting on ReadExactlyAsync
            // can starve that same pool's IO continuations on two-core PCs/runners (2026-10-13).
            Thread = new Thread(Run) { IsBackground = true, Name = "Stem pipe worker" };
            Thread.Start();
        }

        /// <summary>
        /// Whether a missing result at <paramref name="at"/> is a missed frame: only from the first result on. Before it
        /// the model was loading, which the status says instead of counting thousands of missed frames.
        /// </summary>
        public bool Counts(long at)
        {
            long first = Volatile.Read(ref _firstResult);
            return first >= 0 && at >= first;
        }

        /// <summary>Audio thread: appends one input frame, queues each full block; returns the frame's number.</summary>
        public long Push(float left, float right)
        {
            long sequence = Position++;
            Original.WriteFrame(sequence, left, right);
            _pending[_pendingFrames * 2] = left; _pending[_pendingFrames * 2 + 1] = right;
            if (++_pendingFrames < BlockFrames) return sequence;
            _pendingFrames = 0;
            if (_stopped || !Ready) return sequence; // stopped, or the model is still loading: not a dropped block
            int write = _write;
            if (write - Volatile.Read(ref _read) >= Slots) { DroppedBlocks++; return sequence; }
            _pending.CopyTo(_requests[write % Slots], 0);
            _sequences[write % Slots] = Position - BlockFrames;
            Volatile.Write(ref _write, write + 1);
            _signal.Set();
            return sequence;
        }

        private void Run()
        {
            try
            {
                _engine.Start();
                if (_stopped) return;
                Ready = true;
                var vocals = new float[BlockFrames * 2];
                long previousEnd = -1;
                while (!_stopped)
                {
                    int read = _read;
                    if (read == Volatile.Read(ref _write)) { _signal.WaitOne(100); continue; }
                    long first = _sequences[read % Slots];
                    long began = Stopwatch.GetTimestamp();
                    _engine.Separate(first, _requests[read % Slots], vocals);
                    foreach (float value in vocals)
                        if (!float.IsFinite(value)) throw new StemModuleException(StemError.Failed, "the module sent non-finite audio");
                    // Every reset (the first block, or one after dropped blocks) has an invalid first hop. Never label
                    // that as a separated result.
                    long from = first == previousEnd ? first - Hop : first;
                    Vocals.Write(from, first == previousEnd ? vocals : vocals.AsSpan(Hop * 2));
                    if (Volatile.Read(ref _firstResult) < 0) Volatile.Write(ref _firstResult, from);
                    previousEnd = first + BlockFrames;
                    Volatile.Write(ref InferenceMs, Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                    Volatile.Write(ref _read, read + 1);
                }
            }
            catch (Exception ex) when (!_stopped)
            {
                _failed(this, ex is StemModuleException module ? module.Kind : StemError.Failed, ex.Message);
            }
            catch (Exception)
            {
                // Stopping kills the module, and its pipes throw. Exceptions on a dedicated thread must never escape.
            }
            finally
            {
                Ready = false;
                try { _engine.Dispose(); } catch (Exception) { }
            }
        }

        public void Stop()
        {
            _stopped = true; _signal.Set();
            try { _engine.Abort(); } catch (Exception) { }
        }
    }
}
