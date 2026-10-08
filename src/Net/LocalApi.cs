using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HomePodCast.Net;

/// <summary>
/// Read-only status endpoint for the browser extension: GET http://127.0.0.1:47100/v1/status.
/// Loopback only, and no CORS headers, so ordinary web pages cannot read it; the extension's
/// service worker can (its host permission bypasses CORS).
/// </summary>
public sealed class LocalApi : IDisposable
{
    public const int DefaultPort = 47100;

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
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Handle(client);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                var stream = client.GetStream();
                var buf = new byte[4096];
                int n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                var requestLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
                var parts = requestLine.Split(' ');
                bool ok = parts.Length >= 2 && parts[0] == "GET" && parts[1].Split('?')[0] == "/v1/status";

                var body = ok ? JsonSerializer.SerializeToUtf8Bytes(_status(), Json) : "{\"error\":\"not found\"}"u8.ToArray();
                var head = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\n" +
                           "Content-Type: application/json; charset=utf-8\r\n" +
                           "Cache-Control: no-store\r\n" +
                           $"Content-Length: {body.Length}\r\n" +
                           "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
