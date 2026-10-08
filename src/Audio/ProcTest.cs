using System.Diagnostics;
using System.Runtime.InteropServices;
using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// `proctest`: what does process loopback capture, and what does it cost in latency, compared with
/// endpoint loopback? Child processes (`tone`) play quiet test tones; the parent captures them with
/// endpoint loopback and with process loopback (include / exclude the child's tree) while it mutes
/// or turns down the child's session and the endpoint. Every changed state is restored.
/// </summary>
internal static class ProcTest
{
    private const double FreqA = 1000, FreqB = 1500;
    private const float ToneAmp = 0.05f;           // -26 dBFS: quiet
    private const long BufferDuration = 200_000;   // 20 ms, as in LoopbackCapture

    public static int Run(string[] args)
    {
        if (!ProcessLoopback.IsSupported)
        {
            Console.WriteLine($"process loopback needs Windows 10 2004 (build {ProcessLoopback.MinimumBuild}) or later; " +
                              $"this is {Environment.OSVersion.Version}");
            return 2;
        }

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device), "device");
        var iid = typeof(IAudioEndpointVolume).GUID;
        CoreAudio.Check(device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "endpoint volume");
        var endpoint = (IAudioEndpointVolume)obj;
        var ctx = Guid.Empty;
        endpoint.GetMute(out bool origMute);
        endpoint.GetMasterVolumeLevelScalar(out float origVol);
        Log.Info($"device \"{CoreAudio.FriendlyName(device)}\": muted={origMute} volume={origVol:P0}");

        var children = new List<Process>();
        AppAudio? sessionA = null;
        bool? origSessionMute = null;
        float origSessionVol = 1;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var a = StartTone(children, string.Format(inv, "--freq {0} --amp {1} --seconds 60", FreqA, ToneAmp));
            var b = StartTone(children, string.Format(inv, "--freq {0} --amp {1} --seconds 60", FreqB, ToneAmp));
            sessionA = WaitForSession((uint)a.Id);
            using var sessionB = WaitForSession((uint)b.Id);
            if (sessionA == null || sessionB == null) throw new InvalidOperationException("child sessions did not appear");
            origSessionMute = sessionA.Muted;
            origSessionVol = sessionA.Volume;

            using var rig = new Rig();
            rig.Open("endpoint", null, true, 0);
            rig.Open("include A", (uint)a.Id, true, RtpSender.SampleRate);
            rig.Open("exclude A", (uint)a.Id, false, RtpSender.SampleRate);
            rig.Open("include A 48k", (uint)a.Id, true, 48000);
            rig.Start();

            endpoint.SetMute(false, ref ctx);
            endpoint.SetMasterVolumeLevelScalar(Math.Max(origVol, 0.1f), ref ctx);
            var rows = new List<(string, Dictionary<string, Accum>)>();
            rows.Add(("normal", rig.Measure()));
            Log.Info(PacketReport(rows[0].Item2));

            sessionA.Muted = true;
            rows.Add(("A session muted", rig.Measure()));
            sessionA.Muted = false;
            sessionA.Volume = 0f;
            rows.Add(("A session vol 0", rig.Measure()));
            sessionA.Volume = 0.5f;
            rows.Add(("A session vol 50%", rig.Measure()));

            // Can a tiny session volume (inaudible locally) be undone by gain on the captured float data?
            foreach (float tiny in new[] { 1e-3f, 1e-4f, 1e-5f, 1e-6f })
            {
                sessionA.Volume = tiny;
                float readBack = sessionA.Volume;
                var m = rig.Measure(300, 1000);
                Log.Info($"A session vol {tiny:E0} (reads back {readBack:E3}): " + string.Join(", ", m.Select(kv =>
                    $"{kv.Key} {kv.Value.Amplitude(0):E3} (x1/vol = {kv.Value.Amplitude(0) / tiny:F4})")));
            }
            sessionA.Volume = origSessionVol;
            sessionA.Muted = origSessionMute.Value;

            endpoint.SetMute(true, ref ctx);
            rows.Add(("endpoint muted", rig.Measure()));
            endpoint.SetMute(false, ref ctx);
            endpoint.SetMasterVolumeLevelScalar(0f, ref ctx);
            rows.Add(("endpoint 0%", rig.Measure()));
            endpoint.SetMasterVolumeLevelScalar(origVol, ref ctx);
            endpoint.SetMute(origMute, ref ctx);

            PrintTable(rows, rig.Names);
            foreach (var app in AppAudio.Enumerate())
            {
                Log.Info($"session: pid={app.ProcessId} system={app.IsSystemSounds} name={app.Name}");
                app.Dispose();
            }

            // What does a process stream deliver when its target is silent, idle or gone?
            var c = StartTone(children, "--amp 0 --seconds 30");          // stream running, all zeros
            var d = StartTone(children, "--idle --seconds 30");           // process without any audio stream
            using (WaitForSession((uint)c.Id)) { }
            rig.Open("include C (zeros)", (uint)c.Id, true, RtpSender.SampleRate, start: true);
            rig.Open("include D (no stream)", (uint)d.Id, true, RtpSender.SampleRate, start: true);
            Thread.Sleep(300);
            Log.Info("idle targets:\n" + PacketReport(rig.Measure()));
            a.Kill();
            a.WaitForExit(2000);
            Thread.Sleep(300);
            Log.Info("after child A exited:\n" + PacketReport(rig.Measure()));
            b.Kill();
            b.WaitForExit(2000);

            // Latency: the click child prints the QPC time each burst starts playing; compare with when
            // each capture stream delivers it.
            rig.Dispose();
            using var clickRig = new Rig();
            var clicks = StartTone(children, "--clicks --freq 2000 --amp 0.08 --seconds 10", redirect: true);
            var onsets = new List<long>();
            clicks.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith("onset ") && long.TryParse(line[6..], out var t))
                    lock (onsets) onsets.Add(t);
            };
            clicks.BeginOutputReadLine();
            using (WaitForSession((uint)clicks.Id)) { }
            uint cp = (uint)clicks.Id;
            clickRig.Open("endpoint", null, true, 0);
            clickRig.Open("include 44.1k 20ms", cp, true, RtpSender.SampleRate);
            clickRig.Open("include 44.1k 0ms", cp, true, RtpSender.SampleRate, buffer: 0);
            clickRig.Open("include 44.1k 100ms", cp, true, RtpSender.SampleRate, buffer: 1_000_000);
            clickRig.Open("include 48k 20ms", cp, true, 48000);
            clickRig.Open("include 48k raw", cp, true, 48000, autoConvert: false);
            clickRig.Open("exclude 44.1k", cp, false, RtpSender.SampleRate);
            clickRig.DetectOnsets = true;
            clickRig.Start();
            var clickStats = clickRig.Measure(1000, 7000);
            clickRig.Dispose();
            Log.Info("click streams:\n" + PacketReport(clickStats));
            List<long> scheduled;
            lock (onsets) scheduled = [.. onsets];
            Log.Info(LatencyReport(scheduled, clickRig));
        }
        finally
        {
            if (sessionA != null && origSessionMute is { } m)
            {
                try { sessionA.Volume = origSessionVol; sessionA.Muted = m; } catch { }
                sessionA.Dispose();
            }
            endpoint.SetMasterVolumeLevelScalar(origVol, ref ctx);
            endpoint.SetMute(origMute, ref ctx);
            endpoint.GetMute(out bool em);
            endpoint.GetMasterVolumeLevelScalar(out float ev);
            Log.Info($"restored: muted={em} volume={ev:P0}");
            foreach (var p in children)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                p.Dispose();
            }
            Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        return 0;
    }

    private static Process StartTone(List<Process> children, string args, bool redirect = false)
    {
        var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tone " + args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
        })!;
        children.Add(p);
        return p;
    }

    private static AppAudio? WaitForSession(uint pid)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            AppAudio? found = null;
            foreach (var app in AppAudio.Enumerate())
            {
                if (found == null && app.ProcessId == pid && !app.IsSystemSounds) found = app;
                else app.Dispose();
            }
            if (found != null) return found;
            Thread.Sleep(100);
        }
        return null;
    }

    private static void PrintTable(List<(string State, Dictionary<string, Accum> Values)> rows, IReadOnlyList<string> names)
    {
        var lines = new List<string> { $"{"state",-18}" + string.Concat(names.Select(n => $" | {n,-20}")) };
        lines.Add($"{"",-18}" + string.Concat(names.Select(_ => $" | {"A 1kHz   B 1.5kHz",-20}")));
        foreach (var (state, values) in rows)
            lines.Add($"{state,-18}" + string.Concat(names.Select(n =>
                values.TryGetValue(n, out var v) ? $" | {v.Amplitude(0),6:F4}   {v.Amplitude(1),6:F4}     " : $" | {"-",-20}")));
        lines.Add("tone A per 100 ms block (x1000), endpoint / include A:");
        foreach (var (state, values) in rows)
            lines.Add($"  {state,-18} " + string.Join(" / ", names.Take(2).Select(n =>
                values.TryGetValue(n, out var v) ? string.Join(" ", v.Blocks.Select(x => (x * 1000).ToString("F0"))) : "-")));
        Log.Info($"tone amplitude (each child plays {ToneAmp}):\n" + string.Join("\n", lines));
    }

    private static string PacketReport(Dictionary<string, Accum> values) => string.Join("\n", values.Select(kv =>
    {
        var a = kv.Value;
        double seconds = a.WindowSeconds;
        return $"  {kv.Key,-22} {a.Packets / seconds,6:F1} packets/s, frames/packet {a.MinFrames}-{a.MaxFrames} " +
               $"@ {a.Rate} Hz ({a.MaxFrames * 1000.0 / Math.Max(1, a.Rate):F1} ms), silent-flag {a.SilentPackets}, " +
               $"mean wake interval {a.MeanInterval:F1} ms (max {a.MaxInterval:F1}), " +
               $"age of newest frame at read {a.MeanAge:F1} ms";
    }));

    private static string LatencyReport(List<long> scheduled, Rig rig)
    {
        var lines = new List<string> { $"click latency over {scheduled.Count} clicks (capture available - scheduled play time):" };
        foreach (var name in rig.Names)
        {
            var detected = rig.Onsets(name);
            var arrival = new List<double>();
            var stamp = new List<double>();
            foreach (var t in scheduled)
            {
                var hit = detected.FirstOrDefault(o => o.Arrival > t - Stopwatch.Frequency / 20 && o.Arrival - t < Stopwatch.Frequency * 3 / 10);
                if (hit.Arrival == 0) continue;
                arrival.Add((hit.Arrival - t) * 1000.0 / Stopwatch.Frequency);
                stamp.Add((hit.Stamp - t) * 1000.0 / Stopwatch.Frequency);
            }
            lines.Add(arrival.Count == 0
                ? $"  {name,-20} no clicks detected ({detected.Count} onsets)"
                : $"  {name,-20} {arrival.Count} hits: arrival median {Median(arrival):F1} ms " +
                  $"(min {arrival.Min():F1}, max {arrival.Max():F1}); device timestamp median {Median(stamp):F1} ms");
        }
        return string.Join("\n", lines);
    }

    private static double Median(List<double> v) => v.Order().ElementAt(v.Count / 2);

    /// <summary>Per-window statistics of one stream.</summary>
    private sealed class Accum(int rate)
    {
        private readonly double[] _re = new double[2], _im = new double[2];
        private long _n;
        private long _lastArrival;
        private double _ageSum;
        private int _ages, _intervals;
        private double _intervalSum;
        private readonly long _start = Stopwatch.GetTimestamp();

        public int Rate => rate;
        public long Packets, SilentPackets;
        public uint MinFrames = uint.MaxValue, MaxFrames;
        public double MaxInterval;
        public double WindowSeconds => Math.Max(1e-3, (Stopwatch.GetTimestamp() - _start) / (double)Stopwatch.Frequency);
        public double MeanAge => _ages == 0 ? double.NaN : _ageSum / _ages;
        public double MeanInterval => _intervals == 0 ? double.NaN : _intervalSum / _intervals;

        public double Amplitude(int i) => _n == 0 ? 0 : 2 * Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) / _n;

        public void Packet(uint frames, uint flags, ulong qpc100ns, long arrival)
        {
            Packets++;
            if ((flags & CoreAudio.BufferFlagsSilent) != 0) SilentPackets++;
            MinFrames = Math.Min(MinFrames, frames);
            MaxFrames = Math.Max(MaxFrames, frames);
            if (qpc100ns != 0)
            {
                double arrival100 = arrival * (1e7 / Stopwatch.Frequency);
                _ageSum += (arrival100 - qpc100ns) / 1e4 - frames * 1000.0 / rate;
                _ages++;
            }
        }

        public void Wake(long now)
        {
            if (_lastArrival != 0)
            {
                double ms = (now - _lastArrival) * 1000.0 / Stopwatch.Frequency;
                _intervalSum += ms;
                _intervals++;
                MaxInterval = Math.Max(MaxInterval, ms);
            }
            _lastArrival = now;
        }

        /// <summary>Amplitude of tone A per 100 ms block, to see ramps inside the window.</summary>
        public readonly List<double> Blocks = [];
        private double _bre, _bim;
        private int _bn;

        public void Samples(List<float> stereo, ref long index)
        {
            for (int i = 0; i < stereo.Count; i += 2, index++)
            {
                double x = stereo[i];
                for (int k = 0; k < 2; k++)
                {
                    double ph = 2 * Math.PI * (k == 0 ? FreqA : FreqB) * index / rate;
                    _re[k] += x * Math.Cos(ph);
                    _im[k] += x * Math.Sin(ph);
                    if (k == 0) { _bre += x * Math.Cos(ph); _bim += x * Math.Sin(ph); }
                }
                _n++;
                if (++_bn == rate / 10)
                {
                    Blocks.Add(2 * Math.Sqrt(_bre * _bre + _bim * _bim) / _bn);
                    _bre = _bim = 0;
                    _bn = 0;
                }
            }
        }
    }

    /// <summary>Several loopback streams on one MTA capture thread, measured over windows.</summary>
    private sealed class Rig : IDisposable
    {
        private sealed class Probe
        {
            public required string Name;
            public required LoopbackStream Stream;
            public required Accum Window;
            public long Index;
            public long QuietSince;
            public readonly List<(long Arrival, long Stamp)> Onsets = [];
            public bool Started;
        }

        private readonly List<Probe> _probes = [];
        private readonly AutoResetEvent _ready = new(false);
        private readonly BlockingQueue _commands = new();
        private readonly Thread _thread;
        private volatile bool _stop;

        public bool DetectOnsets { get; set; }
        public IReadOnlyList<string> Names { get { lock (_probes) return _probes.Select(p => p.Name).ToList(); } }

        public Rig()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "proctest capture" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>Open a stream on the capture thread (rate 0 = endpoint loopback in its mix format).</summary>
        public void Open(string name, uint? pid, bool include, int rate, bool start = false, long buffer = BufferDuration,
            bool autoConvert = true) => _commands.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var s = pid is { } id
                ? LoopbackStream.OpenProcess(id, include, rate, _ready.SafeWaitHandle.DangerousGetHandle(), buffer, autoConvert)
                : OpenEndpoint();
            var probe = new Probe { Name = name, Stream = s, Window = new Accum(s.Format.SampleRate) };
            s.PacketObserver = (frames, flags, qpc) => probe.Window.Packet(frames, flags, qpc, Stopwatch.GetTimestamp());
            lock (_probes) _probes.Add(probe);
            Log.Info($"opened {name}: {s.Format.SampleRate} Hz {s.Format.Channels} ch in {sw.Elapsed.TotalMilliseconds:F0} ms");
            if (start) { s.Start(); probe.Started = true; }
        });

        private LoopbackStream OpenEndpoint()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device), "device");
                try { return LoopbackStream.OpenEndpoint(device, _ready.SafeWaitHandle.DangerousGetHandle(), BufferDuration); }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public void Start() => _commands.Run(() =>
        {
            foreach (var p in _probes.Where(p => !p.Started)) { p.Stream.Start(); p.Started = true; }
        });

        /// <summary>Settle, then measure every stream for a fixed window.</summary>
        public Dictionary<string, Accum> Measure(int settleMs = 300, int windowMs = 1200)
        {
            Thread.Sleep(settleMs);
            _commands.Run(() => { foreach (var p in _probes) p.Window = new Accum(p.Stream.Format.SampleRate); });
            Thread.Sleep(windowMs);
            var result = new Dictionary<string, Accum>();
            _commands.Run(() =>
            {
                foreach (var p in _probes)
                {
                    result[p.Name] = p.Window;
                    p.Window = new Accum(p.Stream.Format.SampleRate); // the returned window stops accumulating
                }
            });
            return result;
        }

        public List<(long Arrival, long Stamp)> Onsets(string name)
        {
            lock (_probes) return _probes.First(p => p.Name == name).Onsets.ToList();
        }

        private void Loop()
        {
            var buf = new List<float>(8192);
            while (!_stop)
            {
                _ready.WaitOne(5);
                _commands.RunPending();
                long now = Stopwatch.GetTimestamp();
                foreach (var p in _probes)
                {
                    if (!p.Started) continue;
                    buf.Clear();
                    long before = p.Window.Packets;
                    try
                    {
                        if (!p.Stream.Drain(buf)) continue;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"{p.Name}: {ex.Message}");
                        continue;
                    }
                    if (p.Window.Packets == before) continue;
                    p.Window.Wake(now);
                    if (DetectOnsets) Detect(p, buf, now);
                    p.Window.Samples(buf, ref p.Index);
                }
            }
            foreach (var p in _probes) p.Stream.Dispose();
        }

        private static void Detect(Probe p, List<float> buf, long arrival)
        {
            int rate = p.Stream.Format.SampleRate;
            int frames = buf.Count / 2;
            for (int i = 0; i < frames; i++)
            {
                long index = p.Index + i;
                if (Math.Abs(buf[i * 2]) < 0.02f) continue;
                if (index - p.QuietSince > rate / 5)
                {
                    // Stamp: when this frame was played, assuming the newest frame of the read played just now.
                    long stamp = arrival - (long)((frames - i) * (double)Stopwatch.Frequency / rate);
                    p.Onsets.Add((arrival, stamp));
                }
                p.QuietSince = index;
            }
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _thread.Join(2000);
        }
    }

    /// <summary>Runs actions on the capture thread and waits for them.</summary>
    private sealed class BlockingQueue
    {
        private readonly Queue<(Action, ManualResetEventSlim, Exception?[])> _q = new();

        public void Run(Action action)
        {
            var done = new ManualResetEventSlim();
            var error = new Exception?[1];
            lock (_q) _q.Enqueue((action, done, error));
            if (!done.Wait(5000)) throw new TimeoutException("capture thread did not respond");
            if (error[0] is { } ex) throw new InvalidOperationException(ex.Message, ex);
        }

        public void RunPending()
        {
            while (true)
            {
                (Action, ManualResetEventSlim, Exception?[]) item;
                lock (_q)
                {
                    if (_q.Count == 0) return;
                    item = _q.Dequeue();
                }
                try { item.Item1(); } catch (Exception ex) { item.Item3[0] = ex; }
                item.Item2.Set();
            }
        }
    }

    // ------------------------------------------------------------------ child: `tone`

    /// <summary>
    /// Plays a quiet sine (or short bursts with --clicks, printing "onset &lt;QPC&gt;" for each) through
    /// the default output. --idle keeps the process alive without opening any audio stream.
    /// </summary>
    public static int Tone(string[] args)
    {
        double freq = double.Parse(Opt(args, "--freq") ?? "1000", System.Globalization.CultureInfo.InvariantCulture);
        float amp = Math.Clamp(float.Parse(Opt(args, "--amp") ?? "0.05", System.Globalization.CultureInfo.InvariantCulture), 0f, 0.2f);
        int seconds = int.Parse(Opt(args, "--seconds") ?? "10");
        bool clicks = args.Contains("--clicks");
        if (args.Contains("--idle"))
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return 0;
        }

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        CoreAudio.Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device), "device");
        CoreAudio.Check(device.Activate(ref CoreAudio.IidAudioClient, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "activate");
        var client = (IAudioClient)obj;
        CoreAudio.Check(client.GetMixFormat(out var mix), "mix");
        var fmt = WaveFormat.FromPointer(mix);
        using var ready = new AutoResetEvent(false);
        CoreAudio.Check(client.Initialize(0, CoreAudio.StreamFlagsEventCallback | CoreAudio.StreamFlagsNoPersist,
            100_000, 0, mix, IntPtr.Zero), "init");
        Marshal.FreeCoTaskMem(mix);
        CoreAudio.Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "event");
        CoreAudio.Check(client.GetBufferSize(out uint bufferFrames), "size");
        CoreAudio.Check(client.GetService(ref CoreAudio.IidAudioRenderClient, out var svc), "render");
        var render = (IAudioRenderClient)svc;

        int rate = fmt.SampleRate;
        int burst = rate * 20 / 1000;
        long written = 0, nextOnset = rate / 2;
        var sw = Stopwatch.StartNew();
        CoreAudio.Check(client.Start(), "start");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            ready.WaitOne(50);
            CoreAudio.Check(client.GetCurrentPadding(out uint padding), "padding");
            uint frames = bufferFrames - padding;
            if (frames == 0) continue;
            CoreAudio.Check(render.GetBuffer(frames, out var data), "buffer");
            long now = Stopwatch.GetTimestamp();
            for (uint i = 0; i < frames; i++)
            {
                long pos = written + i;
                float s;
                if (!clicks) s = amp * MathF.Sin((float)(2 * Math.PI * freq * (pos % (long)rate) / rate));
                else
                {
                    if (pos == nextOnset)
                        Console.WriteLine($"onset {now + (long)((padding + i) * (double)Stopwatch.Frequency / rate)}");
                    long into = pos - nextOnset;
                    s = into >= 0 && into < burst ? amp * MathF.Sin((float)(2 * Math.PI * freq * into / rate)) : 0f;
                    if (into == burst) nextOnset += (long)(rate * (0.4 + Random.Shared.NextDouble() * 0.3));
                }
                WriteFrame(data, (int)i, fmt, s);
            }
            render.ReleaseBuffer(frames, 0);
            written += frames;
        }
        client.Stop();
        Marshal.ReleaseComObject(render);
        Marshal.ReleaseComObject(client);
        Marshal.ReleaseComObject(device);
        Marshal.ReleaseComObject(enumerator);
        return 0;
    }

    private static unsafe void WriteFrame(IntPtr data, int frame, WaveFormat fmt, float value)
    {
        byte* f = (byte*)data + (long)frame * fmt.BlockAlign;
        int bytes = fmt.BitsPerSample / 8;
        for (int c = 0; c < fmt.Channels; c++)
        {
            byte* s = f + c * bytes;
            if (fmt.IsFloat) *(float*)s = value;
            else if (bytes == 2) *(short*)s = (short)(value * 32767);
            else if (bytes == 4) *(int*)s = (int)(value * int.MaxValue);
        }
    }

    private static string? Opt(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
