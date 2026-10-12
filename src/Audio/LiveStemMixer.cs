using System.Diagnostics;
using System.Runtime.InteropServices;

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

/// <summary>
/// A music-only experimental effect BEFORE mic mix-in/EQ/compression. The sender only copies into a bounded SPSC
/// queue and reads tagged results; ONNX and pipe IO run in a separate optional process and on a worker thread.
/// Missing vocals mute an unequal-gain mix, never leak the original into a karaoke/isolated-vocal destination.
/// </summary>
public sealed class LiveStemMixer : IAudioEffect, ITapSource, IDisposable
{
    public const int DelayFrames = 8192;
    public const int ExtraLatencyMs = (DelayFrames * 1000 + 44099) / 44100;
    private volatile Pipeline? _pipeline;
    private volatile AudioTap[] _taps = [];
    private readonly object _tapLock = new();
    private LocalMonitor? _monitor;
    private bool _monitorWanted;
    private volatile bool _monitorBlocked;
    private volatile string? _captured;
    private float _homeVocal, _homeMusic = 1, _localVocal = 1, _localMusic;
    private int _monitorDelayMs;

    public StemSettings Settings { get; }
    public bool Enabled => _pipeline != null;
    public bool Ready => _pipeline?.Ready == true;
    public string? Error => _pipeline?.Error;
    public long MissedFrames => _pipeline?.MissedFrames ?? 0;
    public long DroppedBlocks => _pipeline?.DroppedBlocks ?? 0;
    public double InferenceMs => _pipeline?.InferenceMs ?? 0;
    public bool MonitorOn => _monitorWanted;
    public bool MonitorBlocked => _monitorBlocked;
    public string? MonitorError => _monitor?.Error;
    public int Rate => 44100;
    public double LatencyMs => Enabled ? DelayFrames * 1000.0 / Rate + _monitorDelayMs : 0;
    public double ChunkMs => 352 * 1000.0 / Rate;
    public int MonitorDelayMs { set => Volatile.Write(ref _monitorDelayMs, Math.Clamp(value, 0, 500)); }

    public LiveStemMixer(StemSettings settings) { Settings = settings; ApplyGains(); }
    public void ApplyGains()
    {
        Volatile.Write(ref _homeVocal, StemMix.Gain(Settings.HomePodVocals));
        Volatile.Write(ref _homeMusic, StemMix.Gain(Settings.HomePodAccompaniment));
        Volatile.Write(ref _localVocal, StemMix.Gain(Settings.MonitorVocals));
        Volatile.Write(ref _localMusic, StemMix.Gain(Settings.MonitorAccompaniment));
    }

