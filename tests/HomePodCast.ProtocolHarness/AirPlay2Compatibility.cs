using System.Buffers.Binary;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.ProtocolHarness;

internal static class AirPlay2Compatibility
{
    public static void Fixture(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Generate the fixture with Windows Media Foundation");
        Directory.CreateDirectory(directory);
        using var blocks = new BinaryWriter(File.Create(Path.Combine(directory, "blocks.bin")));
        using var adts = File.Create(Path.Combine(directory, "signal.aac"));
        using var pcmFile = new BinaryWriter(File.Create(Path.Combine(directory, "source.pcm")));
        using var encoder = new AacEncoder();
        var pcm = new short[2048];
        for (int block = 0; block < 600; block++)
        {
            for (int i = 0; i < 1024; i++)
                for (int channel = 0; channel < 2; channel++)
                {
                    short value = RaopCompatibility.Signal(block * 1024L + i, channel);
                    pcm[i * 2 + channel] = value; pcmFile.Write(value);
                }
            var output = encoder.Encode(pcm);
            blocks.Write((ushort)output.Count);
            foreach (var aac in output) { blocks.Write((ushort)aac.Length); blocks.Write(aac); adts.Write(BufferedAudio.Adts(aac)); }
        }
        Console.WriteLine("Windows AAC-LC fixture generated without opening an audio device");
    }

