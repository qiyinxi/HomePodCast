using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HomePodCast.Net;

namespace HomePodCast.ProtocolHarness;

internal static class DlnaCompatibility
{
    private const int SampleRate = 44_100;
    private const int WarmupFrames = SampleRate / 4;
    private const int RequiredFrames = SampleRate * 4;

    // Independent, asymmetric channels expose swapped channels, byte order, gain and missing frames.
    private static short Signal(long frame, int channel)
    {
        if (frame < WarmupFrames || frame >= WarmupFrames + RequiredFrames + SampleRate) return 0;
        frame -= WarmupFrames;
        return channel == 0 ? (short)((frame * 997 + 313) % 50_000 - 25_000)
            : (short)((frame * 1_543 + 9_173) % 46_000 - 23_000);
    }

    internal static async Task Run(string name, string localAddress, string rendererAddress,
        string formatText, string framingText, string pcmPath)
    {
        RequireIsolatedRunner(localAddress, rendererAddress);
        var local = IPAddress.Parse(localAddress);
        var peer = IPAddress.Parse(rendererAddress);
        var format = formatText switch { "l16" => DlnaAudioFormat.L16, "wav" => DlnaAudioFormat.Wav,
            _ => throw new ArgumentException("Expected l16 or wav.") };
        var framing = framingText switch { "chunked" => DlnaHttpFraming.Chunked,
            "length" => DlnaHttpFraming.LargeContentLength,
            _ => throw new ArgumentException("Expected chunked or length.") };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        DlnaDevice? device = null;
        for (int attempt = 0; attempt < 5 && device is null; attempt++)
        {
            var found = await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(2), deadline.Token, local);
            foreach (var candidate in found)
                Console.WriteLine($"SSDP: {candidate.Name}, {candidate.Udn}, {candidate.DescriptionUri}");
            device = found.SingleOrDefault(d => d.Name == name && d.Address.Equals(peer));
        }
        if (device is null) throw new Exception("The isolated software renderer was not discovered through SSDP.");
        using var control = new DlnaControl(device);
        var protocols = await control.GetProtocolInfoAsync(deadline.Token);
        Console.WriteLine("ConnectionManager Sink: " + string.Join(',', protocols));
        Console.WriteLine("AVTransport before connect: " + await control.GetTransportInfoAsync(deadline.Token));

