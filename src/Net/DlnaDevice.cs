using System.Net;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;

namespace HomePodCast.Net;

internal sealed record DlnaService(string ServiceType, Uri ControlUri);

internal sealed record DlnaDevice(string Udn, string Name, IPAddress Address, Uri DescriptionUri,
    DlnaService AvTransport, DlnaService ConnectionManager, DlnaService? RenderingControl,
    string? Manufacturer, string? Model)
{
    internal const string MediaRenderer = "urn:schemas-upnp-org:device:MediaRenderer:";

    /// <summary>SSDP and device XML are untrusted: all subsequent requests stay on the replying peer.</summary>
    internal static DlnaDevice ParseDescription(ReadOnlySpan<byte> xml, IPAddress peerAddress, Uri descriptionUri)
    {
        DlnaHttp.RequirePeerHttp(descriptionUri, peerAddress);
        var document = DlnaXml.Parse(xml);
        var root = document.Root ?? throw new FormatException("Missing UPnP root.");
        var renderer = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "device"
            && HasVersion(Value(e, "deviceType"), MediaRenderer));
        if (renderer is null) throw new FormatException("No MediaRenderer in the device description.");
        string udn = Value(renderer, "UDN");
        if (!udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) || udn.Length <= 5 || udn.Length > 256
            || udn.Any(char.IsControl)) throw new FormatException("Missing or invalid renderer UDN.");
        string name = Value(renderer, "friendlyName");
        if (name.Length == 0 || name.Length > 512) throw new FormatException("Missing or invalid renderer name.");
        Uri baseUri = descriptionUri;
        string urlBase = Value(root, "URLBase");
        if (urlBase.Length > 0)
        {
            if (!Uri.TryCreate(urlBase, UriKind.Absolute, out baseUri)) throw new FormatException("Invalid URLBase.");
            DlnaHttp.RequirePeerHttp(baseUri, peerAddress);
        }
        var serviceList = renderer.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList");
        var services = new List<DlnaService>();
        if (serviceList is not null)
            foreach (var service in serviceList.Elements().Where(e => e.Name.LocalName == "service"))
            {
                string type = Value(service, "serviceType");
                if (!HasVersion(type, "urn:schemas-upnp-org:service:AVTransport:")
                    && !HasVersion(type, "urn:schemas-upnp-org:service:ConnectionManager:")
                    && !HasVersion(type, "urn:schemas-upnp-org:service:RenderingControl:")) continue;
                string control = Value(service, "controlURL");
                if (control.Length == 0 || !Uri.TryCreate(baseUri, control, out var uri))
                    throw new FormatException("Missing or invalid service control URL.");
                DlnaHttp.RequirePeerHttp(uri, peerAddress);
                services.Add(new(type, uri));
            }
        DlnaService? Find(string name) => services.FirstOrDefault(s => HasVersion(s.ServiceType,
            $"urn:schemas-upnp-org:service:{name}:"));
        return new(udn, name, peerAddress, descriptionUri,
            Find("AVTransport") ?? throw new FormatException("Missing AVTransport service."),
            Find("ConnectionManager") ?? throw new FormatException("Missing ConnectionManager service."),
            Find("RenderingControl"), Optional(renderer, "manufacturer"), Optional(renderer, "modelName"));
    }

    internal static bool HasVersion(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(value.AsSpan(prefix.Length), out int version) && version >= 1;

    internal static string Value(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    private static string? Optional(XElement element, string name) => Value(element, name) is { Length: > 0 } text ? text : null;
}

internal static class DlnaXml
{
    internal const int MaxBytes = 256 * 1024;

    internal static XDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new FormatException("UPnP XML exceeds its size limit.");
        try
        {
            using var input = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            return XDocument.Load(reader);
        }
        catch (XmlException e) { throw new FormatException("Invalid UPnP XML.", e); }
    }
}

internal static class DlnaHttp
{
    internal static void RequirePeerHttp(Uri uri, IPAddress peerAddress)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !IPAddress.TryParse(uri.Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || !address.Equals(peerAddress) || uri.Port is < 1 or > 65535)
            throw new FormatException("UPnP URLs must use HTTP on the SSDP peer's IPv4 address.");
    }

    internal static HttpClient CreateClient(IPAddress? localAddress = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxResponseHeadersLength = 32,
        };
        if (localAddress is not null)
            handler.ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > DlnaXml.MaxBytes) throw new FormatException("UPnP response exceeds its size limit.");
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > DlnaXml.MaxBytes) throw new FormatException("UPnP response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
    }
}