    public static bool ModuleExists(string? directory) => !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, "HomePodCast.StemWorker.exe")) && File.Exists(Path.Combine(directory, "vocals.onnx"));

    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled) return;
        if (enabled)
        {
            if (!ModuleExists(Settings.ModuleDirectory)) throw new FileNotFoundException("Select an installed stem module first.");
            _pipeline = new Pipeline(Settings.ModuleDirectory!);
        }
        else
        {
            Interlocked.Exchange(ref _pipeline, null)?.Stop();
            SetMonitor(false);
        }
    }

    public void SetCaptured(string? endpoint)
    {
        _captured = endpoint;
        bool blocked = _monitorWanted && endpoint != null &&
            CaptureEndpoint.Same(Settings.MonitorDeviceId ?? AudioEndpoints.DefaultOutputId(), endpoint);
        if (blocked != _monitorBlocked) SetMonitor(_monitorWanted);
    }
    public void SetMonitor(bool enabled)
    {
        _monitor?.Dispose(); _monitor = null;
        _monitorWanted = enabled && Enabled;
        _monitorBlocked = _monitorWanted && _captured != null &&
            CaptureEndpoint.Same(Settings.MonitorDeviceId ?? AudioEndpoints.DefaultOutputId(), _captured);
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
        var pipeline = _pipeline;
        if (pipeline == null) return;
        frames = Math.Min(frames, interleaved.Length / 2);
        float hv = Volatile.Read(ref _homeVocal), hm = Volatile.Read(ref _homeMusic);
        float lv = Volatile.Read(ref _localVocal), lm = Volatile.Read(ref _localMusic);
        int localDelay = Volatile.Read(ref _monitorDelayMs) * Rate / 1000;
        var taps = _taps;
        // The sender's packet has a fixed 352-frame maximum. Larger standalone calls are split below.
        Span<float> local = stackalloc float[704];
        for (int at = 0; at < frames; at += 352)
        {
            int take = Math.Min(352, frames - at);
            for (int f = 0; f < take; f++)
            {
                int i = (at + f) * 2;
                long sequence = pipeline.Position;
                pipeline.Push(interleaved[i], interleaved[i + 1]);
                long homeAt = sequence - DelayFrames;
                pipeline.Original.Read(homeAt, out float left, out float right);
                bool available = pipeline.Vocals.Read(homeAt, out float vl, out float vr);
                if (!available && homeAt >= 0 && hv != hm) pipeline.MissedFrames++;
                // Gain slew and availability envelope avoid clicks when sliders or deadlines change.
                pipeline.HomeVocal += Math.Clamp(hv - pipeline.HomeVocal, -1f / 441, 1f / 441);
                pipeline.HomeMusic += Math.Clamp(hm - pipeline.HomeMusic, -1f / 441, 1f / 441);
                float allowed = available || hv == hm ? 1 : 0;
                pipeline.HomeEnvelope += Math.Clamp(allowed - pipeline.HomeEnvelope, -1f / 220, 1f / 220);
                bool safe = available || pipeline.HomeVocal == pipeline.HomeMusic;
                interleaved[i] = safe ? StemMix.Sample(left, vl, pipeline.HomeVocal, pipeline.HomeMusic) * pipeline.HomeEnvelope : 0;
                interleaved[i + 1] = safe ? StemMix.Sample(right, vr, pipeline.HomeVocal, pipeline.HomeMusic) * pipeline.HomeEnvelope : 0;
                long localAt = homeAt - localDelay;
                pipeline.Original.Read(localAt, out left, out right);
                available = pipeline.Vocals.Read(localAt, out vl, out vr);
                // Local isolated stems fail closed too; headphones are approximately aligned to AirPlay playout.
                local[f * 2] = available || lv == lm ? StemMix.Sample(left, vl, lv, lm) : 0;
                local[f * 2 + 1] = available || lv == lm ? StemMix.Sample(right, vr, lv, lm) : 0;
            }
            foreach (var tap in taps) tap.Write(local[..(take * 2)]);
        }
    }

    public void Dispose() { SetEnabled(false); }

    private sealed class Pipeline
    {
        private const int BlockFrames = 1024, Slots = 4;
        private readonly float[][] _requests = Enumerable.Range(0, Slots).Select(_ => new float[BlockFrames * 2]).ToArray();
        private readonly long[] _sequences = new long[Slots];
        private readonly float[] _pending = new float[BlockFrames * 2];
        private readonly AutoResetEvent _signal = new(false);
        private Process? _process;
        private int _read, _write, _pendingFrames;
        private volatile bool _stopped;
        public volatile bool Ready;
        public volatile string? Error;
        public StemTimeline Original { get; } = new();
        public StemTimeline Vocals { get; } = new();
        public long Position, MissedFrames, DroppedBlocks;
        public float HomeVocal, HomeMusic, HomeEnvelope;
        public double InferenceMs;

        public Pipeline(string directory)
        {
            // Redirected anonymous pipes use blocking handles. A pool thread waiting on ReadExactlyAsync
            // can starve that same pool's IO continuations on two-core PCs/runners (2026-10-13).
            new Thread(() => Run(directory)) { IsBackground = true, Name = "Stem pipe worker" }.Start();
        }
        public void Push(float left, float right)
        {
            Span<float> pair = stackalloc float[2] { left, right };
            Original.Write(Position++, pair);
            _pending[_pendingFrames * 2] = left; _pending[_pendingFrames * 2 + 1] = right;
            if (++_pendingFrames < BlockFrames) return;
            _pendingFrames = 0;
            int write = _write;
            if (_stopped || !Ready || write - Volatile.Read(ref _read) >= Slots) { DroppedBlocks++; return; }
            _pending.CopyTo(_requests[write % Slots], 0);
            _sequences[write % Slots] = Position - BlockFrames;
            Volatile.Write(ref _write, write + 1);
            _signal.Set();
        }

        private void Run(string directory)
        {
            try
            {
                var start = new ProcessStartInfo(Path.Combine(directory, "HomePodCast.StemWorker.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
                };
                var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start the stem module.");
                _process = process;
                if (_stopped) { process.Kill(true); return; }
                // Drain stderr concurrently: an error must never fill the pipe and deadlock inference.
                var errors = process.StandardError.ReadToEndAsync();
                // Killing the child closes its pipe ends and unblocks a stalled synchronous read/write.
                using var deadline = new System.Threading.Timer(_ =>
                {
                    Error = "Stem module response timed out.";
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                }, null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
                var header = new byte[16];
                process.StandardOutput.BaseStream.ReadExactly(header);
                if (BitConverter.ToInt32(header, 0) != 0x31535448 || BitConverter.ToInt32(header, 4) != BlockFrames ||
                    BitConverter.ToInt32(header, 8) != 128 || BitConverter.ToInt32(header, 12) != 44100)
                    throw new InvalidDataException("Incompatible stem module protocol.");
                Ready = true;
                deadline.Change(Timeout.Infinite, Timeout.Infinite);
                var input = process.StandardInput.BaseStream; var output = process.StandardOutput.BaseStream;
                var response = new byte[8 + BlockFrames * 8]; var request = new byte[8 + BlockFrames * 8];
                long previousEnd = -1;
                while (!_stopped)
                {
                    int read = _read;
                    if (read == Volatile.Read(ref _write)) { _signal.WaitOne(100); continue; }
                    long first = _sequences[read % Slots];
                    BitConverter.TryWriteBytes(request, first);
                    MemoryMarshal.AsBytes(_requests[read % Slots].AsSpan()).CopyTo(request.AsSpan(8));
                    long began = Stopwatch.GetTimestamp();
                    deadline.Change(2000, Timeout.Infinite);
                    input.Write(request); input.Flush();
                    output.ReadExactly(response);
                    deadline.Change(Timeout.Infinite, Timeout.Infinite);
                    if (BitConverter.ToInt64(response, 0) != first - 128) throw new InvalidDataException("Stem output lost input alignment.");
                    var vocals = MemoryMarshal.Cast<byte, float>(response.AsSpan(8));
                    foreach (float value in vocals) if (!float.IsFinite(value)) throw new InvalidDataException("Invalid stem audio.");
                    // Every reset has an invalid first hop. Never label that as a separated result.
                    if (first == previousEnd) Vocals.Write(first - 128, vocals);
                    else Vocals.Write(first, vocals[256..]);
                    previousEnd = first + BlockFrames;
                    Volatile.Write(ref InferenceMs, Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                    Volatile.Write(ref _read, read + 1);
                }
            }
            catch (Exception ex) when (!_stopped)
            {
                Error ??= ex.Message; Log.Warn($"stem module: {Error}");
            }
            finally
            {
                Ready = false;
                try { if (_process is { HasExited: false } p) p.Kill(true); } catch { }
                _process?.Dispose();
            }
        }
        public void Stop()
        {
            _stopped = true; _signal.Set();
            try { if (_process is { HasExited: false } p) p.Kill(true); } catch { }
        }
    }
}
