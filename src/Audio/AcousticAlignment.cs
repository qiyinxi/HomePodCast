using System.Diagnostics;

namespace HomePodCast.Audio;

public readonly record struct AcousticMeasurement(double DelayMs, double Confidence);

/// <summary>Matched chirp correlation. Relative speaker delays cancel the common microphone/driver latency.</summary>
public static class AcousticAlignment
{
    /// <summary>Calibration explicitly uses the maximum app volume for its short isolated test tones.</summary>
    public const double MeasurementVolumeCapPercent = 100;

    /// <summary>A 100 ms 400 → 1800 Hz full-level sweep with a raised-sine envelope (no click at either end).</summary>
    public static float[] Chirp(int rate = 44100)
    {
        if (rate < 8000 || rate > 192000) throw new ArgumentOutOfRangeException(nameof(rate));
        int length = rate / 10;
        var result = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)rate;
            double phase = 2 * Math.PI * (400 * t + 0.5 * 14000 * t * t); // 400 → 1800 Hz in 100 ms
            result[i] = (float)(0.95 * Math.Sin(phase) * Math.Pow(Math.Sin(Math.PI * i / (length - 1)), 2));
        }
        return result;
    }

    public static AcousticMeasurement? Measure(ReadOnlySpan<float> recording, int rate = 44100)
    {
        if (rate != 44100) throw new ArgumentOutOfRangeException(nameof(rate));
        var chirp = Chirp(); const int stride = 4;
        double referenceEnergy = 0;
        for (int i = 0; i < chirp.Length; i += stride) referenceEnergy += chirp[i] * chirp[i];
        double best = 0; int bestAt = 0;
        int max = Math.Min(recording.Length - chirp.Length, rate); // an arrival later than 1 s is outside the calibration contract
        for (int lag = 0; lag <= max; lag += stride)
        {
            double dot = 0, energy = 0;
            for (int i = 0; i < chirp.Length; i += stride)
            { float v = recording[lag + i]; dot += v * chirp[i]; energy += v * v; }
            if (energy < 1e-7) continue;
            double score = Math.Abs(dot) / Math.Sqrt(referenceEnergy * energy);
            if (score > best) { best = score; bestAt = lag; }
        }
        return best >= 0.45 ? new(bestAt * 1000.0 / rate, best) : null;
    }

    /// <summary>The delay to give each speaker so that all arrive with the slowest one (which gets 0).</summary>
    /// <exception cref="CinemaException">A reading is missing or unreliable, or the speakers differ by more than 500 ms.</exception>
    public static int[] RelativeOffsets(IReadOnlyList<AcousticMeasurement> readings)
    {
        if (readings.Count < 2 || readings.Any(r => !double.IsFinite(r.DelayMs) || r.DelayMs is < 0 or > 1000 ||
            !double.IsFinite(r.Confidence) || r.Confidence is < 0.45 or > 1.00001))
            throw new CinemaException(L.T("有的音箱没有测到可靠的结果，原来的延迟没有改。"), "incomplete or unreliable acoustic measurements");
        double slowest = readings.Max(r => r.DelayMs);
        var offsets = readings.Select(r => (int)Math.Round(slowest - r.DelayMs)).ToArray();
        if (offsets.Any(v => v > 500))
            throw new CinemaException(L.T("各音箱测到的时间相差超过 500 毫秒，结果不可靠，原来的延迟没有改。"),
                $"speakers differ by {offsets.Max()} ms");
        return offsets;
    }
}

