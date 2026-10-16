using System.Net;
using System.Text.Json;
using HomePodCast.Net;

namespace HomePodCast.ProtocolHarness;

internal static class DlnaCompressedCompatibility
{
    private const int RequiredFrames = DlnaCodecFixture.SampleRate * 4;

    // Called only after DlnaCompatibility has verified the CI namespace, exact veth address and absence of a route.
    public static async Task Run(string name, IPAddress local, IPAddress peer, DlnaAudioFormat format,
        DlnaHttpFraming framing, string pcmPath, string fixtureDirectory)
    {
        var fixture = DlnaCodecFixture.Load(fixtureDirectory, format);
        byte[] reference = File.ReadAllBytes(Path.Combine(fixtureDirectory, "reference.pcm"));
        byte[] source = File.ReadAllBytes(Path.Combine(fixtureDirectory, "source.pcm"));
        if (reference.Length % 4 != 0 || reference.Length / 4 != fixture.OutputPcmFrames
            || reference.Length < (DlnaPcmComparison.MarkerFrame + RequiredFrames) * 4
            || reference.Length > DlnaCodecFixture.SampleRate * 8 * 4)
            throw new InvalidDataException("Independent decoder PCM has an unexpected sample count.");
        var sourceMarker = DlnaPcmComparison.FindMarker(source, DlnaPcmComparison.MarkerFrame, reference, reference.Length);
        if (sourceMarker.Frame < 0 || sourceMarker.Rms > 1500)
            throw new InvalidDataException($"The codec fixture lost its source/channel marker (RMS={sourceMarker.Rms:F1}).");
        int codecDelay = sourceMarker.Frame - DlnaPcmComparison.MarkerFrame;
        Console.WriteLine($"Reference: {reference.Length / 4} FFmpeg-decoded frames; measured source-to-reference offset={codecDelay} frames, "
            + $"source marker RMS={sourceMarker.Rms:F1}. Lossy output is compared with the decoded bitstream, not sample-exact original PCM.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        DlnaDevice? device = null;
        for (int attempt = 0; attempt < 5 && device is null; attempt++)
        {
            var found = await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(2), deadline.Token, local);
            foreach (var candidate in found) Console.WriteLine($"SSDP: {candidate.Name}, {candidate.Udn}, {candidate.DescriptionUri}");
            device = found.SingleOrDefault(d => d.Name == name && d.Address.Equals(peer));
        }
        if (device is null) throw new Exception("The isolated software renderer was not discovered through SSDP.");
        using var control = new DlnaControl(device);
        Console.WriteLine("ConnectionManager Sink: " + string.Join(',', await control.GetProtocolInfoAsync(deadline.Token)));
        using var collecting = new CancellationTokenSource();
        using var received = new MemoryStream();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new List<(long Ticks, int Frames)>();
        int marker = -1;
        long markerTicks = 0;
        var reader = Task.Run(async () =>
        {
            try
            {
                await using var file = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[8192];
                int nextSearch = (DlnaPcmComparison.MarkerFrame + DlnaPcmComparison.MarkerFrames) * 4;
                while (true)
                {
                    int count = await file.ReadAsync(buffer, collecting.Token);
                    if (count == 0) { await Task.Delay(2, collecting.Token); continue; }
                    received.Write(buffer, 0, count);
                    long now = MediaClock.Now;
                    if (received.Length > DlnaCodecFixture.SampleRate * 4 * 15)
                        throw new Exception("Renderer PCM exceeded the bounded fixture duration.");
                    observations.Add((now, checked((int)received.Length / 4)));
                    if (marker < 0 && received.Length >= nextSearch)
                    {
                        var match = DlnaPcmComparison.FindMarker(reference, DlnaPcmComparison.MarkerFrame,
                            received.GetBuffer(), checked((int)received.Length));
                        if (match.Rms <= 16)
                        {
                            marker = match.Frame;
                            markerTicks = now - MediaClock.FromMs((received.Length / 4 - marker) * 1000.0 / DlnaCodecFixture.SampleRate);
                            Console.WriteLine($"Measured renderer/reference alignment: renderer frame {marker}, "
                                + $"reference frame {DlnaPcmComparison.MarkerFrame}, marker RMS={match.Rms:F2} LSB.");
                        }
                        nextSearch += 16_384;
                    }
                    if (marker >= 0 && received.Length >= (marker + RequiredFrames) * 4L) complete.TrySetResult();
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
                        stereo[i] = DlnaCodecFixture.Signal(sourceFrame, 0);
                        stereo[i + 1] = DlnaCodecFixture.Signal(sourceFrame, 1);
                    }
                }, () => true)
            {
                PreferredFormat = format, Framing = framing, EncoderFactory = fixture.CreateEncoder,
                // This gain is confined to an isolated CI filesink with no audio endpoint or network uplink.
                VolumePercent = 100, VolumeCapPercent = 100, PullTimeout = TimeSpan.FromSeconds(10),
            }, deadline.Token);
            if (client.Format != format) throw new Exception($"Negotiated {client.Format}, expected {format}.");
            Console.WriteLine($"Negotiated {format}, MIME={DlnaAudioFormats.ContentType(format)}, framing={framing}, stream={client.StreamUri}");
            if (await control.GetMediaInfoAsync(deadline.Token) != client.StreamUri.AbsoluteUri)
                throw new Exception("AVTransport did not retain the sender's media URI.");
            if (await control.GetTransportInfoAsync(deadline.Token) != "PLAYING")
                throw new Exception("Renderer did not enter PLAYING.");
            await Task.WhenAny(complete.Task, client.Lost).WaitAsync(deadline.Token);
            if (client.Lost.IsCompleted) throw new Exception("DLNA session lost: " + await client.Lost);
            await complete.Task;
        }
        finally
        {
            try { if (client is not null) await client.DisposeAsync(); }
            finally { collecting.Cancel(); await reader; }
        }

