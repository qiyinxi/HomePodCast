using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

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
                "gui" => RunGui(startHidden: args.Contains("--tray"), openMixer: args.Contains("--mixer"), openFlyout: args.Contains("--flyout"),
                    testOsd: args.Contains("--osd-muted") ? "muted" : args.Contains("--osd") ? "volume" : null),
                "scan" => Scan(args.Contains("--txt")).GetAwaiter().GetResult(),
                "info" => Info(args).GetAwaiter().GetResult(),
                "stream" => Stream(args).GetAwaiter().GetResult(),
                "group" => GroupCli.Run(args).GetAwaiter().GetResult(),
                "dlna" => DlnaCli.Run(args).GetAwaiter().GetResult(),
                "clicks" => Clicks(),
                "mutetest" => MuteTest(),
                "proctest" => ProcTest.Run(args),
                "tone" => ProcTest.Tone(args),
                "routetest" => RouteTest.Run(args),
                "fakeapi" => FakeApi(args),
                "players" => Players.PlayersCommand.Run(name => Opt(args, name)),
                "firewall" => FirewallCommand.Run(), // CI: tools/ci/msi-e2e.ps1
                Update.UpdateSelfTest.Command => Update.UpdateSelfTest.Run(args), // CI: tools/ci/update-e2e.ps1
                "autostart" => AutostartCommand(args), // CI: packaging/msix/e2e-test.ps1
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            return 1;
        }
    }

    /// <summary>The window's single-instance mutex (one per test profile); update-selftest checks it for a running app.</summary>
    internal static string SingleInstanceName => @"Local\HomePodCast.Single" + (AppConfig.Profile is { } p ? "." + p : "");

    /// <param name="testOsd">Hidden test switch <c>--osd</c> / <c>--osd-muted</c>: show the volume-key OSD once running.</param>
    private static int RunGui(bool startHidden, bool openMixer, bool openFlyout, string? testOsd)
    {
        Log.ToConsole = false;

        var suffix = AppConfig.Profile is { } p ? "." + p : "";
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HomePodCast.Show" + suffix);
        using var flyout = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HomePodCast.Flyout" + suffix);
        using var single = new Mutex(true, SingleInstanceName, out bool first);
        if (!first)
        {
            // Ask the running instance to show its window; the hidden test switch --flyout opens its tray flyout instead
            // (the GUI smoke test in CI, tests/HomePodCast.GuiSmoke, has no tray icon it could right-click reliably).
            (openFlyout ? flyout : show).Set();
            return 0;
        }

        // Only the running instance opens the log: it keeps the file open for writing, so a second launch opening it
        // first threw, exited with 1 and never showed the window.
        Log.OpenFile(Path.Combine(AppConfig.Directory, AppConfig.LogFileName));
        var updatedFrom = Update.UpdateInstaller.UpdatedFrom(Environment.GetCommandLineArgs());
        Log.Info($"HomePodCast {Update.ReleaseVersion.CurrentText} starting" + (updatedFrom != null ? $" (after an update from {updatedFrom})" : "") +
                 (AppEnvironment.IsPackaged ? $" from the package {AppEnvironment.PackageFamilyName ?? "?"} (Microsoft Store version)" : ""));
        // A line of its own: msix-e2e checks how the line above ends.
        Log.Info($"Windows {Environment.OSVersion.Version}" +
                 (Protocol.ChaChaPoly.UsesOwnImplementation ? ": no ChaCha20-Poly1305 in Windows, using our own" : ""));
        // The Store version's startup task passes --tray (packaging/msix/AppxManifest.xml); if Windows didn't, the
        // activation kind still says it was the startup task.
        if (!startHidden && Autostart.StartedByStartupTask())
        {
            Log.Info("started by the startup task: into the tray");
            startHidden = true;
        }
        Autostart.Repair();
        // When an installer's Restart Manager closes us for an upgrade, it may start us again afterwards (into the
        // tray). Not after crashes, hangs or reboots, and never for test copies.
        if (AppConfig.Profile == null) Native.RegisterApplicationRestart("--tray", Native.RestartNoCrash | Native.RestartNoHang | Native.RestartNoReboot);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error($"UI: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"fatal: {e.ExceptionObject}");
        var config = AppConfig.Load();
        L.Use(config.Language);
        UI.Theme.Mode = config.Theme;
        Log.Info($"UI language {L.Language}, theme {UI.Theme.Mode} ({(UI.Theme.IsDark ? "dark" : "light")})");
        var args = Environment.GetCommandLineArgs();
        Application.Run(args.Contains("--effects") ? UI.Pages.EffectsPage.Standalone()
            : new UI.TrayApp(startHidden, show, openMixer, openFlyout, testOsd, updatedFrom, flyout, AppRestart.Resume(args),
                Update.ReleaseFeed.ForGuiSmoke(args, Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")));
        // 「立即重启」 after a .NET update (UI/TrayApp.Runtime.cs), or a new UI language.
        if ((AppRestart.Requested ?? (UI.LanguageMenu.RestartRequested ? new AppRestart.Request("to apply the UI language") : null)) is { } restart)
        {
            // The Store version starts again through its package (Explorer), so the new copy has package identity too.
            var (file, arguments) = AppRestart.CommandFor(restart);
            bool asShellUser = AppRestart.RunsElevated();
            Log.Info($"restarting {restart.Why}" + (AppEnvironment.IsPackaged || arguments.Length > 0 ? $": {file} {arguments}" : "") +
                     (asShellUser ? " (as the signed-in user: this copy runs as administrator)" : ""));
            Log.CloseFile(); // the new copy opens the same log file
            single.ReleaseMutex();
            single.Dispose(); // ...and must be able to create the single-instance mutex
            if (AppRestart.Start(file, arguments, asShellUser) is { } error)
            {
                Log.OpenFile(Path.Combine(AppConfig.Directory, AppConfig.LogFileName)); // no new copy holds it
                Log.Error($"restart: {file} did not start: {error}");
                Log.CloseFile();
                return 1;
            }
        }
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            HomePodCast
              scan [--txt]                           list AirPlay speakers (and stereo pairs)
              info --host IP [--port N]                         dump the speaker's /info (read-only)
              stream --host IP [--latency MS] [--seconds N] [--volume PCT] [--tone | --silent] [--verbose]
                     [--setup KEY=VALUE] [--session KEY=VALUE]   experiments: stream / session SETUP keys
              stream --protocol raop --host IP [--port N] [--latency 1000|1500|2000] [--silent | --tone] [--cap 10]
                     [--encryption 0|1|4]           experimental; password via HOMEPODCAST_RAOP_PASSWORD
              players [--apply MS [--seconds N]]     list local players; --apply sets their audio delay to -MS, then restores it
              autostart on|off|status                the Microsoft Store version's startup task (tests: see packaging/msix)
              firewall                               the startup firewall check for this exe (read-only; exit code 0: no prompt)
            """);
        // experimental since 1.1: the RAOP CLI above stays separate from the existing realtime command.
        // experimental since 1.4: the 5.1 line below.
        Console.WriteLine("  " + SurroundCliOptions.Usage + "   5.1 buffered protocol check, digital silence only (experimental)");
        Console.WriteLine(GroupCli.Usage + "   (experimental)");
        Console.WriteLine(DlnaCli.Usage);
        Console.WriteLine(Update.UpdateSelfTest.Usage);
        return 0;
    }

    /// <summary>
    /// <c>autostart on|off|status</c>: the Microsoft Store version's startup task, switched by the same code as 设置 →
    /// 开机自动启动 (Autostart.SetStartupTask) and read as that switch reads it. The package has no command line of its
    /// own (Start passes no arguments), so this is for tests: CI's msix-e2e job activates the installed package with
    /// these arguments (packaging/msix/e2e-test.ps1). The lines go to the app's log as well, unless a running copy
    /// holds it. Exit code: 0 when the task is as asked (status: when it could be read), 2 when Windows keeps it
    /// otherwise (turned off by the user in Windows, or a policy), 1 on failure. An unpackaged copy has no startup task
    /// (its switch writes the Run value): it fails and touches neither the Run value nor the log.
    /// </summary>
    private static int AutostartCommand(string[] args)
    {
        var what = args.Where(a => !a.StartsWith("--")).Skip(1).FirstOrDefault();
        if (what is not ("on" or "off" or "status"))
        {
            Console.WriteLine("usage: HomePodCast autostart on|off|status");
            return 1;
        }
        if (Autostart.Method != AutostartMethod.StartupTask)
        {
            Log.Warn("autostart: only the Microsoft Store version has a startup task; this copy's 开机自动启动 is the Run value");
            return 1;
        }
        try { Log.OpenFile(Path.Combine(AppConfig.Directory, AppConfig.LogFileName)); }
        catch (Exception) { } // the running copy holds the log: the outcome is only in the exit code
        try
        {
            if (what == "status")
            {
                Log.Info($"autostart: startup task {StartupTaskApi.GetState(AppEnvironment.StartupTaskId)}");
                return 0;
            }
            bool on = what == "on";
            return Autostart.ViewOf(Autostart.SetStartupTask(on)).On == on ? 0 : 2;
        }
        catch (Exception ex)
        {
            if (what == "status") Log.Warn($"autostart: the startup task can't be read: {ex.Message}");
            return 1; // on, off: Autostart.SetStartupTask logged it
        }
        finally
        {
            Log.CloseFile();
        }
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

    private static async Task<int> Scan(bool txt)
    {
        var sw = Stopwatch.StartNew();
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine($"found {devices.Count} device(s) in {sw.ElapsedMilliseconds} ms");
        foreach (var d in devices)
        {
            Console.WriteLine("  " + d);
            if (txt) Console.WriteLine($"      experimental receiver={d.Kind} airplay={(d.HasAirPlay ? d.Port : null)} raop={d.RaopPort} " + string.Join(' ', d.Txt.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        }
        foreach (var line in StereoPairs.Describe(devices, allHomePods: txt)) Console.WriteLine(line); // experimental stereo pairs
        foreach (var line in HomeTheater.Describe(devices)) Console.WriteLine(line); // Apple TVs and their home-theater HomePods
        return 0;
    }

    /// <summary>`info --host IP [--port N]`: the speaker's GET /info, flattened (read-only: no pairing, no stream, no volume).</summary>
    private static async Task<int> Info(string[] args)
    {
        var host = IPAddress.Parse(Opt(args, "--host") ?? throw new ArgumentException("--host required"));
        int port = int.Parse(Opt(args, "--port") ?? "7000");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var info = await SpeakerInfoReader.ReadAsync(host, port, timeout.Token);
        Console.WriteLine($"{host}:{port}: {ReceiverKinds.Classify("", null, info: info)}");
        foreach (var line in InfoDump.Lines(info)) Console.WriteLine("  " + line);
        Console.WriteLine("-> " + SpeakerInfoCache.DescribeFlags(info));
        return 0;
    }

    private static async Task<int> Stream(string[] args)
    {
        if (args.Contains("--surround")) return await StreamSurround(args);
        if (Opt(args, "--protocol") is "raop")
            return await StreamRaop(args);
        var host = IPAddress.Parse(Opt(args, "--host") ?? throw new ArgumentException("--host required"));
        int latency = int.Parse(Opt(args, "--latency") ?? "250");
        int seconds = int.Parse(Opt(args, "--seconds") ?? "15");
        double? volume = Opt(args, "--volume") is { } v ? double.Parse(v) : null;
        bool tone = args.Contains("--tone");
        bool silent = args.Contains("--silent"); // nothing captured: the sender fills the stream with silence

        var fifo = new AudioFifo(RtpSender.SampleRate, targetMs: 20, capMs: 60);
        using var capture = tone || silent ? null : new LoopbackCapture(fifo, RtpSender.SampleRate);
        using var toneGen = tone && !silent ? new ToneGenerator(fifo, int.Parse(Opt(args, "--beeps") ?? "1")) : null;
        capture?.Start();
        toneGen?.Start();

        // Experiments: --setup key=value (repeatable) replaces/adds stream SETUP keys, --session key=value session
        // SETUP keys; integers, true/false or text ("new-uuid": a fresh upper-case UUID).
        Dictionary<string, object?>? Overrides(string option)
        {
            var overrides = new Dictionary<string, object?>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != option || args[i + 1].Split('=', 2) is not [var key, var text]) continue;
                overrides[key] = text == "new-uuid" ? Guid.NewGuid().ToString().ToUpperInvariant()
                    : long.TryParse(text, out var n) ? n : bool.TryParse(text, out var b) ? b : text;
            }
            return overrides.Count > 0 ? overrides : null;
        }
        var options = new StreamOptions(latency, volume)
        {
            VolumeCapPercent = double.Parse(Opt(args, "--cap") ?? "100"),
            StreamSetupOverrides = Overrides("--setup"),
            SessionSetupExtras = Overrides("--session"),
            Pairings = PairingStore.Default, // an Apple TV paired in the app: pair-verify with that pairing (never a new PIN)
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
    // experimental since 1.1: separate compatibility sender, including a silent protocol-test mode.
    private static async Task<int> StreamRaop(string[] args)
    {
        var host = IPAddress.Parse(Opt(args, "--host") ?? throw new ArgumentException("--host required"));
        int port = int.Parse(Opt(args, "--port") ?? "7000");
        int seconds = int.Parse(Opt(args, "--seconds") ?? "15");
        bool silent = args.Contains("--silent"), tone = args.Contains("--tone");
        var fifo = new AudioFifo(RaopAudio.SampleRate, targetMs: 20, capMs: 60);
        using var capture = silent || tone ? null : new LoopbackCapture(fifo, RaopAudio.SampleRate);
        using var source = tone && !silent ? new ToneGenerator(fifo) : null;
        capture?.Start(); source?.Start();
        var floats = new float[RaopAudio.FramesPerPacket * 2];
        using var client = await RaopClient.ConnectAsync(host, port, new RaopOptions
        {
            LatencyMs = int.Parse(Opt(args, "--latency") ?? "2000"),
            VolumePercent = double.Parse(Opt(args, "--volume") ?? "10"),
            VolumeCapPercent = double.Parse(Opt(args, "--cap") ?? "10"),
            Txt = new Dictionary<string, string> { ["et"] = Opt(args, "--encryption") ?? "0", ["cn"] = "1" },
            Password = Environment.GetEnvironmentVariable("HOMEPODCAST_RAOP_PASSWORD"),
            Log = Log.Info,
            ReadAudio = pcm =>
            {
                fifo.Read(floats);
                for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)Math.Round(floats[i] * 32767), short.MinValue, short.MaxValue);
            },
        }, CancellationToken.None);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Lost += reason => lost.TrySetResult(reason);
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds)), lost.Task);
        Log.Info($"RAOP sent={client.PacketsSent} rtx={client.RetransmitRequests}/{client.Retransmitted}/{client.RetransmitMisses} ntp={client.TimingRequests}");
        if (lost.Task.IsCompleted) { Log.Warn(lost.Task.Result); return 1; }
        return 0;
    }

    /// <summary>
    /// experimental since 1.4: `stream --surround --host IP --silent`, the app's 5.1 session (AirPlayClient.Surround.cs)
    /// sending AAC-encoded digital silence: a FIFO nobody writes to, no capture, no tone. Refuses a speaker another sender
    /// is using (no override: AGENTS.md). Exit code 0: anchored and nothing lost; 2: refused or in use; 1: lost or failed.
    /// </summary>
    private static async Task<int> StreamSurround(string[] args)
    {
        if (SurroundCliOptions.Parse(args, out var error) is not { } o)
        {
            Console.WriteLine(error);
            Console.WriteLine("usage: HomePodCast " + SurroundCliOptions.Usage);
            return 2;
        }
        Log.Info($"stream --surround: host {o.Host}, {o.Seconds} s of digital silence, latency {o.LatencyMs} ms, volume {o.VolumePercent}% (cap {o.CapPercent}%)");
        var fifo = new AudioFifo(SurroundSender.Rate, targetMs: 40, capMs: 250, channels: SurroundSender.Channels); // never written
        var options = new StreamOptions(o.LatencyMs, o.VolumePercent)
        {
            VolumeCapPercent = o.CapPercent,
            Pairings = PairingStore.Default,
            RequestLog = Log.Info,
        };
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Log.Info("Ctrl+C: stopping; TEARDOWN follows"); stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            AirPlayClient client;
            try
            {
                client = await AirPlayClient.ConnectSurroundAsync(o.Host, 7000, options, new SurroundSetup(fifo) { RequireIdle = true }, stop.Token);
            }
            catch (Exception ex) when (ex is SurroundUnavailableException or SpeakerInUseException)
            {
                Log.Info($"SURROUND REFUSED: {AirPlayException.ForLog(ex)}");
                return 2;
            }
            catch (Exception ex)
            {
                Log.Info($"SURROUND FAILED: {ex.GetType().Name}: {AirPlayException.ForLog(ex)}");
                return 1;
            }
            using (client)
            {
                var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Lost += reason => lost.TrySetResult(reason);
                if (client.LostReason is { } early) lost.TrySetResult(early);
                var sender = client.SurroundSender!;
                var ptp = client.SurroundClock!;
                var watch = Stopwatch.StartNew();
                for (int t = 1; t <= o.Seconds && !lost.Task.IsCompleted && !stop.IsCancellationRequested; t++)
                {
                    var wait = TimeSpan.FromSeconds(t) - watch.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.WhenAny(Task.Delay(wait, stop.Token).ContinueWith(_ => { }, TaskScheduler.Default), lost.Task);
                    if (lost.Task.IsCompleted || stop.IsCancellationRequested) break;
                    if (t == 2) Log.Info(await SurroundStatusFlags(o.Host, stop.Token));
                    if (t % 2 == 0)
                        Log.Info($"t={t}s sent={sender.PacketsSent} bytes={sender.BytesSent} maxLate={sender.MaxLateMs:F1}ms " +
                                 $"ptp syncAge={ptp.SyncAgeMs?.ToString() ?? "?"}ms drift={ptp.DriftPpm?.ToString("F1") ?? "?"}ppm");
                }
                Log.Info(ptp.Describe);
                if (lost.Task.IsCompleted)
                {
                    Log.Info($"SURROUND LOST: {lost.Task.Result}");
                    return 1;
                }
                Log.Info(stop.IsCancellationRequested ? "SURROUND OK (stopped early with Ctrl+C)" : "SURROUND OK");
                return 0;
            }
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>One read-only GET /info on its own connection: bit 17 (a session) and bit 20 (an anchored timeline running).</summary>
    private static async Task<string> SurroundStatusFlags(IPAddress host, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var info = await SpeakerInfoReader.ReadAsync(host, 7000, timeout.Token);
            return StatusFlags.FromInfo(info) is { } flags
                ? $"statusFlags {StatusFlags.Hex(flags)} bit17 session={StatusFlags.SessionActive(flags)}, " +
                  $"bit20 timeline={((flags & StatusFlags.AnchoredTimelineRunning) != 0 ? "yes" : "no")}"
                : "statusFlags unreadable: /info has none";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"statusFlags unreadable: {ex.GetType().Name}: {ex.Message}";
        }
    }
}

/// <summary>
/// experimental since 1.4: the options of `stream --surround`. Silence only, and a low ceiling: it talks to a real
/// HomePod (the maintainer's checklist), so --silent is required, --tone/--beeps are refused, --cap stays at or below 10 %.
/// </summary>
internal sealed record SurroundCliOptions(IPAddress Host, double CapPercent, double VolumePercent, int LatencyMs, int Seconds)
{
    public const string Usage = "stream --surround --host IP --silent [--latency MS] [--seconds N] [--cap PCT]";

    /// <summary>Silence still goes out at a volume: never more than this.</summary>
    public const double MaxCapPercent = 10;

    /// <summary>Null with a reason when the arguments ask for anything but a silent, low-volume check.</summary>
    public static SurroundCliOptions? Parse(string[] args, out string? error)
    {
        error = null;
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool Number(string name, double fallback, out double value)
        {
            value = fallback;
            if (Opt(name) is not { } text) return !args.Contains(name);
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
                   && double.IsFinite(value);
        }

        if (!args.Contains("--silent")) error = "--surround sends digital silence only: add --silent";
        else if (args.Contains("--tone") || args.Contains("--beeps")) error = "--surround sends digital silence only: --tone and --beeps are refused";
        else if (Opt("--host") is not { } hostText || !IPAddress.TryParse(hostText, out _)) error = "--host IP required";
        else if (!Number("--cap", MaxCapPercent, out double cap) || cap < 0) error = "--cap: a percentage";
        else if (cap > MaxCapPercent) error = $"--cap {cap} refused: at most {MaxCapPercent} %";
        else if (!Number("--volume", cap, out double volume) || volume < 0) error = "--volume: a percentage";
        else if (volume > cap) error = $"--volume {volume} refused: above --cap {cap}";
        else if (!Number("--latency", SurroundPolicy.LatencyMs, out double latency)) error = "--latency: milliseconds"; // the app's 5.1 latency
        else if (!Number("--seconds", 30, out double seconds) || seconds < 1) error = "--seconds: at least 1";
        else return new(IPAddress.Parse(hostText), cap, volume, (int)Math.Clamp(latency, 1000, 4000), (int)Math.Min(seconds, 3600));
        return null;
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
