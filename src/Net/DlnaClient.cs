using System.Net;
using System.Xml.Linq;

namespace HomePodCast.Net;

internal sealed record DlnaClientOptions(IPAddress LocalAddress, DlnaPcmReader ReadAudio, Func<bool> IsPrivateNetwork)
{
    public DlnaAudioFormat? PreferredFormat { get; init; }
    public DlnaHttpFraming Framing { get; init; } = DlnaHttpFraming.Chunked;
    public double VolumePercent { get; init; } = 20;
    public double VolumeCapPercent { get; init; } = 25;
    public bool Muted { get; init; }
    public bool TakeOver { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PullTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Owns one explicitly selected renderer, without changing capture, saved settings or firewall rules.</summary>
internal sealed class DlnaClient : IAsyncDisposable
{
    private readonly DlnaControl _control;
    private readonly HttpClient _http;
    private readonly DlnaClientOptions _options;
    private readonly IPAddress _rendererAddress;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _commands = new(1);
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new();
    private Task? _monitor, _dispose;
    private int _stopping;
    private int _losing;
    private double _gain;
    private bool _remoteVolume = true, _remoteMute = true, _assigned;
    private string _previousUri = "";
    private DlnaStreamServer? _server;

    private DlnaClient(DlnaDevice device, DlnaClientOptions options, CancellationToken ct)
    {
        _options = options;
        _rendererAddress = device.Address;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _http = DlnaHttp.CreateClient(options.LocalAddress);
        _control = new DlnaControl(device, _http);
    }

    public Uri StreamUri => StreamServer.Url;
    public DlnaAudioFormat Format { get; private set; }
    public DlnaStreamServer StreamServer => _server ?? throw new InvalidOperationException("The DLNA stream is not prepared.");
    public Task<string> Lost => _lost.Task;

    public static async Task<DlnaClient> ConnectAsync(DlnaDevice device, DlnaClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ReadAudio);
        ArgumentNullException.ThrowIfNull(options.IsPrivateNetwork);
        ValidateVolume(options.VolumePercent, options.VolumeCapPercent);
        if (options.PreferredFormat is { } format && !Enum.IsDefined(format) || !Enum.IsDefined(options.Framing))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PollInterval < TimeSpan.FromMilliseconds(20) || options.PollInterval > TimeSpan.FromSeconds(30)
            || options.PullTimeout < TimeSpan.FromMilliseconds(20) || options.PullTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options));
        ct.ThrowIfCancellationRequested();
        if (!options.IsPrivateNetwork()) throw new InvalidOperationException("DLNA streaming requires a private network.");
        var client = new DlnaClient(device, options, ct);
        try
        {
            await client.InitializeAsync().ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        var ct = _stop.Token;
        // Inspect before changing volume or URI: a failed inspection never authorizes takeover.
        string state = await _control.GetTransportInfoAsync(ct).ConfigureAwait(false);
        string previousUri = await _control.GetMediaInfoAsync(ct).ConfigureAwait(false);
        if (!_options.TakeOver && !IsAvailable(state))
            throw new InvalidOperationException("The DLNA renderer is in use; explicit takeover is required.");
        _previousUri = previousUri;
        Format = SelectFormat(await _control.GetProtocolInfoAsync(ct).ConfigureAwait(false), _options.PreferredFormat);
        // During explicit takeover, never unmute the previous content: an unsupported volume action
        // only attenuates our PCM and cannot cap somebody else's currently playing media.
        await SetVolumeAsync(_options.VolumePercent, true, ct).ConfigureAwait(false);
        _server = new DlnaStreamServer(new(_options.LocalAddress, _rendererAddress, Format, ReadAudio, _options.IsPrivateNetwork)
        {
            Framing = _options.Framing,
        });
        _server.Start();
        // Some renderers prefetch before SetAVTransportURI/Play returns. Serve bounded PCM immediately;
        // waiting for the SOAP reply here would deadlock those renderers.
        await RequireOwnershipAsync(ct).ConfigureAwait(false);
        _assigned = true;
        await _control.SetAvTransportUriAsync(StreamUri, Metadata(StreamUri, Format), ct).ConfigureAwait(false);
        await SetVolumeAsync(_options.VolumePercent, _options.Muted, ct).ConfigureAwait(false);
        await _control.PlayAsync(ct).ConfigureAwait(false);
        await StreamServer.FirstGetTicks.WaitAsync(_options.PullTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _monitor = MonitorAsync();
    }

    public async Task SetVolumeAsync(double percent, bool muted, CancellationToken ct = default)
    {
        ValidateVolume(percent, _options.VolumeCapPercent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _gain, 0);
            double capped = Math.Min(percent, _options.VolumeCapPercent);
            if (_remoteVolume)
                try
                {
                    await RequireOwnershipAsync(linked.Token).ConfigureAwait(false);
                    await _control.SetVolumeAsync((int)Math.Floor(capped), linked.Token).ConfigureAwait(false);
                }
                catch (DlnaActionException ex) when (ex.IsUnsupportedAction) { _remoteVolume = false; }
            if (_remoteMute)
                try
                {
                    await RequireOwnershipAsync(linked.Token).ConfigureAwait(false);
                    await _control.SetMuteAsync(muted, linked.Token).ConfigureAwait(false);
                }
                catch (DlnaActionException ex) when (ex.IsUnsupportedAction) { _remoteMute = false; }
            await RequireOwnershipAsync(linked.Token).ConfigureAwait(false);
            // Remote and digital volume are alternatives. Digital mute remains available even when
            // the renderer omits RenderingControl; authentication/network errors never trigger fallback.
            Volatile.Write(ref _gain, muted ? 0 : _remoteVolume ? 1 : capped / 100);
        }
        finally { _commands.Release(); }
    }

