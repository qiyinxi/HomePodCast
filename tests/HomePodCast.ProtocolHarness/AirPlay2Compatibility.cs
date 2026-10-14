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

    private sealed class ReplayEncoder(string path) : IAacEncoder
    {
        private readonly BinaryReader _file = new(File.OpenRead(path));
        public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> stereo)
        {
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

    public static async Task Run(string host, int port, int latency, string directory, string pcmPath, bool realtime)
    {
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
        var received = new List<byte>(); long arrived = 0; int offset = -1;
        using var collecting = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            using var file = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[8192];
            while (!collecting.IsCancellationRequested)
            {
                int size = await file.ReadAsync(buffer, collecting.Token);
                if (size == 0) { await Task.Delay(2, collecting.Token); continue; }
                received.AddRange(buffer.AsSpan(0, size).ToArray());
                if (offset < 0)
                {
                    offset = received.ToArray().AsSpan().IndexOf(marker);
                    if (offset >= 0) arrived = MediaClock.Now;
                }
            }
        });
        var device = new AirPlayDevice("CI AirPlay 2", "CI", IPAddress.Parse(host), port, "Shairport Sync",
            new Dictionary<string, string> { ["features"] = "0x0,0x300" });
        using var client = await AirPlayClient.ConnectBufferedAsync(device, new StreamOptions(latency, 10)
            { VolumeCapPercent = 10, RequestLog = Console.WriteLine, Effects = new SignalEffect() },
            new AudioFifo(44100, 0, 999), timeout.Token, () => new ReplayEncoder(Path.Combine(directory, "blocks.bin")), realtime: realtime);
        await Task.Delay(11000, timeout.Token);
        long began = realtime ? client.PtpRealtime!.StartedAt : client.Buffered!.StartedAt;
        long packets = realtime ? client.PtpRealtime!.PacketsSent : client.Buffered!.PacketsSent;
        client.Dispose(); collecting.Cancel();
        try { await reader; } catch (OperationCanceledException) { }
        Console.WriteLine($"AirPlay 2 diagnostics: realtime={realtime}, packets={packets}, decoded={received.Count}, marker={offset}, lost={client.LostReason}");
        if (client.LostReason != null) throw new Exception(client.LostReason);
        if (offset < 0 || offset % 4 != 0) throw new Exception("AirPlay 2 PCM marker missing or unaligned");
        double actual = MediaClock.ToMs(arrived - began) - markerFrame * 1000.0 / 44100;
        if (Math.Abs(actual - latency) > 50) throw new Exception($"AirPlay 2 latency {actual:F1} ms, expected {latency} ±50 ms");
        int bytes = Math.Min(6 * 44100 * 4, received.Count - offset);
        if (bytes < 4 * 44100 * 4) throw new Exception("AirPlay 2 decoded fewer than four seconds");
        if (!received.ToArray().AsSpan(offset, bytes).SequenceEqual(reference.AsSpan(markerFrame * 4, bytes)))
            throw new Exception("AirPlay 2 decoded PCM differs from the independent reference or has gaps");
        Console.WriteLine($"PASS: AirPlay 2 {(realtime ? "realtime" : "buffered")}, {bytes / 4} exact decoded frames, latency={actual:F1} ms");
    }
}
