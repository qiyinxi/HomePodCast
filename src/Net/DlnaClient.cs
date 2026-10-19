using System.Net;
using System.Xml.Linq;
using HomePodCast.Audio;

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
    public DlnaTransferPacing Pacing { get; init; } = DlnaTransferPacing.Sender;
    public Func<DlnaAudioFormat, IDlnaAudioEncoder>? EncoderFactory { get; init; }
    public Func<long, CancellationToken, ValueTask>? BeforeReadAsync { get; init; }
    public Action<DlnaWriteObservation>? ObserveWrite { get; init; }
    public Action<long>? OnStreamStarted { get; init; }
    // Group commands may await SOAP. These live sources still attenuate/mute every PCM callback immediately.
    public Func<double>? LiveVolumePercent { get; init; }
    public Func<double>? LiveVolumeCapPercent { get; init; }
    public Func<bool>? LiveMuted { get; init; }
    public Func<bool>? LiveRemoteMuted { get; init; }
}

/// <summary>Owns one explicitly selected renderer, without changing capture, saved settings or firewall rules.</summary>
internal sealed class DlnaClient : IAsyncDisposable
{
    private sealed record GainState(double Gain, double Volume);
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
    private GainState _audioState = new(0, 0);
    private Exception? _lostException;
    private bool _remoteVolume = true, _remoteMute = true, _assigned;
    private string _previousUri = "";
    private DlnaStreamServer? _server;
    private long _lastPayloadFrames, _lastPayloadProgressTicks;

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
    public Exception? LostException => Volatile.Read(ref _lostException);