/// <summary>
/// Only activated by the calibration button. The recording is analysed in memory to find each chirp and then cleared;
/// it is never written or sent anywhere (PRIVACY.md says so).
/// </summary>
internal static class CinemaAcousticCalibrator
{
    /// <param name="names">Each speaker's name, in the source's speaker order (for the message when one is not heard).</param>
    /// <param name="stillPlaying">The cinema still streams this source (checked between and during the pulses).</param>
    /// <exception cref="CinemaException">The microphone, the stream or a reading failed; nothing is applied.</exception>
    public static async Task<AcousticMeasurement[]> MeasureAsync(CinemaFileSource source, IReadOnlyList<string> names, string? micId,
        Func<bool> stillPlaying, CancellationToken cancellation)
    {
        return await Task.Run(() =>
        {
            using var mic = new MicCapture(micId);
            float[]? activeRecording = null;
            int lastFrame = 0;
            void OnSamples(ReadOnlySpan<float> samples, long firstTicks)
            {
                var recording = Volatile.Read(ref activeRecording);
                long pulse = source.PulseStartedTicks;
                if (recording == null || pulse == 0 || firstTicks == 0) return;
                int first = (int)Math.Round((firstTicks - pulse) * 44100.0 / Stopwatch.Frequency);
                for (int f = 0; f < samples.Length / 2; f++)
                {
                    int at = first + f;
                    if (at >= 0 && at < recording.Length) recording[at] = samples[f * 2];
                }
                if (ReferenceEquals(Volatile.Read(ref activeRecording), recording) && source.PulseStartedTicks == pulse)
                    Volatile.Write(ref lastFrame, Math.Max(Volatile.Read(ref lastFrame), first + samples.Length / 2));
            }
            var interrupted = new CinemaException(L.T("测量中断了：影院播放停止了，或者连接变了。"), "the cinema stream ended during calibration");
            mic.CalibrationSamples += OnSamples; mic.Start();
            try
            {
                long waitUntil = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                while (!mic.Running)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stillPlaying()) throw interrupted;
                    if (mic.AccessDenied || mic.Error != null || Stopwatch.GetTimestamp() > waitUntil)
                        throw new CinemaException(L.T("麦克风打不开：请确认它已接好，并且 Windows 允许本应用使用麦克风。"),
                            $"microphone did not start: {(mic.AccessDenied ? "access denied" : mic.Error ?? "timeout")}");
                    Thread.Sleep(10);
                }
                Thread.Sleep(300); // the speakers' lowered measurement volume (set before this) is in effect by now
                var measurements = new AcousticMeasurement[names.Count];
                for (int index = 0; index < names.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stillPlaying()) throw interrupted;
                    var recording = new float[44100 * 6 / 5];
                    source.BeginPulse(index);
                    Volatile.Write(ref lastFrame, 0);
                    Volatile.Write(ref activeRecording, recording);
                    long limit = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
                    while (Volatile.Read(ref lastFrame) < recording.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!stillPlaying()) throw interrupted;
                        if (!mic.Running || Stopwatch.GetTimestamp() > limit)
                            throw new CinemaException(L.T("测量超时：没有及时收到麦克风的声音。"),
                                $"no microphone audio for speaker {index} ({(mic.Running ? "timeout" : "the microphone stopped")})");
                        Thread.Sleep(2);
                    }
                    Volatile.Write(ref activeRecording, null);
                    // Samples use device QPC time relative to the pulse's RTP source deadline, not polling time.
                    var reading = AcousticAlignment.Measure(recording);
                    Array.Clear(recording);
                    measurements[index] = reading ?? throw new CinemaException(
                        L.F("没有可靠地听到「{0}」的测试音：把麦克风放近一些，或者把音量调大一点再试。原来的延迟没有改。", names[index]),
                        $"chirp of speaker {index} not detected");
                    Thread.Sleep(100); // let the short room tail settle before the next independent measurement
                }
                AcousticAlignment.RelativeOffsets(measurements); // all-or-nothing validation
                return measurements;
            }
            finally { Volatile.Write(ref activeRecording, null); mic.CalibrationSamples -= OnSamples; source.EndCalibration(); }
        }, cancellation);
    }
}

