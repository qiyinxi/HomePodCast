using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>Loopback-only renderer: records control requests without any real discovery or audio.</summary>
internal sealed class FakeDlnaRenderer : IAsyncDisposable
{
    internal sealed record Request(string Method, string Path, string SoapAction, string Body);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _accept;
    internal ConcurrentQueue<Request> Requests { get; } = [];
    internal ConcurrentDictionary<string, int> Faults { get; } = new();
    internal ConcurrentDictionary<string, TimeSpan> Delays { get; } = new();
    internal ConcurrentDictionary<string, string> ResponseOverrides { get; } = new();
    internal string ProtocolInfo { get; set; } = "http-get:*:audio/L16;rate=44100;channels=2:*,http-get:*:audio/wav:*";
    internal string CurrentUri { get; set; } = "";
    internal string TransportState { get; set; } = "STOPPED";
    internal int Volume { get; set; } = 30;
    internal bool Muted { get; set; }
    internal string? DescriptionOverride { get; set; }
    internal Func<Uri, CancellationToken, Task>? OnPlayAsync { get; set; }
    internal Func<string, CancellationToken, Task>? AfterActionAsync { get; set; }
    internal Uri DescriptionUri { get; }
    internal DlnaDevice Device => DlnaDevice.ParseDescription(Encoding.UTF8.GetBytes(Description), IPAddress.Loopback, DescriptionUri);
    internal string Description => DescriptionOverride ?? DlnaDiscoveryTests.Description(
        DlnaDiscoveryTests.Service("AVTransport", "/transport", 2)
        + DlnaDiscoveryTests.Service("ConnectionManager", "/connection")
        + DlnaDiscoveryTests.Service("RenderingControl", "/rendering"));

    internal FakeDlnaRenderer()
    {
        _listener.Start();
        DescriptionUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/description.xml");
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (InvalidOperationException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, path, headers, body) = await ReadRequestAsync(stream, _stop.Token);
                string soapAction = headers.GetValueOrDefault("SOAPAction", "").Trim('"');
                Requests.Enqueue(new(method, path, soapAction, body));
                if (method == "GET" && path == "/description.xml")
                {
                    if (Delays.TryGetValue("Description", out var descriptionDelay)) await Task.Delay(descriptionDelay, _stop.Token);
                    await ReplyAsync(stream, 200, Description, _stop.Token);
                    return;
                }
                string action = soapAction[(soapAction.LastIndexOf('#') + 1)..];
                string service = soapAction.Contains('#') ? soapAction[..soapAction.LastIndexOf('#')] : "";
                if (Delays.TryGetValue(action, out var delay)) await Task.Delay(delay, _stop.Token);
                if (Faults.TryGetValue(action, out int code))
                {
                    await ReplyAsync(stream, 500, Envelope(new XElement(XName.Get("Fault", Soap),
                        new XElement("faultcode", "s:Client"), new XElement("faultstring", "UPnPError"),
                        new XElement("detail", new XElement(XName.Get("UPnPError", "urn:schemas-upnp-org:control-1-0"),
                            new XElement("errorCode", code), new XElement("errorDescription", "Fake fault"))))), _stop.Token);
                    return;
                }
                if (ResponseOverrides.TryGetValue(action, out string? responseOverride))
                {
                    await ReplyAsync(stream, 200, responseOverride, _stop.Token);
                    return;
                }
                var request = XDocument.Parse(body).Descendants().First(e => e.Name.LocalName == action);
                string Arg(string name) => request.Elements().First(e => e.Name.LocalName == name).Value;
                var result = new XElement(XName.Get(action + "Response", service));
                switch (action)
                {
                    case "GetProtocolInfo": result.Add(new XElement("Source", ""), new XElement("Sink", ProtocolInfo)); break;
                    case "GetTransportInfo": result.Add(new XElement("CurrentTransportState", TransportState),
                        new XElement("CurrentTransportStatus", "OK"), new XElement("CurrentSpeed", "1")); break;
                    case "GetMediaInfo": result.Add(new XElement("CurrentURI", CurrentUri)); break;
                    case "SetAVTransportURI": CurrentUri = Arg("CurrentURI"); TransportState = "STOPPED"; break;
                    case "Play":
                        TransportState = "PLAYING";
                        if (OnPlayAsync is { } callback) await callback(new Uri(CurrentUri), _stop.Token);
                        break;
                    case "Stop": TransportState = "STOPPED"; break;
                    case "GetVolume": result.Add(new XElement("CurrentVolume", Volume)); break;
                    case "SetVolume": Volume = int.Parse(Arg("DesiredVolume"), CultureInfo.InvariantCulture); break;
                    case "GetMute": result.Add(new XElement("CurrentMute", Muted ? "1" : "0")); break;
                    case "SetMute": Muted = Arg("DesiredMute") == "1"; break;
                    default: throw new InvalidOperationException($"Unexpected fake action {action}.");
                }
                if (AfterActionAsync is { } afterAction) await afterAction(action, _stop.Token);
                await ReplyAsync(stream, 200, Envelope(result), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { } // A cancellation test deliberately closes the client before the reply.
        }
    }

    internal const string Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    internal static string Envelope(XElement body) => new XDocument(new XElement(XName.Get("Envelope", Soap),
        new XAttribute(XNamespace.Xmlns + "s", Soap), new XElement(XName.Get("Body", Soap), body))).ToString(SaveOptions.DisableFormatting);

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        using var header = new MemoryStream();
        var one = new byte[1];
        int terminator = 0;
        while (terminator != 4)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Incomplete fake HTTP header.");
            header.WriteByte(one[0]);
            if (header.Length > 32 * 1024) throw new InvalidDataException("Oversized fake HTTP header.");
            terminator = one[0] == (terminator is 0 or 2 ? '\r' : '\n') ? terminator + 1 : one[0] == '\r' ? 1 : 0;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var start = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1).Where(l => l.Contains(':')))
        {
            int colon = line.IndexOf(':');
            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }
        int length = headers.TryGetValue("Content-Length", out string? text) ? int.Parse(text, CultureInfo.InvariantCulture) : 0;
        if (length is < 0 or > DlnaXml.MaxBytes) throw new InvalidDataException("Oversized fake HTTP body.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (start[0], start[1], headers, Encoding.UTF8.GetString(body));
    }

    private static async Task ReplyAsync(NetworkStream stream, int status, string body, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n"
            + $"Content-Type: text/xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), ct);
        await stream.WriteAsync(bytes, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
