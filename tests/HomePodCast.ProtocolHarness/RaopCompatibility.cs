using System.Buffers.Binary;
using System.Net;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.ProtocolHarness;

internal static class RaopCompatibility
{
    internal static short Signal(long frame, int channel)
    {
        short left;
        if (frame < 256) left = (short)(frame * 997 % 50000 - 25000); // unique stereo alignment marker
        else
        {
            double time = frame / 44100.0;
            left = (short)Math.Round(16000 * Math.Sin(2 * Math.PI * (200 * time + 110 * time * time)));
        }
        return channel == 0 ? left : (short)-left;
    }

    public static async Task Run(string host, int port, int encryption, int latency, string password, string pcmPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var received = new List<byte>();
        long markerTime = 0;
        int markerOffset = -1;
        var marker = new byte[128 * 4];
        for (int i = 0; i < 128; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(marker.AsSpan(i * 4), Signal(i, 0));
            BinaryPrimitives.WriteInt16LittleEndian(marker.AsSpan(i * 4 + 2), Signal(i, 1));
        }
        using var collecting = new CancellationTokenSource();
        var read = Task.Run(async () =>
        {
            using var stream = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[8192];
            while (!collecting.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, collecting.Token);
                if (count == 0) { await Task.Delay(5, collecting.Token); continue; }
                received.AddRange(buffer.AsSpan(0, count).ToArray());
                if (markerOffset < 0)
                {
                    markerOffset = received.ToArray().AsSpan().IndexOf(marker);
                    if (markerOffset >= 0) markerTime = MediaClock.Now;
                }
            }
        });
        long sourceFrame = 0;
        using var client = await RaopClient.ConnectAsync(IPAddress.Parse(host), port, new RaopOptions
        {
            Txt = new Dictionary<string, string> { ["et"] = "0,1", ["cn"] = "1" }, EncryptionOverride = encryption,
            LatencyMs = latency, Password = password == "-" ? null : password,
            VolumeCapPercent = 10, VolumePercent = 10, Log = Console.WriteLine,
            ReadAudio = pcm =>
            {
                for (int i = 0; i < pcm.Length; i += 2, sourceFrame++) { pcm[i] = Signal(sourceFrame, 0); pcm[i + 1] = Signal(sourceFrame, 1); }
            },
        }, deadline.Token);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Lost += error => lost.TrySetResult(error);
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10), deadline.Token), lost.Task);
        if (lost.Task.IsCompleted) throw new Exception("RAOP session lost: " + lost.Task.Result);
        client.Dispose();
        collecting.Cancel();
        try { await read; } catch (OperationCanceledException) { }
        if (markerOffset < 0 || markerOffset % 4 != 0) throw new Exception("Decoded PCM has no aligned marker");
        double actualLatency = MediaClock.ToMs(markerTime - client.StartedAt);
        if (Math.Abs(actualLatency - latency) > 50) throw new Exception($"Latency {actualLatency:F1} ms, expected {latency} ±50 ms");
        int frames = Math.Min((received.Count - markerOffset) / 4, 6 * 44100);
        if (frames < 4 * 44100) throw new Exception($"Only {frames} decoded frames");
        var decoded = received.ToArray();
        for (int i = 0; i < frames; i++)
            for (int channel = 0; channel < 2; channel++)
                if (BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(markerOffset + i * 4 + channel * 2)) != Signal(i, channel))
                    throw new Exception($"PCM mismatch/gap at frame {i}, channel {channel}");
        Console.WriteLine($"PASS: et={encryption}, password={password != "-"}, {frames} sample-exact frames, latency={actualLatency:F1} ms");
    }
}
