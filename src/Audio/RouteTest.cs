using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// `routetest`: RoutedCapture end to end, no speaker needed. Three quiet tones play from renamed copies
/// of this exe ("HomePod only" 1 kHz, "both" 1.5 kHz, "local" 2 kHz). The FIFO is read like the RTP
/// sender would; endpoint loopback stands in for what this PC plays. The default route is "local", so
/// no other running app is touched. All session volumes are restored afterwards.
/// </summary>
internal static class RouteTest
{
    private static readonly double[] Freqs = [1000, 1500, 2000];
    private static readonly string[] Names = ["hpc-tone-speaker", "hpc-tone-both", "hpc-tone-local"];

    public static int Run(string[] args)
    {
        if (!ProcessLoopback.IsSupported)
        {
            Console.WriteLine($"per-app routing needs Windows 10 2004 (build {ProcessLoopback.MinimumBuild}); this is {Environment.OSVersion.Version}");
            return 2;
        }
        string dir = Path.GetDirectoryName(Environment.ProcessPath)!;
        var copies = Names.Select(n => Path.Combine(dir, n + ".exe")).ToArray();
        var children = new List<Process>();
        var config = new AppConfig { RouteDefault = AudioRoute.Local };
        var routing = new AppRouting(config, save: () => { });
        var fifo = new AudioFifo(RtpSender.SampleRate, targetMs: 12, capMs: 46);
        RoutedCapture? capture = null;
        using var reader = new FifoReader(fifo);
        using var local = new LocalMonitor();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                File.Copy(Environment.ProcessPath!, copies[i], overwrite: true);
                // Through cmd so the tones are not in this process's tree (as unrelated apps would be).
                var p = Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c start \"\" \"{copies[i]}\" tone --freq {Freqs[i].ToString(CultureInfo.InvariantCulture)} --amp 0.05 --seconds 40")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                p.WaitForExit(3000);
            }
            Thread.Sleep(800);
            foreach (var n in Names) children.AddRange(Process.GetProcessesByName(n));
            Log.Info($"tones: {string.Join(", ", children.Select(c => $"{c.ProcessName}#{c.Id}"))}");
            if (children.Count != 3) throw new InvalidOperationException("tone children did not start");

            routing.Set(Names[0], AudioRoute.HomePod);
            routing.Set(Names[1], AudioRoute.Both);
            capture = new RoutedCapture(fifo, RtpSender.SampleRate, routing);
            var sw = Stopwatch.StartNew();
            capture.Start();
            reader.Start();
            local.Start();
            Thread.Sleep(2500);
            Report("routed (speaker=HomePod, both=Both, local=default Local)", reader, local, capture);
            LogVolumes(children);

            routing.Set(Names[0], AudioRoute.Local);
            Thread.Sleep(2000);
            Report("speaker switched to Local while running", reader, local, capture);
            LogVolumes(children);

            routing.Set(Names[0], AudioRoute.HomePod);
            Thread.Sleep(2000);
            Report("speaker back to HomePod", reader, local, capture);

            routing.Set(Names[0], null);
            routing.Set(Names[1], null);
            routing.Default = AudioRoute.HomePod;   // no rules left: whole output again (touches nothing)
            Thread.Sleep(2500);
            Report("no rules (whole output; other apps' audio may show up too)", reader, local, capture);
            LogVolumes(children);

            routing.Default = AudioRoute.Local;
            routing.Set(Names[0], AudioRoute.HomePod);
            Thread.Sleep(2500);
            Report("HomePod-only again", reader, local, capture);

            // Latency through the whole capture -> FIFO -> reader chain, routed vs whole output.
            foreach (var c in children) { try { c.Kill(); c.WaitForExit(2000); } catch { } }
            Thread.Sleep(300);
            var clicks = Process.Start(new ProcessStartInfo(copies[0], "tone --clicks --freq 2000 --amp 0.08 --seconds 16")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            children.Add(clicks);
            var onsets = new List<long>();
            clicks.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith("onset ") && long.TryParse(line[6..], out var t)) lock (onsets) onsets.Add(t);
            };
            clicks.BeginOutputReadLine();
            Thread.Sleep(1500);
            reader.Onsets.Clear();
            Thread.Sleep(6000);
            var routedLatency = Match(onsets, reader.Onsets.ToList());
            routing.Default = AudioRoute.HomePod;
            routing.Set(Names[0], null);
            Thread.Sleep(1500);
            reader.Onsets.Clear();
            int skip;
            lock (onsets) skip = onsets.Count;
            Thread.Sleep(6000);
            List<long> later;
            lock (onsets) later = onsets.Skip(skip).ToList();
            var wholeLatency = Match(later, reader.Onsets.ToList());
            Log.Info($"click -> read from FIFO (median, includes the 12 ms FIFO target): routed {Summary(routedLatency)}, " +
                     $"whole output {Summary(wholeLatency)}");
            capture.Dispose();
            capture = null;
            Thread.Sleep(1000);
            Report("after capture stopped", reader, local, null);
            LogVolumes(children);
            Log.Info($"fifo underruns={fifo.Underruns} overflows={fifo.Overflows} total {sw.Elapsed.TotalSeconds:F0} s");
        }
        finally
        {
            capture?.Dispose();
            foreach (var c in children)
            {
                try { if (!c.HasExited) c.Kill(); c.WaitForExit(2000); } catch { }
                c.Dispose();
            }
            foreach (var n in Names)
                foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); p.WaitForExit(2000); } catch { } p.Dispose(); }
            foreach (var f in copies) { try { File.Delete(f); } catch { } }
        }
        return 0;
    }

    private static void Report(string label, FifoReader reader, LocalMonitor local, RoutedCapture? capture)
    {
        var sent = reader.Take();
        var heard = local.Take();
        Log.Info($"{label}:\n" +
                 $"  to speaker (FIFO): {Describe(sent)}\n" +
                 $"  this PC (output):  {Describe(heard)}\n" +
                 (capture == null ? "" : $"  capture: {capture.DeviceName}, +{capture.ExtraLatencyMs} ms, drift {capture.DriftPpm:F0} ppm"));
    }

    private static List<double> Match(List<long> scheduled, List<long> detected)
    {
        var result = new List<double>();
        lock (scheduled)
            foreach (var t in scheduled)
            {
                var hit = detected.FirstOrDefault(d => d > t - Stopwatch.Frequency / 20 && d - t < Stopwatch.Frequency * 3 / 10);
                if (hit != 0) result.Add((hit - t) * 1000.0 / Stopwatch.Frequency);
            }
        return result;
    }

    private static string Summary(List<double> ms) => ms.Count == 0 ? "no clicks matched"
        : $"{ms.Order().ElementAt(ms.Count / 2):F1} ms ({ms.Count} clicks, {ms.Min():F1}-{ms.Max():F1})";

    private static string Describe(double[] amps) =>
        string.Join("  ", Freqs.Select((f, i) => $"{Names[i][9..]} {f:F0}Hz={amps[i]:F4}"));

    private static void LogVolumes(List<Process> children)
    {
        foreach (var app in AppAudio.Enumerate())
        {
            if (children.Any(c => c.Id == app.ProcessId))
                Log.Info($"  session {app.ExeKey}: raw volume {app.RawVolume:E2} (shown as {app.Volume:P0})");
            app.Dispose();
        }
    }

    /// <summary>Single-bin DFT amplitudes of the test tones over a window.</summary>
    private sealed class Tones(int rate)
    {
        private readonly double[] _re = new double[3], _im = new double[3];
        private long _n, _index;

        public void Add(ReadOnlySpan<float> stereo)
        {
            for (int i = 0; i < stereo.Length; i += 2, _index++, _n++)
                for (int k = 0; k < 3; k++)
                {
                    double ph = 2 * Math.PI * Freqs[k] * _index / rate;
                    _re[k] += stereo[i] * Math.Cos(ph);
                    _im[k] += stereo[i] * Math.Sin(ph);
                }
        }

        public double[] Amplitudes() => [.. Enumerable.Range(0, 3).Select(k => _n == 0 ? 0 : 2 * Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / _n)];
    }

    /// <summary>Consumes the FIFO in real time, 352 frames at a time, like RtpSender.</summary>
    private sealed class FifoReader(AudioFifo fifo) : IDisposable
    {
        private readonly object _lock = new();
        private Tones _tones = new(RtpSender.SampleRate);
        private volatile bool _stop;
        private Thread? _thread;

        /// <summary>QPC time at which each sound onset was read out of the FIFO.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<long> Onsets { get; } = new();

        public void Start()
        {
            _thread = new Thread(() =>
            {
                using var timer = new Native.PreciseTimer();
                var buf = new float[RtpSender.FramesPerPacket * 2];
                long start = Stopwatch.GetTimestamp(), quiet = 0;
                long n = 0;
                while (!_stop)
                {
                    long due = start + n * RtpSender.FramesPerPacket * Stopwatch.Frequency / RtpSender.SampleRate;
                    timer.Sleep(due - Stopwatch.GetTimestamp());
                    fifo.Read(buf);
                    long now = Stopwatch.GetTimestamp();
                    for (int i = 0; i < buf.Length; i += 2, quiet++)
                    {
                        if (Math.Abs(buf[i]) < 0.02f) continue;
                        if (quiet > RtpSender.SampleRate / 5) Onsets.Enqueue(now);
                        quiet = 0;
                    }
                    lock (_lock) _tones.Add(buf);
                    n++;
                }
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        /// <summary>Amplitudes since the last call, skipping the first 500 ms after a change.</summary>
        public double[] Take()
        {
            lock (_lock) _tones = new Tones(RtpSender.SampleRate);
            Thread.Sleep(1000);
            lock (_lock) return _tones.Amplitudes();
        }

        public void Dispose() => _stop = true;
    }

    /// <summary>What this PC plays: endpoint loopback (after session volume, before the endpoint volume).</summary>
    private sealed class LocalMonitor : IDisposable
    {
        private readonly object _lock = new();
        private Tones? _tones;
        private volatile bool _stop;
        private Thread? _thread;

        public void Start()
        {
            _thread = new Thread(() =>
            {
                using var ready = new AutoResetEvent(false);
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device), "device");
                using var stream = LoopbackStream.OpenEndpoint(device, ready.SafeWaitHandle.DangerousGetHandle(), 200_000);
                _rate = stream.Format.SampleRate;
                lock (_lock) _tones = new Tones(_rate);
                stream.Start();
                var buf = new List<float>();
                while (!_stop)
                {
                    ready.WaitOne(20);
                    buf.Clear();
                    stream.Drain(buf);
                    lock (_lock) _tones!.Add(CollectionsMarshal.AsSpan(buf));
                }
                Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            Thread.Sleep(200);
        }

        public double[] Take()
        {
            lock (_lock) _tones = new Tones(_rate);
            Thread.Sleep(1000);
            lock (_lock) return _tones.Amplitudes();
        }

        private volatile int _rate = 48000;

        public void Dispose() => _stop = true;
    }
}
