using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HomePodCast.Net;

/// <summary>
/// Read-only status endpoint for the browser extension: GET http://127.0.0.1:47100/v1/status.
/// Loopback only, and no CORS headers, so ordinary web pages cannot read it; the extension's
/// service worker can (its host permission bypasses CORS). A page that rebinds its own host name to
/// 127.0.0.1 (DNS rebinding) would be same-origin with us, so the Host header must name us too.
/// </summary>
public sealed class LocalApi : IDisposable
{
    public const int DefaultPort = 47100;

    private const int MaxHeadBytes = 8192;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly TcpListener _listener;
    private readonly Func<object> _status;
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Port { get; }

    public LocalApi(int port, Func<object> status)
    {
        _status = status;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
        Log.Info($"local API on http://127.0.0.1:{Port}/v1/status");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return; // disposed
            }
            catch (Exception ex)
            {
                // One failed accept (a client that reset before it was taken, a socket hiccup) must not end the API.
                Log.Debug($"local API: accept: {ex.Message}");
                try { await Task.Delay(100, _cts.Token); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Handle(client); // never throws: every failure stays with its own request
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(ReadTimeout);
                var head = await ReadHeadAsync(stream, timeout.Token);

                int code = StatusCodeFor(head, Port);
                byte[] body;
                try
                {
                    body = code == 200 ? JsonSerializer.SerializeToUtf8Bytes(_status(), Json) : ErrorBody(code);
                }
                catch (Exception ex)
                {
                    Log.Warn($"local API: status: {ex.Message}");
                    (code, body) = (500, ErrorBody(500));
                }
                var response = $"HTTP/1.1 {code} {Reason(code)}\r\n" +
                               "Content-Type: application/json; charset=utf-8\r\n" +
                               "Cache-Control: no-store\r\n" +
                               $"Content-Length: {body.Length}\r\n" +
                               "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                // The client went away, was too slow, or we are shutting down.
            }
            catch (Exception ex)
            {
                Log.Warn($"local API: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>The request line and headers (up to the blank line, at most 8 KB; whatever came before the client stopped).</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[MaxHeadBytes];
        int n = 0;
        while (n < buf.Length)
        {
            int read = await stream.ReadAsync(buf.AsMemory(n), ct);
            if (read == 0) break;
            n += read;
            if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        return Encoding.Latin1.GetString(buf, 0, n);
    }

    /// <summary>
    /// 200 for GET /v1/status, 404 for anything else; but 403 unless the Host header is exactly 127.0.0.1:port or
    /// localhost:port (what the extension's fetch sends), and 400 for a request with two Host headers.
    /// </summary>
    internal static int StatusCodeFor(string head, int port)
    {
        var lines = head.Split("\r\n");
        string? host = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break; // end of the headers
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (host != null) return 400;
            host = line[(colon + 1)..].Trim();
        }
        if (!IsOurHost(host, port)) return 403;
        var parts = lines[0].Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" && parts[1].Split('?')[0] == "/v1/status" ? 200 : 404;
    }

    internal static bool IsOurHost(string? host, int port) =>
        host != null && (host == $"127.0.0.1:{port}" || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase));

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        _ => "Internal Server Error",
    };

    private static byte[] ErrorBody(int code) => Encoding.UTF8.GetBytes(code switch
    {
        400 => "{\"error\":\"bad request\"}",
        403 => "{\"error\":\"forbidden host\"}",
        404 => "{\"error\":\"not found\"}",
        _ => "{\"error\":\"internal error\"}",
    });

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
