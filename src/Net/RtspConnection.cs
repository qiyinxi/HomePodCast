using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

public sealed class HttpMessage
{
    public required string StartLine { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }

    /// <summary>Status code for responses ("RTSP/1.0 200 OK"); 0 for requests.</summary>
    public int StatusCode
    {
        get
        {
            var parts = StartLine.Split(' ', 3);
            return parts.Length >= 2 && parts[0].Contains('/') && int.TryParse(parts[1], out var c) ? c : 0;
        }
    }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";

    /// <summary>Parse one complete message from the front of buffer; returns null if incomplete.</summary>
    public static HttpMessage? TryParse(List<byte> buffer)
    {
        int headerEnd = IndexOf(buffer, "\r\n\r\n"u8);
        if (headerEnd < 0) return null;
        var head = Encoding.UTF8.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        int total = headerEnd + 4 + length;
        if (buffer.Count < total) return null;
        var body = buffer.GetRange(headerEnd + 4, length).ToArray();
        buffer.RemoveRange(0, total);
        return new HttpMessage { StartLine = head[0], Headers = headers, Body = body };
    }

    private static int IndexOf(List<byte> buffer, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= buffer.Count; i++)
        {
            int j = 0;
            while (j < needle.Length && buffer[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    public static byte[] Format(string startLine, IEnumerable<KeyValuePair<string, string>> headers, byte[]? body)
    {
        var sb = new StringBuilder(startLine);
        foreach (var (k, v) in headers) sb.Append("\r\n").Append(k).Append(": ").Append(v);
        sb.Append("\r\n\r\n");
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        return body is { Length: > 0 } ? [.. head, .. body] : head;
    }
}

/// <summary>
/// The AirPlay control connection (TCP 7000). Request/response, plaintext until pairing completes,
/// then HAP-encrypted. Thread-safe: requests are serialized.
/// </summary>
public sealed class RtspConnection : IDisposable
{
    public const string UserAgent = "AirPlay/550.10";

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _lock = new();
    private readonly List<byte> _rx = new();
    private readonly byte[] _readBuf = new byte[16384];
    private HapSession? _session;
    private int _cseq;

    public IPAddress LocalIp { get; }
    public IPAddress RemoteIp { get; }
    public uint SessionId { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public string DacpId { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    public uint ActiveRemote { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    public string Uri => $"rtsp://{LocalIp}/{SessionId}";

    private RtspConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _tcp.ReceiveTimeout = 5000;
        _tcp.SendTimeout = 5000;
        _stream = tcp.GetStream();
        LocalIp = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address.MapToIPv4();
        RemoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.MapToIPv4();
    }

    public static async Task<RtspConnection> ConnectAsync(IPAddress host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return new RtspConnection(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void EnableEncryption(byte[] outputKey, byte[] inputKey)
    {
        lock (_lock) _session = new HapSession(outputKey, inputKey);
    }

    /// <summary>RTSP request carrying the session headers (CSeq, DACP-ID, ...).</summary>
    public HttpMessage Rtsp(string method, string? uri = null, string? contentType = null, byte[]? body = null,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        lock (_lock)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new("CSeq", (_cseq++).ToString()),
                new("DACP-ID", DacpId),
                new("Active-Remote", ActiveRemote.ToString()),
                new("Client-Instance", DacpId),
            };
            if (extra != null) headers.AddRange(extra);
            return Send(method, uri ?? Uri, "RTSP/1.0", UserAgent, contentType, body, headers);
        }
    }

    public HttpMessage RtspPlist(string method, Dictionary<string, object?> plist, string? uri = null) =>
        Rtsp(method, uri, "application/x-apple-binary-plist", BPlist.Write(plist));

    /// <summary>Plain HTTP/1.1 request (used for pair-setup).</summary>
    public HttpMessage Http(string method, string path, string userAgent, string? contentType, byte[]? body,
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        lock (_lock) return Send(method, path, "HTTP/1.1", userAgent, contentType, body, headers.ToList());
    }

    private HttpMessage Send(string method, string uri, string protocol, string userAgent, string? contentType,
        byte[]? body, List<KeyValuePair<string, string>> headers)
    {
        var all = new List<KeyValuePair<string, string>> { new("User-Agent", userAgent) };
        if (contentType != null) all.Add(new("Content-Type", contentType));
        if (body is { Length: > 0 }) all.Add(new("Content-Length", body.Length.ToString()));
        all.AddRange(headers);

        var raw = HttpMessage.Format($"{method} {uri} {protocol}", all, body);
        var wire = _session?.Encrypt(raw) ?? raw;
        _stream.Write(wire);
        return ReadResponse();
    }

    private HttpMessage ReadResponse()
    {
        while (true)
        {
            var msg = HttpMessage.TryParse(_rx);
            if (msg != null) return msg;
            int n = _stream.Read(_readBuf, 0, _readBuf.Length);
            if (n == 0) throw new IOException("connection closed by speaker");
            var chunk = _readBuf.AsSpan(0, n);
            _rx.AddRange(_session != null ? _session.Decrypt(chunk) : chunk.ToArray());
        }
    }

    public void Dispose()
    {
        try { _tcp.Close(); } catch { }
        _session?.Dispose();
    }
}