/// <summary>Measures each currently streaming AirPlay 2 output in isolation; samples are transient and memory-only.</summary>
internal static class LiveGroupAcousticCalibrator
{
    public static async Task<AcousticMeasurement[]> MeasureAsync(IGroupAcousticCalibration source,
        IReadOnlyList<(string Id, string Name)> targets, Func<bool> stillPlaying, CancellationToken cancellation,
        string? micId = null, IProgress<string>? progress = null)
    {
        if (!source.CanCalibrateAcoustically || targets.Count < 2)
            throw new InvalidOperationException("The active output group cannot be microphone-calibrated");
        return await Task.Run(() =>
        {
            using var mic = new MicCapture(micId);
            float[]? activeRecording = null;
            long pulseTicks = 0;
            int lastFrame = 0;
            void OnSamples(ReadOnlySpan<float> samples, long firstTicks)
            {
                var recording = Volatile.Read(ref activeRecording);
                long pulse = Volatile.Read(ref pulseTicks);
                if (recording == null || pulse == 0 || firstTicks == 0) return;
                int first = (int)Math.Round((firstTicks - pulse) * (double)MicCapture.OutputRate / Stopwatch.Frequency);
                for (int f = 0; f < samples.Length / 2; f++)
                {
                    int at = first + f;
                    if ((uint)at < (uint)recording.Length) recording[at] = samples[f * 2];
                }
                if (ReferenceEquals(Volatile.Read(ref activeRecording), recording) && Volatile.Read(ref pulseTicks) == pulse)
                    Volatile.Write(ref lastFrame, Math.Max(Volatile.Read(ref lastFrame), first + samples.Length / 2));
            }
            mic.CalibrationSamples += OnSamples;
            mic.Start();
            try
            {
                long readyBy = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                while (!mic.Running)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stillPlaying()) throw new OperationCanceledException("The output group stopped during calibration");
                    if (mic.AccessDenied || mic.Error != null || Stopwatch.GetTimestamp() > readyBy)
                        throw new CinemaException(L.T("麦克风打不开：请确认它已接好，并且 Windows 允许本应用使用麦克风。"),
                            $"microphone did not start: {(mic.AccessDenied ? "access denied" : mic.Error ?? "timeout")}");
                    Thread.Sleep(10);
                }

                var readings = new AcousticMeasurement[targets.Count];
                for (int index = 0; index < targets.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stillPlaying()) throw new OperationCanceledException("The output group changed");
                    progress?.Report(L.F("正在单独播放「{0}」的最大音量测试音（{1}/{2}）…", targets[index].Name, index + 1, targets.Count));
                    var recording = new float[MicCapture.OutputRate * 6 / 5];
                    Volatile.Write(ref lastFrame, 0);
                    Volatile.Write(ref activeRecording, recording);
                    long start = source.BeginAcousticPulse(targets[index].Id);
                    Volatile.Write(ref pulseTicks, start);
                    long limit = Stopwatch.GetTimestamp() + 4 * Stopwatch.Frequency;
                    while (Volatile.Read(ref lastFrame) < recording.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!stillPlaying()) throw new OperationCanceledException("The output group changed");
                        if (!mic.Running || Stopwatch.GetTimestamp() > limit)
                            throw new CinemaException(L.T("测量超时：没有及时收到麦克风的声音。"),
                                $"no microphone audio for output {index} ({(mic.Running ? "timeout" : "the microphone stopped")})");
                        Thread.Sleep(2);
                    }
                    Volatile.Write(ref activeRecording, null);
                    var reading = AcousticAlignment.Measure(recording);
                    Array.Clear(recording);
                    readings[index] = reading ?? throw new CinemaException(
                        L.F("没有可靠地听到「{0}」的测试音：把麦克风放近一些，或者把音量调大一点再试。原来的延迟没有改。", targets[index].Name),
                        $"chirp of output {index} not detected");
                    progress?.Report(L.F("已听到「{0}」：{1:F1} 毫秒", targets[index].Name, readings[index].DelayMs));
                    source.EndAcousticCalibration();
                    Volatile.Write(ref pulseTicks, 0);
                    Thread.Sleep(150);
                }
                AcousticAlignment.RelativeOffsets(readings);
                return readings;
            }
            finally
            {
                Volatile.Write(ref activeRecording, null);
                Volatile.Write(ref pulseTicks, 0);
                mic.CalibrationSamples -= OnSamples;
                source.EndAcousticCalibration();
            }
        }, cancellation);
    }
}