    private sealed class ReplayEncoder(string path, bool validateSource = false) : IAacEncoder
    {
        private readonly BinaryReader _file = new(File.OpenRead(path));
        private long _frame;
        public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> stereo)
        {
            if (validateSource)
                for (int frame = 0; frame < stereo.Length / 2; frame++, _frame++)
                    for (int channel = 0; channel < 2; channel++)
                        if (stereo[frame * 2 + channel] != RaopCompatibility.Signal(_frame, channel))
                            throw new Exception($"Group AAC input differs from shared history at frame {_frame}, channel {channel}");
            var result = new List<byte[]>();
            int count = _file.ReadUInt16();
            for (int i = 0; i < count; i++) result.Add(_file.ReadBytes(_file.ReadUInt16()));
            return result;
        }
        public void Dispose() => _file.Dispose();
    }

    private sealed class SignalEffect : IAudioEffect
    {
        private long _frame;
        public void Process(Span<float> audio, int frames)
        {
            for (int i = 0; i < frames; i++, _frame++)
                for (int channel = 0; channel < 2; channel++) audio[i * 2 + channel] = RaopCompatibility.Signal(_frame, channel) / 32767f;
        }
    }

    public static async Task Run(string host, int port, int latency, string directory, string pcmPath, bool realtime, bool coordinated = false)
    {
        using var diagnostics = new CompatibilityDiagnostics();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        byte[] reference;
        if (realtime)
        {
            reference = new byte[44100 * 10 * 4];
            for (int i = 0; i < reference.Length / 4; i++)
                for (int channel = 0; channel < 2; channel++)
                    BinaryPrimitives.WriteInt16LittleEndian(reference.AsSpan(i * 4 + channel * 2), RaopCompatibility.Signal(i, channel));
        }
        else reference = File.ReadAllBytes(Path.Combine(directory, "reference.pcm"));
        // Choose a unique decoded marker after startup muting. Compare all subsequent frames, not just the marker.
        int markerFrame = 44100 / 2;
        byte[] marker = reference.AsSpan(markerFrame * 4, 512).ToArray();
        // 2026-10-17: copying all accumulated PCM per read crosses the LOH threshold during early playback.
        // Bound and allocate the capture before starting a transport; finding a marker must not generate GC load.
        var received = new PcmCaptureBuffer(marker, 44100 * 30 * 4);
        using var collecting = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            using var file = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[8192];
            while (!collecting.IsCancellationRequested)
            {
                int size = await file.ReadAsync(buffer, collecting.Token);
                if (size == 0) { await Task.Delay(2, collecting.Token); continue; }
                long completed = MediaClock.Now;
                received.Append(buffer.AsSpan(0, size), completed);
                diagnostics.ObserveRead(received.Count, completed, MediaClock.Now);
            }
        });
        var device = new AirPlayDevice("CI AirPlay 2", "CI", IPAddress.Parse(host), port, "Shairport Sync",
            new Dictionary<string, string> { ["features"] = "0x0,0x300" });
        AirPlayClient? client = null;
        ReceiverOutputSet? outputs = null;
        long began = 0, packets = 0;
        string? lost = null;
        Exception? sessionFailure = null;
        int expectedLatency = latency;
        try
        {
            if (coordinated)
            {
                // Generate the signal before selecting a future epoch, so waveform construction cannot use its lead.
                var source = new float[44100 * 14 * 2];
                for (int frame = 0; frame < source.Length / 2; frame++)
                    for (int channel = 0; channel < 2; channel++)
                        source[frame * 2 + channel] = RaopCompatibility.Signal(frame, channel) / 32767f;
                var timeline = new TimelineBuffer(44100, 44100 * 14, MediaClock.Now);
                var settings = ReceiverPreferences.Of(device) with
                {
                    ProtocolOverride = realtime ? ReceiverProtocol.AirPlay2Realtime : ReceiverProtocol.AirPlay2Buffered,
                    RealtimeLatencyMs = latency, CompatibilityLatencyMs = latency,
                };
                var options = new ReceiverConnectOptions(latency, 0, 10, null, null, new SignalEffect(), Console.WriteLine);
                outputs = await ReceiverOutputSet.PrepareAsync([new(device.DeviceId, device.Name,
                    _ => Task.FromResult(new ReceiverGroupTarget(device, settings)), options)], () => 10, timeout.Token,
                    connect: (protocol, target, preferences, connection, clocks, ct) => ReceiverGroupConnections.PrepareAsync(target,
                        preferences! with { ProtocolOverride = protocol }, connection, clocks, ct,
                        encoder: () => new ReplayEncoder(Path.Combine(directory, "blocks.bin"), validateSource: true)));
                // Precomputed processed history isolates transport alignment from runner scheduling and audio devices.
                // The buffered replay additionally checks every source sample before returning the real Windows AAC.
                long firstFrame = timeline.FrameAt(MediaClock.Now + MediaClock.FromMs(500));
                began = timeline.TicksAt(firstFrame);
                timeline.WriteFrames(firstFrame, source);
                const int headroomMs = 20;
                expectedLatency = outputs.MinimumDelayMs + headroomMs;
                diagnostics.Report("before-group-start", began + MediaClock.FromMs(headroomMs));
                await outputs.StartAsync(timeline, expectedLatency, 10, false, began + MediaClock.FromMs(headroomMs), timeout.Token);
            }
            else
                client = await AirPlayClient.ConnectBufferedAsync(device, new StreamOptions(latency, 10)
                    { VolumeCapPercent = 10, RequestLog = Console.WriteLine, Effects = new SignalEffect() },
                    new AudioFifo(44100, 0, 999), timeout.Token, () => new ReplayEncoder(Path.Combine(directory, "blocks.bin")), realtime: realtime);
            await Task.Delay(11000, timeout.Token);
            if (outputs != null)
            {
                packets = outputs.Status.Single().PacketsSent;
                lost = outputs.Status.Single().Error ?? outputs.LostException?.Message;
            }
            else
            {
                began = realtime ? client!.PtpRealtime!.StartedAt : client!.Buffered!.StartedAt;
                packets = realtime ? client.PtpRealtime!.PacketsSent : client.Buffered!.PacketsSent;
                lost = client.LostReason;
            }
        }
        catch (Exception error) { sessionFailure = error; throw; }
        finally
        {
            try { outputs?.Dispose(); client?.Dispose(); }
            finally
            {
                collecting.Cancel();
                try { await reader; }
                catch (OperationCanceledException) when (collecting.IsCancellationRequested) { }
                catch (Exception error) when (sessionFailure != null)
                {
                    // A collector failure must be observed without replacing the session failure that led here.
                    Console.WriteLine($"PCM collector also failed during cleanup: {error}");
                }
            }
        }
        diagnostics.Report("completed");
        int offset = received.MarkerOffset;
        long arrived = received.MarkerArrivedAt;
        Console.WriteLine($"AirPlay 2 diagnostics: coordinated={coordinated}, realtime={realtime}, packets={packets}, decoded={received.Count}, marker={offset}, lost={lost}");
        if (lost != null) throw new Exception(lost);
        if (offset < 0 || offset % 4 != 0)
        {
            Diagnose(reference, received.Written.ToArray());
            throw new Exception("AirPlay 2 PCM marker missing or unaligned");
        }
        double actual = MediaClock.ToMs(arrived - began) - markerFrame * 1000.0 / 44100;
        if (Math.Abs(actual - expectedLatency) > 50) throw new Exception($"AirPlay 2 latency {actual:F1} ms, expected {expectedLatency} ±50 ms");
        int bytes = Math.Min(6 * 44100 * 4, received.Count - offset);
        if (bytes < 4 * 44100 * 4) throw new Exception("AirPlay 2 decoded fewer than four seconds");
        if (!received.Written.Slice(offset, bytes).SequenceEqual(reference.AsSpan(markerFrame * 4, bytes)))
            throw new Exception("AirPlay 2 decoded PCM differs from the independent reference or has gaps");
        Console.WriteLine($"PASS: AirPlay 2 {(realtime ? "realtime" : "buffered")}, {bytes / 4} exact decoded frames, latency={actual:F1} ms");
    }

    private static void Diagnose(byte[] reference, byte[] received)
    {
        int first = -1, peak = 0;
        for (int i = 0; i + 1 < received.Length; i += 2)
        {
            int value = BinaryPrimitives.ReadInt16LittleEndian(received.AsSpan(i));
            peak = Math.Max(peak, Math.Abs(value));
            if (first < 0 && Math.Abs(value) > 100) first = i / 4;
        }
        Console.WriteLine($"PCM analysis: frames={received.Length / 4}, firstSignal={first}, peak={peak}");
        foreach (int frame in new[] { 22050, 88200, 176400 })
        {
            long best = long.MaxValue; int at = -1;
            for (int i = 0; i < received.Length / 4 - 512; i++)
            {
                long error = 0;
                for (int sample = 0; sample < 512; sample += 32)
                {
                    long delta = BinaryPrimitives.ReadInt16LittleEndian(reference.AsSpan((frame + sample) * 4))
                        - BinaryPrimitives.ReadInt16LittleEndian(received.AsSpan((i + sample) * 4));
                    error += delta * delta;
                    if (error >= best) break;
                }
                if (error < best) { best = error; at = i; }
            }
            int exact = received.AsSpan().IndexOf(reference.AsSpan(frame * 4, 512));
            Console.WriteLine($"PCM reference frame={frame}: exactByte={exact}, bestFrame={at}, squaredError={best}");
            if (at >= 0)
            {
                Console.WriteLine("reference samples=" + string.Join(',', Enumerable.Range(0, 12).Select(i => BinaryPrimitives.ReadInt16LittleEndian(reference.AsSpan((frame + i) * 4)))));
                Console.WriteLine("receiver samples=" + string.Join(',', Enumerable.Range(0, 12).Select(i => BinaryPrimitives.ReadInt16LittleEndian(received.AsSpan((at + i) * 4)))));
            }
        }
    }
}
