using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Net;

internal sealed record DlnaStreamOptions(IPAddress LocalAddress, IPAddress RendererAddress,
    DlnaAudioFormat Format, DlnaPcmReader ReadAudio, Func<bool> IsPrivateNetwork)
{
    public int Port { get; init; }
    public DlnaHttpFraming Framing { get; init; } = DlnaHttpFraming.Chunked;
    // Compatibility with renderers that refuse chunking. This is a finite response, not an infinite WAV.
    public long LargeContentLength { get; init; } = 2_147_483_644;
}

/// <summary>A session-scoped, sender-paced HTTP PCM endpoint for one explicitly selected renderer.</summary>
internal sealed class DlnaStreamServer : IDisposable, IAsyncDisposable
{
    private const int MaxHeaderBytes = 8192;
    private const int MaxLineBytes = 2048;
    private const int MaxHeaderCount = 32;
    private const int MaxConnections = 16;
    private readonly DlnaStreamOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly TaskCompletionSource<long> _firstGet = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<long> _firstPcm = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _path;
    private readonly object _lifecycle = new();
    private TcpListener? _listener;
    private Uri? _url;
    private int _disposed;
    private int _activeGet;
    private long _bytesSent, _framesSent, _framesSkipped, _writeTicks, _maxWriteTicks, _pendingWrite;

    public DlnaStreamServer(DlnaStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateAddress(options.LocalAddress, nameof(options.LocalAddress));
        ValidateAddress(options.RendererAddress, nameof(options.RendererAddress));
        if (options.LocalAddress.AddressFamily != options.RendererAddress.AddressFamily)
            throw new ArgumentException("The selected local and renderer addresses must use the same address family.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.ReadAudio);
        ArgumentNullException.ThrowIfNull(options.IsPrivateNetwork);
        _ = DlnaAudioFormats.ContentType(options.Format);
        if (!Enum.IsDefined(options.Framing)) throw new ArgumentOutOfRangeException(nameof(options.Framing));
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.LargeContentLength < 44 || options.LargeContentLength % 4 != 0
            || options.LargeContentLength > DlnaAudioFormats.MaxWavDataLength + 44L)
            throw new ArgumentOutOfRangeException(nameof(options.LargeContentLength));
        _options = options;
        _path = $"/stream/{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.{DlnaAudioFormats.Extension(options.Format)}";
    }

