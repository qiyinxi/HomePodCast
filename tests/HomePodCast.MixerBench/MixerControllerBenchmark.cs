using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;
using HomePodCast;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Tests;

internal static class MixerControllerBenchmark
{
    public static void Run(string output)
    {
        var rows = new List<object>();
        foreach (bool coordinated in new[] { false, true, true, false })
        {
            using var receiver = new FakeAirPlayReceiver(false) { ArrivalToRenderMs = 85 };
            var config = new AppConfig { DeviceId = receiver.DeviceId, DeviceName = "fake HomePod", Host = "127.0.0.1",
                Scene = Scene.Game, LatencyMs = 105, AutoConnect = false };
            StreamController? controller = null;
            var mixer = new MixerOutputCoordinator(new(), (destination, capture) =>
            {
                controller = NewController(receiver); controller.CaptureFactory = capture;
                return new MixerStreamSession(destination, controller);
            }, (_, fifo) => new Capture(fifo), () => throw new InvalidOperationException("Endpoint bypass created a hub"));
            if (coordinated) mixer.ConnectAsync(config, ConnectTrigger.Manual).GetAwaiter().GetResult();
            else { controller = NewController(receiver); controller.CaptureFactory = fifo => new Capture(fifo); controller.Start(config.DeviceId, config.Host, 105, 0); }
            var guard = Stopwatch.StartNew();
            while (controller!.State != StreamState.Streaming && guard.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
            if (controller.State != StreamState.Streaming) throw new InvalidOperationException(controller.StatusText);
            Thread.Sleep(3000);
            while (receiver.Rtp.Audio.TryDequeue(out _)) { }
            var sender = controller.ActiveSender!;
            long skippedBefore = sender.SkippedPackets, lateBefore = sender.LateWakeups;
            int startTarget = controller.Fifo.TargetFrames, startCap = controller.Fifo.CapFrames;
            using var process = Process.GetCurrentProcess();
            process.Refresh(); var cpu = process.TotalProcessorTime; long allocated = GC.GetTotalAllocatedBytes(true);
            long peakPrivate = process.PrivateMemorySize64;
            int threads = process.Threads.Count;
            var clock = Stopwatch.StartNew();
            long frames = 0, markerSamples = 0;
            var lateness = new List<double>();
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                while (receiver.Rtp.Audio.TryDequeue(out var packet))
                {
                    byte[] samples = FakeReceiver.Decrypt(packet.Bytes, receiver.StreamKey!);
                    for (int i = 0; i < samples.Length; i += 2)
                    {
                        if (BinaryPrimitives.ReadInt16BigEndian(samples.AsSpan(i)) != 8192) throw new InvalidDataException("Bad or silent marker");
                        markerSamples++;
                    }
                    uint frame = unchecked(FakeReceiver.Rtp(packet.Bytes) - sender.RtpBase);
                    long due = sender.T0 + (long)(frame * (double)MediaClock.Frequency / 44100);
                    lateness.Add(Math.Max(0, MediaClock.ToMs(packet.Qpc - due))); frames += 352;
                }
                Thread.Sleep(5);
                process.Refresh(); peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            }
            clock.Stop(); process.Refresh();
            if (frames < 59 * 44100) throw new InvalidDataException("Fake receiver did not consume the minute");
            var cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
            long allocation = GC.GetTotalAllocatedBytes(true) - allocated;
            lateness.Sort();
            long skipped = sender.SkippedPackets - skippedBefore, late = sender.LateWakeups - lateBefore;
            int finalTarget = controller.Fifo.TargetFrames, finalCap = controller.Fifo.CapFrames;
            int extra = controller.Capture!.ExtraLatencyMs;
            if (coordinated) mixer.DisposeAsync().AsTask().GetAwaiter().GetResult(); else controller.StopFullyAsync().GetAwaiter().GetResult();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            var row = new { coordinated, seconds = clock.Elapsed.TotalSeconds, cpuSeconds, cpuCores = cpuSeconds / clock.Elapsed.TotalSeconds,
                allocatedBytes = allocation, peakPrivateBytes = peakPrivate, privateBytesAfterDispose = process.PrivateMemorySize64,
                threadsBefore = threads, threadsAfter = process.Threads.Count, startTarget, finalTarget, startCap, finalCap,
                extraLatencyMs = extra, frames, markerSamples, skipped, late, p99RtpArrivalLateMs = lateness[(int)(lateness.Count * .99)],
                maxRtpArrivalLateMs = lateness[^1], leasesAfter = mixer.Leases.Count };
            rows.Add(row);
            output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { host = "Release console; actual single-controller loop, fake endpoint and loopback AirPlay receiver; ABBA order; 3s warmup and >=60s per pass", results = rows }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
    }
    private static StreamController NewController(FakeAirPlayReceiver receiver) => new() { AirPlayPort = receiver.Port, ArrivalToRenderMs = 85 };
    private sealed class Capture(AudioFifo fifo) : ICaptureSource
    {
        private readonly ManualResetEventSlim _stop = new();
        private Thread? _thread;
        public string? DeviceName => "fake endpoint";
        public double DriftPpm => 0;
        public float Peak => .25f;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start()
        {
            _thread = new Thread(Produce) { IsBackground = true, Name = "fake endpoint producer", Priority = ThreadPriority.Highest };
            _thread.Start();
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Produce()
        {
            using var mmcss = Native.EnterMmcss("Audio");
            using var timer = new Native.PreciseTimer();
            var signal = Enumerable.Repeat(.25f, 882).ToArray();
            long origin = MediaClock.Now, cycles = 0;
            while (!_stop.IsSet)
            {
                long due = origin + cycles * MediaClock.Frequency / 100, now = MediaClock.Now;
                if (now < due) { timer.Sleep(due - now); continue; }
                fifo.Write(signal); cycles++;
            }
        }
        public void Dispose() { _stop.Set(); _thread?.Join(); }
    }
}
