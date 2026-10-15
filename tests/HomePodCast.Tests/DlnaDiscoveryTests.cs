using System.Net;
using System.Text;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaDiscoveryTests
{
    internal static string Description(string? services = null, string urlBase = "", bool nested = false) =>
        $"<d:root xmlns:d='urn:schemas-upnp-org:device-1-0'>{urlBase}"
        + (nested ? "<d:device><d:deviceType>urn:schemas-upnp-org:device:Root:1</d:deviceType><d:deviceList>" : "")
        + "<d:device><d:deviceType>urn:schemas-upnp-org:device:MediaRenderer:2</d:deviceType>"
        + "<d:friendlyName>Loopback &amp; renderer</d:friendlyName><d:UDN>uuid:fake-renderer</d:UDN>"
        + "<d:manufacturer>Test</d:manufacturer><d:modelName>Fake 1</d:modelName><d:serviceList>"
        + (services ?? Service("AVTransport", "transport", 2) + Service("ConnectionManager", "/connection")
            + Service("RenderingControl", "volume"))
        + "</d:serviceList></d:device>"
        + (nested ? "</d:deviceList></d:device>" : "") + "</d:root>";

    internal static string Service(string name, string control, int version = 1) =>
        $"<d:service><d:serviceType>urn:schemas-upnp-org:service:{name}:{version}</d:serviceType>"
        + $"<d:controlURL>{control}</d:controlURL></d:service>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Description_handles_namespaces_nested_renderer_and_relative_services(bool nested)
    {
        var device = Parse(Description(nested: nested));
        Assert.Equal("uuid:fake-renderer", device.Udn);
        Assert.Equal("Loopback & renderer", device.Name);
        Assert.Equal("Test", device.Manufacturer);
        Assert.Equal("Fake 1", device.Model);
        Assert.Equal(new Uri("http://127.0.0.1:12345/device/transport"), device.AvTransport.ControlUri);
        Assert.EndsWith(":2", device.AvTransport.ServiceType);
        Assert.Equal(new Uri("http://127.0.0.1:12345/connection"), device.ConnectionManager.ControlUri);
    }

    [Fact]
    public void Description_uses_same_peer_URLBase_and_allows_missing_volume()
    {
        var device = Parse(Description(Service("AVTransport", "transport") + Service("ConnectionManager", "connection"),
            "<d:URLBase>http://127.0.0.1:23456/base/</d:URLBase>"));
        Assert.Equal(new Uri("http://127.0.0.1:23456/base/transport"), device.AvTransport.ControlUri);
        Assert.Null(device.RenderingControl);
    }

    [Theory]
    [InlineData("http://192.0.2.1/control")]
    [InlineData("http://localhost/control")]
    [InlineData("https://127.0.0.1/control")]
    [InlineData("http://user:pass@127.0.0.1/control")]
    [InlineData("http://127.0.0.1/control#fragment")]
    [InlineData("//192.0.2.1/control")]
    public void Description_rejects_unsafe_control_urls(string control) =>
        Assert.Throws<FormatException>(() => Parse(Description(Service("AVTransport", control) + Service("ConnectionManager", "/cm"))));

    [Fact]
    public void Description_rejects_foreign_base_missing_identity_and_required_services()
    {
        Assert.Throws<FormatException>(() => Parse(Description(urlBase: "<d:URLBase>http://192.0.2.1/</d:URLBase>")));
        Assert.Throws<FormatException>(() => Parse(Description().Replace("uuid:fake-renderer", "")));
        Assert.Throws<FormatException>(() => Parse(Description(Service("AVTransport", "/av"))));
    }

    [Fact]
    public void Description_rejects_DTD_malformed_and_oversized_XML()
    {
        Assert.Throws<FormatException>(() => Parse("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///test'>]><root>&x;</root>"));
        Assert.Throws<FormatException>(() => Parse("<root>"));
        Assert.Throws<FormatException>(() => Parse(new string(' ', DlnaXml.MaxBytes + 1)));
    }

    private static DlnaDevice Parse(string xml) => DlnaDevice.ParseDescription(Encoding.UTF8.GetBytes(xml),
        IPAddress.Loopback, new Uri("http://127.0.0.1:12345/device/description.xml"));
}
