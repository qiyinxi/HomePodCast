using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>Explicit, ephemeral P3 single-renderer command; the unified device picker follows in P5.</summary>
internal static class DlnaCli
{
    public const string Usage = "  dlna --scan | --udn UDN (--silent | --capture ENDPOINT_ID) [--seconds N] [--volume PCT] [--cap PCT]\n"
        + "       [--format l16|wav] [--large-length] [--take-over] [--verbose]   (experimental)"; // experimental since 1.2

    internal sealed record Options(bool Scan, string? Udn, string? CaptureId, bool Silent, int Seconds,
        double Volume, double Cap, DlnaAudioFormat? Format, DlnaHttpFraming Framing, bool TakeOver);

    internal static Options Parse(string[] args)
    {
        string? Opt(string name)
        {
            int i = Array.IndexOf(args, name);
            if (i < 0) return null;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            return args[i + 1];
        }
        bool scan = args.Contains("--scan"), silent = args.Contains("--silent");
        string? udn = Opt("--udn"), capture = Opt("--capture");
        int seconds = int.Parse(Opt("--seconds") ?? "15", CultureInfo.InvariantCulture);
        double volume = double.Parse(Opt("--volume") ?? "10", CultureInfo.InvariantCulture);
        double cap = double.Parse(Opt("--cap") ?? "25", CultureInfo.InvariantCulture);
        if (seconds is < 1 or > 86400 || !double.IsFinite(volume) || volume is < 0 or > 100 || !double.IsFinite(cap) || cap is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(args));
        if (scan && (udn != null || capture != null || silent || args.Contains("--take-over")))
            throw new ArgumentException("--scan cannot be combined with playback options.");
        if (!scan && (string.IsNullOrWhiteSpace(udn) || silent == (capture != null)))
            throw new ArgumentException("Select --udn and exactly one of --silent or --capture ENDPOINT_ID.");
        DlnaAudioFormat? format = Opt("--format")?.ToLowerInvariant() switch
        {
            null => null, "l16" => DlnaAudioFormat.L16, "wav" => DlnaAudioFormat.Wav,
            _ => throw new ArgumentException("--format must be l16 or wav."),
        };
        return new(scan, udn, capture, silent, seconds, volume, cap, format,
            args.Contains("--large-length") ? DlnaHttpFraming.LargeContentLength : DlnaHttpFraming.Chunked, args.Contains("--take-over"));
    }

    public static async Task<int> Run(string[] args)
    {
        var options = Parse(args);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var devices = await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), stop.Token);
            if (options.Scan)
            {
                foreach (var found in devices) Console.WriteLine($"DLNA {Log.Name(found.Name)} {found.Address} {found.Udn}");
                return 0;
            }
            var matches = devices.Where(d => d.Udn == options.Udn).ToArray();
            if (matches.Length != 1) throw new ArgumentException("The selected DLNA UDN was not discovered uniquely; use dlna --scan.");
            var device = matches[0];
            IPAddress local = LocalAddressFor(device.Address);
            bool IsPrivate() => NetworkProfile.ForLocalAddress(local).Category == NetworkCategory.Private;
            if (!IsPrivate()) throw new InvalidOperationException("DLNA streaming requires the selected network to be Private.");
            var fifo = new AudioFifo(DlnaAudioFormats.SampleRate, targetMs: 20, capMs: 60);
            // No AppConfig.Load/Save: this command does not overwrite the GUI's capture or device selection.
            using ICaptureSource? capture = options.Silent ? null : new RoutedCapture(fifo, DlnaAudioFormats.SampleRate,
                new AppRouting(new AppConfig { CaptureDeviceId = options.CaptureId }, () => { }));
            var floats = new float[DlnaAudioFormats.FramesPerChunk * 2];
            await using var client = await DlnaClient.ConnectAsync(device, new(local, (_, pcm) =>
            {
                if (options.Silent) { pcm.Clear(); return; }
                fifo.Read(floats);
                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] = float.IsFinite(floats[i]) ? (short)Math.Clamp(Math.Round(floats[i] * 32767), short.MinValue, short.MaxValue) : (short)0;
            }, IsPrivate)
            {
                PreferredFormat = options.Format, Framing = options.Framing, TakeOver = options.TakeOver,
                VolumePercent = options.Volume, VolumeCapPercent = options.Cap,
            }, stop.Token);
            capture?.Start();
            Log.Info($"DLNA {Log.Name(device.Name)} {client.Format}, {options.Framing}; rough alignment, hardware not yet verified");
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(options.Seconds), stop.Token), client.Lost);
            if (client.Lost.IsCompleted) Log.Warn(client.Lost.Result);
            Log.Info($"DLNA PCM frames={client.StreamServer.FramesSent}, bytes={client.StreamServer.BytesSent}, skipped={client.StreamServer.FramesSkipped}");
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    // UDP Connect selects a route and sends no packet, matching NetworkProfile.ForSpeaker.
    private static IPAddress LocalAddressFor(IPAddress renderer)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(renderer, 1900));
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }
}