        if (client is null || marker < 0) throw new Exception("Renderer produced no aligned decoded marker.");
        var comparison = DlnaPcmComparison.Compare(reference, DlnaPcmComparison.MarkerFrame,
            received.ToArray(), marker, RequiredFrames);
        var server = client.StreamServer;
        if (!server.FirstGetTicks.IsCompletedSuccessfully || !server.FirstPcmTicks.IsCompletedSuccessfully
            || server.FramesSent < RequiredFrames || server.EncodedBytesSent <= 0
            || server.EncodedBytesSent == server.FramesSent * 4 || server.BytesSent != server.EncodedBytesSent
            || server.SourceFramesRead < RequiredFrames || !server.IsStopped)
            throw new Exception("HTTP encoded-byte/PCM-duration counters or session cleanup do not match the stream.");
        if (await control.GetTransportInfoAsync(deadline.Token) != "STOPPED")
            throw new Exception("Session disposal did not stop AVTransport on the software renderer.");
        long getTicks = await server.FirstGetTicks;
        // Each file read observes progress in the software renderer. Remove the measured codec delay and
        // marker alignment; use the median to reduce scheduling and buffer-boundary noise.
        double[] lag = observations.Where(p => p.Frames >= marker + DlnaCodecFixture.SampleRate && p.Frames <= marker + RequiredFrames)
            .Select(p => MediaClock.ToMs(p.Ticks - getTicks)
                - (p.Frames - marker + DlnaPcmComparison.MarkerFrame - codecDelay) * 1000.0 / DlnaCodecFixture.SampleRate)
            .Order().ToArray();
        if (lag.Length == 0) throw new Exception("No bounded renderer timing observations were collected.");
        double median = lag[lag.Length / 2];
        string result = $"PASS: DLNA {format}/{framing}, {RequiredFrames} aligned decoded asymmetric stereo frames; "
            + $"independent decoder RMS={comparison.Rms:F2}, peak={comparison.Peak} LSB, measured codec offset={codecDelay} frames; "
            + $"median software filesink prebuffer={median:F1} ms; GET to marker={MediaClock.ToMs(markerTicks - getTicks):F1} ms; "
            + $"encoded={server.EncodedBytesSent} bytes, media duration={server.FramesSent} PCM frames, source={server.SourceFramesRead} frames, skipped deadlines={server.FramesSkipped}. "
            + "Timing includes codec, transport and software scheduling; it is not acoustic latency.";
        Console.WriteLine(result);
        File.WriteAllText(Path.ChangeExtension(pcmPath, ".result.txt"), result + Environment.NewLine);
        File.WriteAllText(Path.ChangeExtension(pcmPath, ".result.json"), JsonSerializer.Serialize(new
        {
            format = format.ToString(), framing = framing.ToString(), comparedFrames = RequiredFrames,
            referenceFrames = reference.Length / 4, renderedFrames = received.Length / 4,
            codecOffsetFrames = codecDelay, rendererMarkerFrame = marker, referenceMarkerFrame = DlnaPcmComparison.MarkerFrame,
            decoderRmsLsb = comparison.Rms, decoderPeakLsb = comparison.Peak, softwarePrebufferMedianMs = median,
            encodedBytes = server.EncodedBytesSent, mediaPcmFrames = server.FramesSent, sourcePcmFrames = server.SourceFramesRead,
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, result + Environment.NewLine + Environment.NewLine);
    }
}
