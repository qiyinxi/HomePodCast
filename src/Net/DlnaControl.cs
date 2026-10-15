using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HomePodCast.Net;

/// <summary>A renderer's SOAP failure, kept separate from transport and malformed-response failures.</summary>
internal sealed class DlnaActionException(string action, string serviceType, int? errorCode, string description,
    HttpStatusCode? statusCode = null) : Exception($"{action}: {description}")
{
    internal string Action { get; } = action;
    internal string ServiceType { get; } = serviceType;
    internal int? ErrorCode { get; } = errorCode;
    internal HttpStatusCode? StatusCode { get; } = statusCode;
    internal bool IsUnsupportedAction => ErrorCode is 401 or 602;
}

/// <summary>UPnP control only; the caller decides whether it owns the renderer's current media.</summary>
internal sealed class DlnaControl : IDisposable
{
    private const string SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly DlnaDevice _device;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal DlnaControl(DlnaDevice device, HttpClient? client = null)
    {
        _device = device;
        DlnaHttp.RequirePeerHttp(device.DescriptionUri, device.Address);
        DlnaHttp.RequirePeerHttp(device.AvTransport.ControlUri, device.Address);
        DlnaHttp.RequirePeerHttp(device.ConnectionManager.ControlUri, device.Address);
        if (device.RenderingControl is { } rendering) DlnaHttp.RequirePeerHttp(rendering.ControlUri, device.Address);
        _client = client ?? DlnaHttp.CreateClient();
        _ownsClient = client is null;
    }

    internal async Task<IReadOnlyList<string>> GetProtocolInfoAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(_device.ConnectionManager, "GetProtocolInfo", [], ct).ConfigureAwait(false);
        return Field(response, "Sink").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    internal async Task<string> GetTransportInfoAsync(CancellationToken ct = default) =>
        Field(await InvokeAsync(_device.AvTransport, "GetTransportInfo", Instance(), ct).ConfigureAwait(false), "CurrentTransportState");

    internal async Task<string> GetMediaInfoAsync(CancellationToken ct = default) =>
        Field(await InvokeAsync(_device.AvTransport, "GetMediaInfo", Instance(), ct).ConfigureAwait(false), "CurrentURI");

    internal Task SetAvTransportUriAsync(Uri uri, string didl, CancellationToken ct = default)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !IPAddress.TryParse(uri.Host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Media URI must use HTTP with a literal IPv4 address.", nameof(uri));
        return InvokeAsync(_device.AvTransport, "SetAVTransportURI",
            [("InstanceID", "0"), ("CurrentURI", uri.AbsoluteUri), ("CurrentURIMetaData", didl)], ct);
    }

    internal Task PlayAsync(CancellationToken ct = default) =>
        InvokeAsync(_device.AvTransport, "Play", [("InstanceID", "0"), ("Speed", "1")], ct);

    internal Task StopAsync(CancellationToken ct = default) => InvokeAsync(_device.AvTransport, "Stop", Instance(), ct);

    internal async Task<int> GetVolumeAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(Rendering("GetVolume"), "GetVolume", Master(), ct).ConfigureAwait(false);
        if (!int.TryParse(Field(response, "CurrentVolume"), NumberStyles.None, CultureInfo.InvariantCulture, out int volume)
            || volume is < 0 or > 100) throw new FormatException("Invalid renderer volume.");
        return volume;
    }

    internal Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        return InvokeAsync(Rendering("SetVolume"), "SetVolume",
            [("InstanceID", "0"), ("Channel", "Master"), ("DesiredVolume", volume.ToString(CultureInfo.InvariantCulture))], ct);
    }

    internal async Task<bool> GetMuteAsync(CancellationToken ct = default)
    {
        string value = Field(await InvokeAsync(Rendering("GetMute"), "GetMute", Master(), ct).ConfigureAwait(false), "CurrentMute");
        return value switch
        {
            "0" or "false" => false,
            "1" or "true" => true,
            _ => throw new FormatException("Invalid renderer mute state."),
        };
    }

    internal Task SetMuteAsync(bool muted, CancellationToken ct = default) =>
        InvokeAsync(Rendering("SetMute"), "SetMute",
            [("InstanceID", "0"), ("Channel", "Master"), ("DesiredMute", muted ? "1" : "0")], ct);

    private DlnaService Rendering(string action) => _device.RenderingControl
        ?? throw new DlnaActionException(action, "urn:schemas-upnp-org:service:RenderingControl:1", 401,
            "Renderer has no RenderingControl service.");

    private static (string, string)[] Instance() => [("InstanceID", "0")];
    private static (string, string)[] Master() => [("InstanceID", "0"), ("Channel", "Master")];

    private async Task<XElement> InvokeAsync(DlnaService service, string action, (string Name, string Value)[] arguments,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] body;
        using (var output = new MemoryStream())
        {
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true,
            }))
            {
                writer.WriteStartElement("s", "Envelope", SoapNamespace);
                writer.WriteAttributeString("s", "encodingStyle", SoapNamespace, "http://schemas.xmlsoap.org/soap/encoding/");
                writer.WriteStartElement("s", "Body", SoapNamespace);
                writer.WriteStartElement("u", action, service.ServiceType);
                foreach (var (name, value) in arguments) writer.WriteElementString(name, value);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            if (output.Length > DlnaXml.MaxBytes) throw new ArgumentException("UPnP request exceeds its size limit.");
            body = output.ToArray();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUri);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#{action}\"");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("text/xml") { CharSet = "utf-8" };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        byte[] bytes = await DlnaHttp.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        if (bytes.Length == 0 && !response.IsSuccessStatusCode)
            throw new DlnaActionException(action, service.ServiceType, null, $"HTTP {(int)response.StatusCode}.", response.StatusCode);
        var document = DlnaXml.Parse(bytes);
        var soapBody = document.Root?.Name == XName.Get("Envelope", SoapNamespace)
            ? document.Root.Element(XName.Get("Body", SoapNamespace)) : null;
        if (soapBody is null) throw new FormatException("Missing SOAP response body.");
        if (soapBody.Element(XName.Get("Fault", SoapNamespace)) is { } fault)
        {
            var error = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            int? code = int.TryParse(error is null ? "" : DlnaDevice.Value(error, "errorCode"),
                NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
            string description = error is null ? DlnaDevice.Value(fault, "faultstring") : DlnaDevice.Value(error, "errorDescription");
            throw new DlnaActionException(action, service.ServiceType, code, description, response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
            throw new DlnaActionException(action, service.ServiceType, null, $"HTTP {(int)response.StatusCode}.", response.StatusCode);
        return soapBody.Element(XName.Get(action + "Response", service.ServiceType))
            ?? throw new FormatException("Missing or mismatched SOAP action response.");
    }

    private static string Field(XElement response, string name) =>
        response.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value
        ?? throw new FormatException($"Missing SOAP field {name}.");

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
