using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>Read-only receiver information on its advertised port. Neither request starts a session.</summary>
public static class SpeakerInfoReader
{
    public static Task<IReadOnlyDictionary<string, object?>> ReadAsync(IPAddress address, CancellationToken ct) =>
        ReadAsync(address, 7000, ct);

    public static async Task<IReadOnlyDictionary<string, object?>> ReadAsync(IPAddress address, int port, CancellationToken ct)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        try
        {
            using var rtsp = await RtspConnection.ConnectAsync(address, port, ct);
            using var close = ct.Register(rtsp.Dispose);
            var reply = await Task.Run(() => rtsp.Rtsp("GET", "/info"), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (reply.IsSuccess && reply.Body.Length > 0) return Parse(reply.Body);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or FormatException or ArgumentException or XmlException) { }
        ct.ThrowIfCancellationRequested();
        // Some older receivers return an empty /info or close an RTSP connection. Use fresh HTTP for the fallback.
        using var http = await RtspConnection.ConnectAsync(address, port, ct);
        using var cancel = ct.Register(http.Dispose);
        var fallback = await Task.Run(() => http.Http("GET", "/server-info", RtspConnection.UserAgent, null, null,
            [new("Host", $"{address}:{port}"), new("Connection", "close")]), CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (!fallback.IsSuccess || fallback.Body.Length == 0) throw new AirPlayException($"GET /server-info: {fallback.StartLine}");
        return Parse(fallback.Body);
    }

    public static IReadOnlyDictionary<string, object?> Parse(byte[] body)
    {
        if (body.AsSpan().StartsWith("bplist00"u8)) return BPlist.ReadDict(body);
        using var input = new MemoryStream(body);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024,
        });
        var plist = XDocument.Load(reader);
        var root = plist.Root;
        var dict = root?.Name.LocalName == "plist" ? root.Elements().SingleOrDefault() : root;
        if (dict?.Name.LocalName != "dict") throw new FormatException("receiver info is not a plist dictionary");
        return ReadDict(dict);
    }

    private static Dictionary<string, object?> ReadDict(XElement dict)
    {
        var children = dict.Elements().ToArray();
        if (children.Length % 2 != 0) throw new FormatException("unpaired plist key");
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < children.Length; i += 2)
        {
            if (children[i].Name.LocalName != "key") throw new FormatException("plist key expected");
            result.Add(children[i].Value, ReadValue(children[i + 1]));
        }
        return result;
    }

    private static object? ReadValue(XElement node) => node.Name.LocalName switch
    {
        "dict" => ReadDict(node),
        "array" => node.Elements().Select(ReadValue).ToList(),
        "string" or "date" => node.Value,
        "integer" => long.Parse(node.Value, CultureInfo.InvariantCulture),
        "real" => double.Parse(node.Value, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(node.Value),
        _ => throw new FormatException("unsupported plist value: " + node.Name.LocalName),
    };
}