    public static async Task<DlnaClient> ConnectAsync(DlnaDevice device, DlnaClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ReadAudio);
        ArgumentNullException.ThrowIfNull(options.IsPrivateNetwork);
        ValidateVolume(options.VolumePercent, options.VolumeCapPercent);
        if (options.PreferredFormat is { } format && !Enum.IsDefined(format) || !Enum.IsDefined(options.Framing)
            || !Enum.IsDefined(options.Pacing))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PollInterval < TimeSpan.FromMilliseconds(20) || options.PollInterval > TimeSpan.FromSeconds(30)
            || options.PullTimeout < TimeSpan.FromMilliseconds(20) || options.PullTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options));
        ct.ThrowIfCancellationRequested();
        if (!IsPrivate(options)) throw new DlnaPolicyException();
        var client = new DlnaClient(device, options, ct);
        try
        {
            await client.InitializeAsync().ConfigureAwait(false);
            return client;
        }
        catch
        {
            Exception? failure = client.StreamFailure();
            await client.DisposeAsync().ConfigureAwait(false);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
            throw new DlnaBusyException();
        _previousUri = previousUri;
        Format = SelectFormat(await _control.GetProtocolInfoAsync(ct).ConfigureAwait(false), _options.PreferredFormat);
        // During explicit takeover, never unmute the previous content: an unsupported volume action
        // only attenuates our PCM and cannot cap somebody else's currently playing media.
        await SetVolumeCoreAsync(_options.VolumePercent, true, ct, honorLive: false).ConfigureAwait(false);
        _server = new DlnaStreamServer(new(_options.LocalAddress, _rendererAddress, Format, ReadAudio, _options.IsPrivateNetwork)
        {
            Framing = _options.Framing,
            Pacing = _options.Pacing, EncoderFactory = _options.EncoderFactory,
            BeforeReadAsync = _options.BeforeReadAsync, ObserveWrite = _options.ObserveWrite,
            OnStreamStarted = _options.OnStreamStarted,
        });
        _server.Start();
        // Some renderers prefetch before SetAVTransportURI/Play returns. Serve bounded PCM immediately;
        // waiting for the SOAP reply here would deadlock those renderers.
        await RequireOwnershipAsync(ct).ConfigureAwait(false);
        _assigned = true;
        await _control.SetAvTransportUriAsync(StreamUri, Metadata(StreamUri, Format, _options.Pacing), ct).ConfigureAwait(false);
        await SetVolumeCoreAsync(_options.VolumePercent, _options.Muted, ct, honorLive: false).ConfigureAwait(false);
        await _control.PlayAsync(ct).ConfigureAwait(false);
        await StreamServer.FirstGetTicks.WaitAsync(_options.PullTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _lastPayloadFrames = StreamServer.FramesSent;
        _lastPayloadProgressTicks = MediaClock.Now;
        _monitor = MonitorAsync();
    }

    public Task SetVolumeAsync(double percent, bool muted, CancellationToken ct = default) =>
        SetVolumeCoreAsync(percent, muted, ct, honorLive: true);

    private async Task SetVolumeCoreAsync(double percent, bool muted, CancellationToken ct, bool honorLive)
    {
        ValidateVolume(percent, _options.VolumeCapPercent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Silence();
            double desired = percent;
            if (honorLive && _options.LiveVolumePercent is { } live)
                try { desired = Normalize(live()); } catch { desired = 0; }
            double capped = Math.Min(desired, CurrentCap());
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
                    await _control.SetMuteAsync(CurrentRemoteMute(muted, honorLive), linked.Token).ConfigureAwait(false);
                }
                catch (DlnaActionException ex) when (ex.IsUnsupportedAction) { _remoteMute = false; }
            await RequireOwnershipAsync(linked.Token).ConfigureAwait(false);
            // Remote and digital volume are alternatives. Digital mute remains available even when
            // the renderer omits RenderingControl; authentication/network errors never trigger fallback.
            Volatile.Write(ref _audioState, new(CurrentRemoteMute(muted, honorLive) ? 0 : _remoteVolume ? 1 : capped / 100, capped));
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
            var error = new DlnaBusyException("The DLNA renderer became busy; explicit takeover is required.");
            Lose(error);
            throw error;
        }
        if (await _control.GetMediaInfoAsync(ct).ConfigureAwait(false) == StreamUri.AbsoluteUri) return;
        var takenOver = new DlnaTakenOverException();
        Lose(takenOver);
        throw takenOver;
    }

    private void ReadAudio(long ticks, Span<short> stereo)
    {
        _options.ReadAudio(ticks, stereo);
        var state = Volatile.Read(ref _audioState);
        double gain = state.Gain;
        try
        {
            double cap = CurrentCap();
            double desired = _options.LiveVolumePercent is { } live ? Normalize(live()) : state.Volume;
            desired = Math.Min(desired, cap);
            if (_options.LiveMuted?.Invoke() == true) gain = 0;
            // Never amplify to compensate for an unfinished remote command. A lowered cap/master takes effect
            // in this callback even while a previous SetVolume/SetMute reply is still outstanding.
            double applied = state.Volume;
            if (applied > 0) gain *= Math.Min(1, desired / applied);
            else gain = 0;
        }
        catch { gain = 0; }
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
                if (!IsPrivate(_options) || StreamServer.IsPolicyClosed) { Lose(new DlnaPolicyException()); return; }
                if (StreamServer.IsStopped)
                {
                    Lose(StreamFailure() ?? new IOException("DLNA stream endpoint closed.")); return;
                }
                string uri = await _control.GetMediaInfoAsync(_stop.Token).ConfigureAwait(false);
                if (uri != StreamUri.AbsoluteUri) { Lose(new DlnaTakenOverException()); return; }
                string state = await _control.GetTransportInfoAsync(_stop.Token).ConfigureAwait(false);
                if (state is not ("PLAYING" or "TRANSITIONING")) { Lose(new DlnaStoppedException($"DLNA renderer stopped ({state}).")); return; }
                long now = MediaClock.Now, frames = StreamServer.FramesSent;
                if (frames != _lastPayloadFrames)
                {
                    _lastPayloadFrames = frames;
                    _lastPayloadProgressTicks = now;
                }
                else if (now - _lastPayloadProgressTicks >= MediaClock.FromMs(_options.PullTimeout.TotalMilliseconds))
                {
                    Lose(new IOException(StreamServer.IsStreaming ? "DLNA HTTP audio pull stalled." : "DLNA renderer stopped requesting audio."));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            if (Volatile.Read(ref _stopping) == 0) Lose(new IOException("DLNA session cancelled."));
        }
        catch (Exception ex) { Lose(ex); }
    }

    private void Lose(Exception error)
    {
        if (Interlocked.Exchange(ref _losing, 1) != 0) return;
        Silence();
        Volatile.Write(ref _lostException, error);
        _server?.Dispose();
        _stop.Cancel();
        _lost.TrySetResult(error.Message);
    }

    public Task StopAsync() => DisposeAsync().AsTask();

    /// <summary>Synchronously close local audio/HTTP before any bounded remote cleanup is scheduled.</summary>
    public void StopLocal()
    {
        Interlocked.Exchange(ref _stopping, 1);
        Silence();
        _server?.Dispose();
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        StopLocal();
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

    private static double Normalize(double percent) => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
    private void Silence() => Volatile.Write(ref _audioState, new(0, Volatile.Read(ref _audioState).Volume));
    private bool CurrentRemoteMute(bool requested, bool honorLive)
    {
        if (!honorLive || _options.LiveRemoteMuted == null) return requested;
        try { return _options.LiveRemoteMuted(); } catch { return true; }
    }
    private Exception? StreamFailure() => _server?.IsPolicyClosed == true ? new DlnaPolicyException()
        : _server?.Failure is NotSupportedException unsupported ? new DlnaUnsupportedException(unsupported.Message)
        : _server?.Failure;
    private double CurrentCap()
    {
        try { return Math.Min(_options.VolumeCapPercent, Normalize(_options.LiveVolumeCapPercent?.Invoke() ?? _options.VolumeCapPercent)); }
        catch { return 0; }
    }
    private static bool IsPrivate(DlnaClientOptions options)
    {
        try { return options.IsPrivateNetwork(); } catch { return false; }
    }

    internal static DlnaAudioFormat SelectFormat(IEnumerable<string> protocols, DlnaAudioFormat? preferred = null)
    {
        var formats = protocols.ToArray();
        bool Supports(DlnaAudioFormat format) => formats.Any(p => SupportsFormat(p, format));
        if (preferred is { } chosen)
            return Supports(chosen) ? chosen : throw new DlnaUnsupportedException($"The renderer does not advertise {chosen}.");
        if (Supports(DlnaAudioFormat.L16)) return DlnaAudioFormat.L16;
        if (Supports(DlnaAudioFormat.Wav)) return DlnaAudioFormat.Wav;
        if (Supports(DlnaAudioFormat.Mp3)) return DlnaAudioFormat.Mp3;
        if (Supports(DlnaAudioFormat.Aac)) return DlnaAudioFormat.Aac;
        throw new DlnaUnsupportedException("The renderer advertises no supported DLNA audio format.");
    }

    private static bool SupportsFormat(string protocol, DlnaAudioFormat format)
    {
        var fields = protocol.Split(':', 4);
        if (fields.Length != 4 || !fields[0].Equals("http-get", StringComparison.OrdinalIgnoreCase) && fields[0] != "*") return false;
        var mime = fields[2].Split(';', StringSplitOptions.TrimEntries);
        bool wildcard = mime[0] == "*" || mime[0].Equals("audio/*", StringComparison.OrdinalIgnoreCase);
        string contentType = format switch
        {
            DlnaAudioFormat.L16 => "audio/L16", DlnaAudioFormat.Wav => "audio/wav",
            DlnaAudioFormat.Mp3 => "audio/mpeg", DlnaAudioFormat.Aac => "audio/aac", _ => "",
        };
        if (!wildcard && !mime[0].Equals(contentType, StringComparison.OrdinalIgnoreCase)) return false;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in mime.Skip(1))
        {
            var pair = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !parameters.TryAdd(pair[0], pair[1].Trim('"'))) return false;
        }
        return (!parameters.TryGetValue("rate", out var rate) || rate is "44100" or "*")
            && (!parameters.TryGetValue("channels", out var channels) || channels is "2" or "*");
    }

    internal static string Metadata(Uri stream, DlnaAudioFormat format, DlnaTransferPacing pacing = DlnaTransferPacing.Sender)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
        return new XElement(didl + "DIDL-Lite", new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "upnp", upnp),
            new XElement(didl + "item", new XAttribute("id", "0"), new XAttribute("parentID", "-1"), new XAttribute("restricted", "1"),
                new XElement(dc + "title", "HomePodCast live audio"), new XElement(upnp + "class", "object.item.audioItem.audioBroadcast"),
                new XElement(didl + "res", new XAttribute("protocolInfo", DlnaAudioFormats.ProtocolInfo(format, pacing)), stream.AbsoluteUri)))
            .ToString(SaveOptions.DisableFormatting);
    }
}
