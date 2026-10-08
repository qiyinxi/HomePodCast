using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HomePodCast.Players;

namespace HomePodCast.Tests;

/// <summary>
/// VLC's web interface as far as status.xml goes, on a loopback port this test process owns: Basic auth with
/// an empty user (403 when it has no password, 401 for a wrong one), and the audiodelay command.
/// </summary>
internal sealed class FakeVlc : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _password;

    public FakeVlc(string password, double delaySeconds = 0, bool playing = true)
    {
        _password = password;
        Delay = delaySeconds;
        Playing = playing;
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    /// <summary>The audio delay in seconds; setting it is like the user changing it in VLC.</summary>
    public double Delay { get; set; }
    public bool Playing { get; set; }
    public ConcurrentQueue<(string RequestLine, string? Authorization)> Requests { get; } = new();

    public static string AuthorizationFor(string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password));

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync() ?? "";
        string? auth = null;
        while (await reader.ReadLineAsync() is { Length: > 0 } header)
            if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = header[14..].Trim();
        Requests.Enqueue((requestLine, auth));

        string status, body;
        if (_password.Length == 0)
        {
            (status, body) = ("403 Forbidden", "<html>Password for Web interface has not been set.</html>");
        }
        else if (auth != AuthorizationFor(_password))
        {
            (status, body) = ("401 Unauthorized", "<html>401</html>");
        }
        else
        {
            var m = Regex.Match(requestLine, @"command=audiodelay&val=([-0-9.]+)");
            if (m.Success && Playing) Delay = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            status = "200 OK";
            body = Playing
                ? $"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\" ?><root><audiodelay>{Delay.ToString(CultureInfo.InvariantCulture)}</audiodelay><currentplid>3</currentplid><state>playing</state></root>"
                : "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\" ?><root><currentplid>-1</currentplid><state>stopped</state></root>";
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/xml\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>A scanner that always finds the same endpoints.</summary>
internal sealed class FixedScanner(params IPlayerEndpoint[] endpoints) : IPlayerScanner
{
    public Task<PlayerScan> ScanAsync(CancellationToken ct) => Task.FromResult(new PlayerScan(endpoints, []));
}