    private async Task RequireOwnershipAsync(CancellationToken ct)
    {
        if (!_assigned)
        {
            if (_options.TakeOver) return;
            string state = await _control.GetTransportInfoAsync(ct).ConfigureAwait(false);
            string uri = await _control.GetMediaInfoAsync(ct).ConfigureAwait(false);
            if (IsAvailable(state) && uri == _previousUri) return;
            Lose("DLNA renderer became busy during connection.");
            throw new InvalidOperationException("The DLNA renderer became busy; explicit takeover is required.");
        }
        if (await _control.GetMediaInfoAsync(ct).ConfigureAwait(false) == StreamUri.AbsoluteUri) return;
        Lose("DLNA renderer was taken over by another controller.");
        throw new InvalidOperationException("The DLNA session no longer owns this renderer.");
    }

    private void ReadAudio(long ticks, Span<short> stereo)
    {
        _options.ReadAudio(ticks, stereo);
        double gain = Volatile.Read(ref _gain);
        if (gain == 1) return;
        for (int i = 0; i < stereo.Length; i++) stereo[i] = (short)Math.Clamp(Math.Round(stereo[i] * gain), short.MinValue, short.MaxValue);
    }

    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PollInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (StreamServer.IsStopped) { Lose("DLNA stream endpoint closed."); return; }
                string uri = await _control.GetMediaInfoAsync(_stop.Token).ConfigureAwait(false);
                if (uri != StreamUri.AbsoluteUri) { Lose("DLNA renderer was taken over by another controller."); return; }
                string state = await _control.GetTransportInfoAsync(_stop.Token).ConfigureAwait(false);
                if (state is not ("PLAYING" or "TRANSITIONING")) { Lose($"DLNA renderer stopped ({state})."); return; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            if (Volatile.Read(ref _stopping) == 0) Lose("DLNA session cancelled.");
        }
        catch (Exception ex) { Lose($"DLNA control connection lost: {ex.Message}"); }
    }

    private void Lose(string reason)
    {
        if (Interlocked.Exchange(ref _losing, 1) != 0) return;
        Volatile.Write(ref _gain, 0);
        _server?.Dispose();
        _stop.Cancel();
        _lost.TrySetResult(reason);
    }

    public Task StopAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        Volatile.Write(ref _gain, 0);
        _stop.Cancel();
        _server?.Dispose();
        if (_monitor != null) await _monitor.ConfigureAwait(false);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-read ownership even on failure/cancellation: never stop somebody else's replacement stream.
            if (_assigned && _server != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    if (await _control.GetMediaInfoAsync(cleanup.Token).ConfigureAwait(false) == StreamUri.AbsoluteUri)
                        await _control.StopAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Best effort, bounded cleanup. The local listener is already closed. */ }
            }
        }
        finally
        {
            _commands.Release();
            _control.Dispose();
            _http.Dispose();
            _stop.Dispose();
            _lost.TrySetResult("DLNA session stopped.");
        }
    }

    private static void ValidateVolume(double percent, double cap)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > 100 || !double.IsFinite(cap) || cap is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
    }

    private static bool IsAvailable(string state) => state is "STOPPED" or "NO_MEDIA_PRESENT";

    internal static DlnaAudioFormat SelectFormat(IEnumerable<string> protocols, DlnaAudioFormat? preferred = null)
    {
        var formats = protocols.ToArray();
        bool Supports(DlnaAudioFormat format) => formats.Any(p => SupportsFormat(p, format));
        if (preferred is { } chosen)
            return Supports(chosen) ? chosen : throw new NotSupportedException($"The renderer does not advertise {chosen}.");
        if (Supports(DlnaAudioFormat.L16)) return DlnaAudioFormat.L16;
        if (Supports(DlnaAudioFormat.Wav)) return DlnaAudioFormat.Wav;
        throw new NotSupportedException("This renderer requires a compressed format; P3 supports LPCM and WAV.");
    }

    private static bool SupportsFormat(string protocol, DlnaAudioFormat format)
    {
        var fields = protocol.Split(':', 4);
        if (fields.Length != 4 || !fields[0].Equals("http-get", StringComparison.OrdinalIgnoreCase) && fields[0] != "*") return false;
        var mime = fields[2].Split(';', StringSplitOptions.TrimEntries);
        bool wildcard = mime[0] == "*" || mime[0].Equals("audio/*", StringComparison.OrdinalIgnoreCase);
        if (!wildcard && !(format == DlnaAudioFormat.L16 ? mime[0].Equals("audio/L16", StringComparison.OrdinalIgnoreCase)
            : mime[0].Equals("audio/wav", StringComparison.OrdinalIgnoreCase))) return false;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in mime.Skip(1))
        {
            var pair = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !parameters.TryAdd(pair[0], pair[1].Trim('"'))) return false;
        }
        return (!parameters.TryGetValue("rate", out var rate) || rate is "44100" or "*")
            && (!parameters.TryGetValue("channels", out var channels) || channels is "2" or "*");
    }

    internal static string Metadata(Uri stream, DlnaAudioFormat format)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
        return new XElement(didl + "DIDL-Lite", new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "upnp", upnp),
            new XElement(didl + "item", new XAttribute("id", "0"), new XAttribute("parentID", "-1"), new XAttribute("restricted", "1"),
                new XElement(dc + "title", "HomePodCast live audio"), new XElement(upnp + "class", "object.item.audioItem.audioBroadcast"),
                new XElement(didl + "res", new XAttribute("protocolInfo", DlnaAudioFormats.ProtocolInfo(format)), stream.AbsoluteUri)))
            .ToString(SaveOptions.DisableFormatting);
    }
}
