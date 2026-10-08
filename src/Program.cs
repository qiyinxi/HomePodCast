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
                "gui" => RunGui(startHidden: args.Contains("--tray"), openMixer: args.Contains("--mixer")),
                "scan" => Scan().GetAwaiter().GetResult(),
                "stream" => Stream(args).GetAwaiter().GetResult(),
                "clicks" => Clicks(),
                "mutetest" => MuteTest(),
                "fakeapi" => FakeApi(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            return 1;
        }
    }

    private static int RunGui(bool startHidden, bool openMixer)
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
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error($"UI: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"fatal: {e.ExceptionObject}");
        Application.Run(Environment.GetCommandLineArgs().Contains("--effects") ? UI.EffectsForm.Standalone() : new UI.TrayApp(startHidden, show, openMixer));
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            HomePodCast
              scan                                   list AirPlay speakers
              stream --host IP [--latency MS] [--seconds N] [--volume PCT] [--tone] [--verbose]
            """);
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

    private static async Task<int> Scan()
    {
        var sw = Stopwatch.StartNew();
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine($"found {devices.Count} device(s) in {sw.ElapsedMilliseconds} ms");
        foreach (var d in devices) Console.WriteLine("  " + d);
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

        var sw = Stopwatch.StartNew();
        using var client = await AirPlayClient.ConnectAsync(host, 7000, new StreamOptions(latency, volume), fifo,
            CancellationToken.None);
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
