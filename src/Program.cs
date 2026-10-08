using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Verbose = args.Contains("--verbose");
        var cmd = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "gui";
        try
        {
            return cmd switch
            {
                "gui" => RunGui(startHidden: args.Contains("--tray"), openMixer: args.Contains("--mixer"), openFlyout: args.Contains("--flyout")),
                "scan" => Scan(args.Contains("--txt")).GetAwaiter().GetResult(),
                "stream" => Stream(args).GetAwaiter().GetResult(),
                "group" => GroupCli.Run(args).GetAwaiter().GetResult(),
                "clicks" => Clicks(),
                "mutetest" => MuteTest(),
                "proctest" => ProcTest.Run(args),
                "tone" => ProcTest.Tone(args),
                "routetest" => RouteTest.Run(args),
                "fakeapi" => FakeApi(args),
                "players" => PlayersCli(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            return 1;
        }
    }

    private static int RunGui(bool startHidden, bool openMixer, bool openFlyout)
    {
        Log.ToConsole = false;
        Log.OpenFile(Path.Combine(AppConfig.Directory, "homepodcast.log"));

        var suffix = AppConfig.Profile is { } p ? "." + p : "";
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HomePodCast.Show" + suffix);
        using var single = new Mutex(true, @"Local\HomePodCast.Single" + suffix, out bool first);
        if (!first)
        {
            show.Set(); // ask the running instance to show its window
            return 0;
        }

        Log.Info($"HomePodCast {typeof(Program).Assembly.GetName().Version} starting");
        Autostart.Repair();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error($"UI: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"fatal: {e.ExceptionObject}");
        var config = AppConfig.Load();
        L.Use(config.Language);
        UI.Theme.Mode = config.Theme;
        Log.Info($"UI language {L.Language}, theme {UI.Theme.Mode} ({(UI.Theme.IsDark ? "dark" : "light")})");
        Application.Run(Environment.GetCommandLineArgs().Contains("--effects") ? UI.Pages.EffectsPage.Standalone() : new UI.TrayApp(startHidden, show, openMixer, openFlyout));
        if (UI.LanguageMenu.RestartRequested)
        {
            Log.Info("restarting to apply the UI language");
            Log.CloseFile(); // the new copy opens the same log file
            single.ReleaseMutex();
            single.Dispose(); // ...and must be able to create the single-instance mutex
            Process.Start(Environment.ProcessPath!);
        }
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            HomePodCast
              scan [--txt]                           list AirPlay speakers (and stereo pairs)
              stream --host IP [--latency MS] [--seconds N] [--volume PCT] [--tone] [--verbose]
              players [--apply MS [--seconds N]]     list local players; --apply sets their audio delay to -MS, then restores it
            """);
        Console.WriteLine(GroupCli.Usage + "   (experimental)");
        return 0;
    }

    private static string? Opt(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    private static int Clicks()
    {
        using var r = new ClickRenderer();
        r.ClickScheduled += when => Log.Info($"click scheduled {Net.MediaClock.ToMs(when - Net.MediaClock.Now):F1} ms from now");
        r.Start();
        Thread.Sleep(3500);
        return 0;
    }

    /// <summary>
    /// Does WASAPI loopback still see audio when the endpoint is muted / at zero volume?
    /// Decides whether "mute Windows, stream to HomePod" works without a virtual sound card.
    /// Restores the original mute state and volume afterwards.
    /// </summary>
    private static int MuteTest()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device);
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj);
        var vol = (IAudioEndpointVolume)obj;
        var ctx = Guid.Empty;
        vol.GetMute(out bool origMute);
        vol.GetMasterVolumeLevelScalar(out float origVol);
        Log.Info($"device \"{CoreAudio.FriendlyName(device)}\": muted={origMute} volume={origVol:P0}");

        var fifo = new AudioFifo(RtpSender.SampleRate, targetMs: 20, capMs: 1000);
        using var capture = new LoopbackCapture(fifo, RtpSender.SampleRate);
        using var clicks = new ClickRenderer();
        capture.Start();
        clicks.Start();

        float Measure(string label)
        {
            Thread.Sleep(300);
            float max = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000) { max = Math.Max(max, capture.Peak); Thread.Sleep(20); }
            Log.Info($"{label,-14} loopback peak = {max:F3}");
            return max;
        }

        try
        {
            vol.SetMute(false, ref ctx);
            vol.SetMasterVolumeLevelScalar(Math.Max(origVol, 0.5f), ref ctx);
            Measure("unmuted");
            vol.SetMute(true, ref ctx);
            Measure("muted");
            vol.SetMute(false, ref ctx);
            vol.SetMasterVolumeLevelScalar(0f, ref ctx);
            Measure("volume 0%");
        }
        finally
        {
            vol.SetMasterVolumeLevelScalar(origVol, ref ctx);
            vol.SetMute(origMute, ref ctx);
            vol.GetMute(out bool m);
            vol.GetMasterVolumeLevelScalar(out float v);
            Log.Info($"restored: muted={m} volume={v:P0}");
        }
        return 0;
    }

    /// <summary>Serve the extension status endpoint with a fixed delay, no speaker needed (for testing).</summary>
    private static int FakeApi(string[] args)
    {
        int delay = int.Parse(Opt(args, "--delay") ?? "141");
        int seconds = int.Parse(Opt(args, "--seconds") ?? "600");
        using var api = new LocalApi(LocalApi.DefaultPort, () => new
        {
            app = "HomePodCast",
            version = "fake",
            streaming = delay > 0,
            device = "测试",
            latencyMs = Math.Max(0, delay - 36),
            videoDelayMs = delay,
            videoDelaySource = "fake",
        });
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    /// <summary>
    /// The players the 影视 scene would adjust, and (--apply MS) a dry run without a speaker: hold their audio
    /// delay at −MS for --seconds (default 6), polling like the app, then put their own value back.
    /// </summary>
    private static int PlayersCli(string[] args)
    {
        var scanner = new Players.PlayerScanner();
        var scan = scanner.ScanAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var e in scan.Endpoints)
        {
            string reading;
            try { reading = e.ReadAsync(CancellationToken.None).GetAwaiter().GetResult().ToString(); }
            catch (Exception ex) { reading = ex.Message; }
            Log.Info($"{e.Name} {e.Key}: {reading}");
        }
        foreach (var n in scan.Notes) Log.Info($"{n.Name}: {n.Problem}");
        if (scan.Endpoints.Count == 0 && scan.Notes.Count == 0) Log.Info("no supported player running");
        if (Opt(args, "--apply") is not { } apply) return 0;

        int lag = int.Parse(apply);
        var until = DateTime.UtcNow.AddSeconds(int.Parse(Opt(args, "--seconds") ?? "6"));
        var sync = new Players.PlayerSync(scanner);
        var input = new Players.PlayerSyncInput(true, StreamState.Streaming, lag);
        while (true)
        {
            sync.StepAsync(input, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var s in sync.Statuses) Log.Info(Players.PlayerText.Line(s));
            if (DateTime.UtcNow >= until) break;
            Thread.Sleep(Players.PlayerSync.PollInterval);
        }
        sync.StepAsync(input with { Enabled = false }, CancellationToken.None).GetAwaiter().GetResult();
        Log.Info("restored");
        return 0;
    }

    private static async Task<int> Scan(bool txt)
    {
        var sw = Stopwatch.StartNew();
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine($"found {devices.Count} device(s) in {sw.ElapsedMilliseconds} ms");
        foreach (var d in devices)
        {
            Console.WriteLine("  " + d);
            if (txt) Console.WriteLine("      " + string.Join(' ', d.Txt.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        }
        foreach (var line in StereoPairs.Describe(devices)) Console.WriteLine(line); // experimental stereo pairs
        return 0;
    }

    private static async Task<int> Stream(string[] args)
    {
        var host = IPAddress.Parse(Opt(args, "--host") ?? throw new ArgumentException("--host required"));
        int latency = int.Parse(Opt(args, "--latency") ?? "250");
        int seconds = int.Parse(Opt(args, "--seconds") ?? "15");
        double? volume = Opt(args, "--volume") is { } v ? double.Parse(v) : null;
        bool tone = args.Contains("--tone");

        var fifo = new AudioFifo(RtpSender.SampleRate, targetMs: 20, capMs: 60);
        using var capture = tone ? null : new LoopbackCapture(fifo, RtpSender.SampleRate);
        using var toneGen = tone ? new ToneGenerator(fifo, int.Parse(Opt(args, "--beeps") ?? "1")) : null;
        capture?.Start();
        toneGen?.Start();

        // Experiments: --setup key=value (repeatable) replaces/adds stream SETUP keys; integers, true/false or text.
        var overrides = new Dictionary<string, object?>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--setup" || args[i + 1].Split('=', 2) is not [var key, var text]) continue;
            overrides[key] = long.TryParse(text, out var n) ? n : bool.TryParse(text, out var b) ? b : text;
        }
        var options = new StreamOptions(latency, volume)
        {
            VolumeCapPercent = double.Parse(Opt(args, "--cap") ?? "100"),
            StreamSetupOverrides = overrides.Count > 0 ? overrides : null,
        };

        var sw = Stopwatch.StartNew();
        using var client = await AirPlayClient.ConnectAsync(host, 7000, options, fifo, CancellationToken.None);
        Log.Info($"arrivalToRenderLatencyMs={client.ArrivalToRenderMs?.ToString() ?? "?"} latency={latency} ms");
        Log.Info($"streaming after {sw.ElapsedMilliseconds} ms setup");
        var lost = new TaskCompletionSource<string>();
        client.Lost += reason => lost.TrySetResult(reason);

        var s = client.Sender!;
        for (int t = 1; t <= seconds && !lost.Task.IsCompleted; t++)
        {
            await Task.WhenAny(Task.Delay(1000), lost.Task);
            Log.Info($"t={t,3}s fifo={fifo.Depth * 1000.0 / RtpSender.SampleRate,5:F1}ms " +
                     $"drift={capture?.DriftPpm ?? 0,6:F0}ppm under={fifo.Underruns} over={fifo.Overflows} " +
                     $"sent={s.PacketsSent} silent={s.SilentPackets} late={s.LateWakeups} maxLate={s.MaxLateMs:F1}ms " +
                     $"skip={s.SkippedPackets} rtx={s.RetransmitRequests}/{s.Retransmitted}/{s.RetransmitMisses} " +
                     $"ntp={client.TimingRequests} peak={capture?.Peak ?? 0:F2}");
        }
        if (lost.Task.IsCompleted) Log.Warn($"stopped: {lost.Task.Result}");
        return 0;
    }
}

/// <summary>Test source: N short 880 Hz beeps at the start of every second, generated in real time.</summary>
internal sealed class ToneGenerator(AudioFifo fifo, int beeps = 1) : IDisposable
{
    private volatile bool _stop;
    private Thread? _thread;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            const int rate = RtpSender.SampleRate, chunk = rate / 100;
            var buf = new float[chunk * 2];
            long frame = 0;
            var sw = Stopwatch.StartNew();
            while (!_stop)
            {
                for (int i = 0; i < chunk; i++, frame++)
                {
                    double t = (double)(frame % rate) / rate;
                    int slot = (int)(t / 0.16);                  // 80 ms beep + 80 ms gap
                    bool on = slot < beeps && t - slot * 0.16 < 0.08;
                    float s = on ? (float)(0.5 * Math.Sin(2 * Math.PI * 880 * frame / rate)) : 0f;
                    buf[i * 2] = buf[i * 2 + 1] = s;
                }
                fifo.Write(buf);
                double ahead = frame * 1000.0 / rate - sw.Elapsed.TotalMilliseconds;
                if (ahead > 0) Thread.Sleep((int)ahead);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    public void Dispose() => _stop = true;
}