    public Uri Url => _url ?? throw new InvalidOperationException("Start the stream server before reading its URL.");
    /// <summary>PCM payload only; excludes WAV, HTTP and chunk headers.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesSkipped => Interlocked.Read(ref _framesSkipped);
    public Task<long> FirstGetTicks => _firstGet.Task;
    /// <summary>Planned media-clock timestamp of the first PCM callback, not renderer/acoustic latency.</summary>
    public Task<long> FirstPcmTicks => _firstPcm.Task;
    public long TotalWriteTicks => Interlocked.Read(ref _writeTicks);
    public long MaxWriteTicks => Interlocked.Read(ref _maxWriteTicks);
    /// <summary>Monotonic start of an outstanding body write, or zero; measures transport backpressure only.</summary>
    public long PendingWriteStartedTicks => Interlocked.Read(ref _pendingWrite);
    public bool IsStopped => Volatile.Read(ref _disposed) != 0;
    public bool IsStreaming => Volatile.Read(ref _activeGet) != 0;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(IsStopped, this);
            if (_listener != null) throw new InvalidOperationException("The stream server has already started.");
            if (!IsPrivate())
            {
                Dispose();
                throw new InvalidOperationException("DLNA streaming requires a private network.");
            }
            var listener = new TcpListener(_options.LocalAddress, _options.Port);
            listener.Start(MaxConnections);
            _listener = listener;
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _url = new UriBuilder("http", _options.LocalAddress.ToString(), port, _path).Uri;
            _ = AcceptLoopAsync(listener);
            _ = MonitorPolicyAsync();
        }
    }

    private static void ValidateAddress(IPAddress address, string name)
    {
        ArgumentNullException.ThrowIfNull(address, name);
        if (address.IsIPv4MappedToIPv6 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None) || address.IsIPv6Multicast)
            throw new ArgumentException("Select a unicast address.", name);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] is 0 or >= 224 || bytes[3] == 255)
                throw new ArgumentException("Select a unicast address, not a multicast or broadcast address.", name);
        }
        else if (address.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("Select an IPv4 or IPv6 address.", name);
    }

    private bool IsPrivate()
    {
        try { return _options.IsPrivateNetwork(); }
        catch { return false; } // A failed policy lookup must never expose the endpoint.
    }

    private async Task MonitorPolicyAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                if (!IsPrivate()) { Dispose(); return; }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (IsStopped) { client.Dispose(); break; }
                // Reject other peers before parsing headers or touching the audio source.
                if (client.Client.RemoteEndPoint is not IPEndPoint peer || !peer.Address.Equals(_options.RendererAddress))
                {
                    client.Dispose();
                    continue;
                }
                if (!IsPrivate()) { client.Dispose(); Dispose(); break; }
                if (_clients.Count >= MaxConnections) { client.Dispose(); continue; }
                _clients.TryAdd(client, 0);
                // Dispose can race the insertion; never leave an accepted socket behind it.
                if (IsStopped) { _clients.TryRemove(client, out _); client.Dispose(); break; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        bool ownsGet = false;
        try
        {
            client.NoDelay = true;
            client.SendBufferSize = 16 * 1024;
            using var stream = client.GetStream();
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await ReadRequestAsync(stream, headerTimeout.Token).ConfigureAwait(false);
            int error = request.Error;
            if (error == 0 && !string.Equals(request.Host, Url.Authority, StringComparison.OrdinalIgnoreCase)) error = 400;
            if (error == 0 && request.Path != _path) error = 404;
            if (error == 0 && request.Method is not ("GET" or "HEAD")) error = 405;
            if (error == 0 && !IsPrivate()) { Dispose(); return; }
            if (error == 0 && request.Method == "GET")
            {
                ownsGet = Interlocked.CompareExchange(ref _activeGet, 1, 0) == 0;
                if (!ownsGet) error = 409;
            }
            if (error != 0)
            {
                await WriteErrorAsync(stream, error, _stop.Token).ConfigureAwait(false);
                return;
            }
            if (ownsGet) _firstGet.TrySetResult(MediaClock.Now);
            await stream.WriteAsync(ResponseHeaders(), _stop.Token).ConfigureAwait(false);
            if (ownsGet) await StreamPcmAsync(stream, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception) { /* A failing source ends this GET; it must not fault an unobserved listener task. */ }
        finally
        {
            client.Dispose();
            if (ownsGet) Interlocked.Exchange(ref _activeGet, 0);
            _clients.TryRemove(client, out _);
        }
    }

    private byte[] ResponseHeaders()
    {
        string framing = _options.Framing == DlnaHttpFraming.Chunked ? "Transfer-Encoding: chunked\r\n"
            : $"Content-Length: {_options.LargeContentLength.ToString(CultureInfo.InvariantCulture)}\r\n";
        return Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {DlnaAudioFormats.ContentType(_options.Format)}\r\n"
            + "Connection: close\r\nCache-Control: no-store\r\nAccept-Ranges: none\r\ntransferMode.dlna.org: Streaming\r\n"
            + $"contentFeatures.dlna.org: {DlnaAudioFormats.ContentFeatures}\r\n{framing}\r\n");
    }

    private async Task StreamPcmAsync(NetworkStream stream, CancellationToken ct)
    {
        bool wav = _options.Format == DlnaAudioFormat.Wav;
        long remaining = _options.Framing == DlnaHttpFraming.LargeContentLength
            ? _options.LargeContentLength - (wav ? 44 : 0) : wav ? DlnaAudioFormats.MaxWavDataLength : long.MaxValue;
        if (wav) await WriteBodyAsync(stream, DlnaAudioFormats.WavHeader((uint)remaining), ct).ConfigureAwait(false);
        short[] pcm = new short[DlnaAudioFormats.FramesPerChunk * 2];
        byte[] bytes = new byte[pcm.Length * 2];
        long epoch = MediaClock.Now, frame = 0;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            long planned = epoch + (long)((Int128)frame * MediaClock.Frequency / DlnaAudioFormats.SampleRate);
            await WaitUntilAsync(planned, ct).ConfigureAwait(false);
            Array.Clear(pcm);
            _options.ReadAudio(planned, pcm);
            ct.ThrowIfCancellationRequested();
            _firstPcm.TrySetResult(planned);
            int count = (int)Math.Min(bytes.Length, remaining);
            for (int i = 0; i < count / 2; i++)
            {
                if (wav) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), pcm[i]);
                else BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), pcm[i]);
            }
            await WriteBodyAsync(stream, bytes.AsMemory(0, count), ct).ConfigureAwait(false);
            Interlocked.Add(ref _bytesSent, count);
            Interlocked.Add(ref _framesSent, count / DlnaAudioFormats.BytesPerFrame);
            frame += DlnaAudioFormats.FramesPerChunk;
            remaining -= count;
            // A stalled HTTP receiver must not make the source replay stale deadlines in a catch-up burst.
            long elapsedFrames = (long)((Int128)(MediaClock.Now - epoch) * DlnaAudioFormats.SampleRate / MediaClock.Frequency);
            if (elapsedFrames > frame + DlnaAudioFormats.FramesPerChunk)
            {
                long next = (elapsedFrames / DlnaAudioFormats.FramesPerChunk + 1) * DlnaAudioFormats.FramesPerChunk;
                Interlocked.Add(ref _framesSkipped, next - frame);
                frame = next;
            }
        }
        if (_options.Framing == DlnaHttpFraming.Chunked)
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    private static async Task WaitUntilAsync(long deadline, CancellationToken ct)
    {
        while (true)
        {
            long remaining = deadline - MediaClock.Now;
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, MediaClock.ToMs(remaining))), ct).ConfigureAwait(false);
        }
    }

    private async Task WriteBodyAsync(NetworkStream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        long start = MediaClock.Now;
        Interlocked.Exchange(ref _pendingWrite, start);
        try
        {
            if (_options.Framing == DlnaHttpFraming.Chunked)
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:X}\r\n"), ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            if (_options.Framing == DlnaHttpFraming.Chunked)
                await stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }
        finally
        {
            long elapsed = MediaClock.Now - start;
            Interlocked.Add(ref _writeTicks, elapsed);
            long old;
            do { old = Interlocked.Read(ref _maxWriteTicks); }
            while (elapsed > old && Interlocked.CompareExchange(ref _maxWriteTicks, elapsed, old) != old);
            Interlocked.Exchange(ref _pendingWrite, 0);
        }
    }

    private readonly record struct Request(string Method, string Path, string Host, int Error = 0);

    private static async Task<Request> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int count = 0, lineStart = 0, lines = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) return new("", "", "", 400);
            int previous = count;
            count += read;
            for (int i = previous; i < count; i++)
            {
                byte b = buffer[i];
                if (i > 0 && buffer[i - 1] == 13 && b != 10) return new("", "", "", 400);
                if (b > 126 || b < 32 && b is not (13 or 10)) return new("", "", "", 400);
                if (i - lineStart > MaxLineBytes) return new("", "", "", 431);
                if (b != 10) continue;
                if (i == 0 || buffer[i - 1] != 13) return new("", "", "", 400);
                if (++lines > MaxHeaderCount + 2) return new("", "", "", 431);
                if (i - lineStart == 1)
                {
                    // No request body or pipelined request is accepted, even when it arrived in the same read.
                    if (i != count - 1) return new("", "", "", 400);
                    return ParseRequest(Encoding.ASCII.GetString(buffer, 0, count));
                }
                lineStart = i + 1;
            }
        }
        return new("", "", "", 431);
    }

    private static Request ParseRequest(string text)
    {
        string[] lines = text.Split("\r\n", StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[0].Length == 0 || first[1].Length == 0 || first[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            return new("", "", "", 400);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length - 2; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0 || !lines[i].AsSpan(0, colon).ContainsOnlyHttpToken()) return new("", "", "", 400);
            if (!headers.TryAdd(lines[i][..colon], lines[i][(colon + 1)..].Trim())) return new("", "", "", 400);
        }
        if (!headers.TryGetValue("Host", out string? host) || host.Length == 0 || headers.ContainsKey("Transfer-Encoding")
            || headers.TryGetValue("Content-Length", out string? length) && length != "0") return new("", "", "", 400);
        if (headers.ContainsKey("Range") || headers.ContainsKey("TimeSeekRange.dlna.org")) return new("", "", "", 416);
        return new(first[0], first[1], host);
    }

    private static Task WriteErrorAsync(NetworkStream stream, int status, CancellationToken ct)
    {
        string reason = status switch { 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 416 => "Range Not Satisfiable", 431 => "Request Header Fields Too Large", _ => "Bad Request" };
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n"
            + (status == 405 ? "Allow: GET, HEAD\r\n" : "") + "\r\n"), ct).AsTask();
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _listener?.Stop();
            foreach (TcpClient client in _clients.Keys) client.Dispose();
            _firstGet.TrySetCanceled(_stop.Token);
            _firstPcm.TrySetCanceled(_stop.Token);
        }
    }

    // Source callbacks are user code and may block; shutdown closes sockets without waiting on such callbacks.
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

internal static class DlnaHttpTokens
{
    public static bool ContainsOnlyHttpToken(this ReadOnlySpan<char> value)
    {
        foreach (char c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))) return false;
        return true;
    }
}