        byte[] marker = new byte[128 * 4];
        for (int frame = 0; frame < 128; frame++)
            for (int channel = 0; channel < 2; channel++)
                BinaryPrimitives.WriteInt16LittleEndian(marker.AsSpan(frame * 4 + channel * 2), Signal(frame + WarmupFrames, channel));
        using var collecting = new CancellationTokenSource();
        using var received = new MemoryStream();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int markerOffset = -1;
        long markerTicks = 0;
        var reader = Task.Run(async () =>
        {
            try
            {
                await using var file = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[8192];
                while (true)
                {
                    int count = await file.ReadAsync(buffer, collecting.Token);
                    if (count == 0) { await Task.Delay(2, collecting.Token); continue; }
                    received.Write(buffer, 0, count);
                    if (received.Length > SampleRate * 4 * 25) throw new Exception("Renderer PCM exceeded the bounded test duration.");
                    if (markerOffset < 0)
                    {
                        markerOffset = received.GetBuffer().AsSpan(0, (int)received.Length).IndexOf(marker);
                        if (markerOffset >= 0) markerTicks = MediaClock.Now;
                    }
                    if (markerOffset >= 0 && received.Length >= markerOffset + RequiredFrames * 4L)
                        complete.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (collecting.IsCancellationRequested) { }
            catch (Exception e) { complete.TrySetException(e); throw; }
        });

        long sourceFrame = 0;
        DlnaClient? client = null;
        try
        {
            client = await DlnaClient.ConnectAsync(device, new DlnaClientOptions(local,
                (_, stereo) =>
                {
                    for (int i = 0; i < stereo.Length; i += 2, sourceFrame++)
                    {
                        stereo[i] = Signal(sourceFrame, 0);
                        stereo[i + 1] = Signal(sourceFrame, 1);
                    }
                }, () => true)
            {
                PreferredFormat = format, Framing = framing,
                // This bypass is confined to the CI-only, filesink renderer with no network uplink.
                VolumePercent = 100, VolumeCapPercent = 100,
                PullTimeout = TimeSpan.FromSeconds(10),
            }, deadline.Token);
            if (client.Format != format) throw new Exception($"Negotiated {client.Format}, expected {format}.");
            Console.WriteLine($"Negotiated {client.Format}, framing={framing}, stream={client.StreamUri}");
            if (await control.GetMediaInfoAsync(deadline.Token) != client.StreamUri.AbsoluteUri)
                throw new Exception("AVTransport did not retain the sender's media URI.");
            string state = await control.GetTransportInfoAsync(deadline.Token);
            Console.WriteLine("AVTransport after Play: " + state);
            if (state != "PLAYING") throw new Exception("Renderer did not enter PLAYING.");
            await Task.WhenAny(complete.Task, client.Lost).WaitAsync(deadline.Token);
            if (client.Lost.IsCompleted) throw new Exception("DLNA session lost: " + await client.Lost);
            await complete.Task;
        }
        finally
        {
            try { if (client is not null) await client.DisposeAsync(); }
            finally
            {
                collecting.Cancel();
                await reader;
            }
        }

        if (client is null || markerOffset < 0 || markerOffset % 4 != 0)
            throw new Exception("Rendered PCM has no aligned stereo marker.");
        byte[] decoded = received.ToArray();
        for (int frame = 0; frame < RequiredFrames; frame++)
            for (int channel = 0; channel < 2; channel++)
            {
                short actual = BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(markerOffset + frame * 4 + channel * 2));
                short expected = Signal(frame + WarmupFrames, channel);
                if (actual != expected)
                    throw new Exception($"Rendered PCM differs at frame {frame}, channel {channel}: {actual}, expected {expected}.");
            }
        var server = client.StreamServer;
        if (!server.FirstGetTicks.IsCompletedSuccessfully || !server.FirstPcmTicks.IsCompletedSuccessfully
            || server.FramesSent < RequiredFrames || server.BytesSent != server.FramesSent * 4 || !server.IsStopped)
            throw new Exception("HTTP PCM counters or session cleanup did not match the rendered stream.");
        if (await control.GetTransportInfoAsync(deadline.Token) != "STOPPED")
            throw new Exception("Session disposal did not stop AVTransport on the software renderer.");
        double markerMs = MediaClock.ToMs(markerTicks - await server.FirstGetTicks);
        double prebufferMs = markerMs - WarmupFrames * 1000.0 / SampleRate;
        string result = $"PASS: DLNA {format}/{framing}, {RequiredFrames} sample-exact asymmetric stereo frames; "
            + $"HTTP GET to rendered marker={markerMs:F1} ms, estimated receiver prebuffer={prebufferMs:F1} ms; "
            + $"sent PCM={server.BytesSent} bytes/{server.FramesSent} frames, skipped deadlines={server.FramesSkipped}. "
            + "Timing observes a software filesink and includes its scheduling; it is not acoustic latency.";
        Console.WriteLine(result);
        File.WriteAllText(Path.ChangeExtension(pcmPath, ".result.txt"), result + Environment.NewLine);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, result + Environment.NewLine + Environment.NewLine);
    }

    private static void RequireIsolatedRunner(string local, string renderer)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("HOMEPODCAST_DLNA_CI_ISOLATED") != "1"
            || local != "10.77.0.1" || renderer != "10.77.0.2")
            throw new InvalidOperationException("DLNA compatibility sessions require the isolated Linux CI namespaces.");
        var addresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToArray();
        if (addresses.Length != 1 || !addresses[0].Equals(IPAddress.Parse(local))
            || File.ReadLines("/proc/net/route").Skip(1).Any(line => line.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } fields && fields[1] == "00000000"))
            throw new InvalidOperationException("CI sender must have only its veth address and no default route.");
    }
}