/// <summary>Measures one output played by the normal Windows audio path, with the microphone wherever it can hear it.</summary>
internal static class HomeAcousticCalibrator
{
    public static async Task<AcousticMeasurement> MeasureOutputAsync(string? outputEndpointId, CancellationToken cancellation)
    {
        return await Task.Run(() =>
        {
            using var mic = new MicCapture();
            float[]? recording = null;
            long pulseTicks = 0;
            int lastFrame = 0;
            string? renderError = null;
            void OnSamples(ReadOnlySpan<float> samples, long firstTicks)
            {
                var active = Volatile.Read(ref recording);
                long pulse = Volatile.Read(ref pulseTicks);
                if (active == null || pulse == 0 || firstTicks == 0) return;
                int first = (int)Math.Round((firstTicks - pulse) * (double)MicCapture.OutputRate / Stopwatch.Frequency);
                for (int frame = 0; frame < samples.Length / 2; frame++)
                {
                    int at = first + frame;
                    if ((uint)at < (uint)active.Length) active[at] = samples[frame * 2];
                }
                if (ReferenceEquals(Volatile.Read(ref recording), active) && Volatile.Read(ref pulseTicks) == pulse)
                    Volatile.Write(ref lastFrame, Math.Max(Volatile.Read(ref lastFrame), first + samples.Length / 2));
            }
            mic.CalibrationSamples += OnSamples;
            mic.Start();
            try
            {
                long readyBy = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                while (!mic.Running)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (mic.AccessDenied || mic.Error != null || Stopwatch.GetTimestamp() > readyBy)
                        throw new CinemaException(L.T("麦克风打不开：请确认它已接好，并且 Windows 允许本应用使用麦克风。"),
                            $"microphone did not start: {(mic.AccessDenied ? "access denied" : mic.Error ?? "timeout")}");
                    Thread.Sleep(10);
                }

                var samples = new float[MicCapture.OutputRate * 6 / 5];
                Volatile.Write(ref recording, samples);
                using var scheduled = new ManualResetEventSlim();
                using var renderer = new ClickRenderer(outputEndpointId, calibrationChirp: true);
                renderer.ClickScheduled += ticks => { Volatile.Write(ref pulseTicks, ticks); scheduled.Set(); };
                renderer.Failed += message => Volatile.Write(ref renderError, message);
                renderer.Start();
                long deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                while (Volatile.Read(ref lastFrame) < samples.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref renderError) is { } error)
                        throw new CinemaException(L.T("无法播放测试音，请检查本机输出设备后重试。"), $"chirp renderer failed: {error}");
                    if (Stopwatch.GetTimestamp() > deadline)
                        throw new CinemaException(L.T("测量超时：没有及时收到麦克风的声音。"), "home acoustic measurement timed out");
                    Thread.Sleep(2);
                }
                if (!scheduled.IsSet) throw new CinemaException(L.T("没有播放测试音，请重试。"), "calibration chirp was not scheduled");
                Volatile.Write(ref recording, null);
                var result = AcousticAlignment.Measure(samples)
                    ?? throw new CinemaException(L.T("没有检测到可靠的测试音：请确认麦克风能听到音箱，再重试。"), "calibration chirp not detected");
                Array.Clear(samples);
                return result;
            }
            finally
            {
                Volatile.Write(ref recording, null);
                mic.CalibrationSamples -= OnSamples;
            }
        }, cancellation);
    }
}
