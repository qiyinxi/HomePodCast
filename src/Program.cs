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
        var cmd = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "help";
        try
        {
            return cmd switch
            {
                "scan" => Scan().GetAwaiter().GetResult(),
                "stream" => Stream(args).GetAwaiter().GetResult(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            return 1;
        }
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
        using var toneGen = tone ? new ToneGenerator(fifo) : null;
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

/// <summary>Test source: 880 Hz beep for 150 ms every second, generated in real time.</summary>
internal sealed class ToneGenerator(AudioFifo fifo) : IDisposable
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
                    float s = t < 0.15 ? (float)(0.5 * Math.Sin(2 * Math.PI * 880 * frame / rate)) : 0f;
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
