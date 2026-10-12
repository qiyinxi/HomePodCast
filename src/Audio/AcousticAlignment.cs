using System.Diagnostics;

namespace HomePodCast.Audio;

public readonly record struct AcousticMeasurement(double DelayMs, double Confidence);

/// <summary>Matched chirp correlation. Relative speaker delays cancel the common microphone/driver latency.</summary>
public static class AcousticAlignment
{
    public static float[] Chirp()
    {
        const int rate = 44100, length = rate / 10;
        var result = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)rate;
            double phase = 2 * Math.PI * (400 * t + 0.5 * 14000 * t * t); // 400 → 1800 Hz in 100 ms
            result[i] = (float)(0.1 * Math.Sin(phase) * Math.Pow(Math.Sin(Math.PI * i / (length - 1)), 2));
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
    public static int[] RelativeOffsets(IReadOnlyList<AcousticMeasurement> readings)
    {
        if (readings.Count < 2 || readings.Any(r => !double.IsFinite(r.DelayMs) || r.DelayMs is < 0 or > 1000 ||
            !double.IsFinite(r.Confidence) || r.Confidence is < 0.45 or > 1.00001)) throw new ArgumentException("Incomplete or unreliable acoustic measurements.");
        double slowest = readings.Max(r => r.DelayMs);
        var offsets = readings.Select(r => (int)Math.Round(slowest - r.DelayMs)).ToArray();
        if (offsets.Any(v => v > 500)) throw new ArgumentException("Speakers differ by over 500 ms; retry the calibration.");
        return offsets;
    }
}

/// <summary>Only activated by the calibration button. Never retains or writes microphone recordings.</summary>
internal static class CinemaAcousticCalibrator
{
    public static async Task<AcousticMeasurement[]> MeasureAsync(CinemaFileSource source, int count, string? micId,
        Func<bool> stillStreaming, CancellationToken cancellation)
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
            mic.CalibrationSamples += OnSamples; mic.Start();
            try
            {
                long waitUntil = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                while (!mic.Running)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stillStreaming() || mic.AccessDenied || Stopwatch.GetTimestamp() > waitUntil)
                        throw new IOException(mic.Error ?? "The microphone did not start.");
                    Thread.Sleep(10);
                }
                var measurements = new AcousticMeasurement[count];
                for (int index = 0; index < count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stillStreaming()) throw new IOException("The cinema stream changed during calibration.");
                    var recording = new float[44100 * 6 / 5];
                    source.BeginPulse(index);
                    Volatile.Write(ref lastFrame, 0);
                    Volatile.Write(ref activeRecording, recording);
                    long limit = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
                    while (Volatile.Read(ref lastFrame) < recording.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!stillStreaming() || !mic.Running || Stopwatch.GetTimestamp() > limit)
                            throw new IOException("Calibration audio stopped or timed out.");
                        Thread.Sleep(2);
                    }
                    Volatile.Write(ref activeRecording, null);
                    // Samples use device QPC time relative to the pulse's RTP source deadline, not polling time.
                    var reading = AcousticAlignment.Measure(recording);
                    Array.Clear(recording);
                    if (reading == null) throw new IOException("The test chirp was not detected reliably; previous delays are kept.");
                    measurements[index] = reading.Value;
                    Thread.Sleep(100); // let the short room tail settle before the next independent measurement
                }
                AcousticAlignment.RelativeOffsets(measurements); // all-or-nothing validation
                return measurements;
            }
            finally { Volatile.Write(ref activeRecording, null); mic.CalibrationSamples -= OnSamples; source.EndCalibration(); }
        }, cancellation);
    }
}
