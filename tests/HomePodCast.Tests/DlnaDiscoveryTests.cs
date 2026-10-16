using System.Net;
using System.Net.Sockets;
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
    public async Task Multicast_notification_listener_stays_on_the_explicit_loopback_interface()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var endpoint = new IPEndPoint(IPAddress.Parse("239.255.77.79"), port);
        var browsing = DlnaDiscovery.BrowseAsync(TimeSpan.FromMilliseconds(400), localAddress: IPAddress.Loopback, endpoint: endpoint);
        await Task.Delay(30);
        var listeners = LoopbackOnlyTests.Listening().Where(e => e.Port == port).ToArray();
        await browsing;
        Assert.NotEmpty(listeners);
        Assert.All(listeners, e => Assert.True(IPAddress.IsLoopback(e.Address)));
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

    [Fact]
    public async Task Browse_fetches_descriptions_on_explicit_loopback_and_deduplicates_UDN()
    {
        await using var renderer = new FakeDlnaRenderer();
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var endpoint = (IPEndPoint)responder.Client.LocalEndPoint!;
        var reply = Task.Run(async () =>
        {
            var search = await responder.ReceiveAsync(guard.Token);
            string request = Encoding.ASCII.GetString(search.Buffer);
            Assert.Contains("M-SEARCH * HTTP/1.1", request);
            Assert.Contains("ST: urn:schemas-upnp-org:device:MediaRenderer:1", request);
            Assert.Contains("MAN: \"ssdp:discover\"", request);
            byte[] advertisement = Encoding.ASCII.GetBytes(Advertisement(renderer.DescriptionUri));
            await responder.SendAsync(advertisement, search.RemoteEndPoint, guard.Token);
            await responder.SendAsync(advertisement, search.RemoteEndPoint, guard.Token);
        });
        var devices = await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(1), guard.Token, IPAddress.Loopback, endpoint);
        await reply;
        Assert.Equal("uuid:fake-renderer", Assert.Single(devices).Udn);
        Assert.All(renderer.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task Browse_rejects_cross_peer_locations_and_invalid_descriptions()
    {
        await using var renderer = new FakeDlnaRenderer();
        renderer.DescriptionOverride = "<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///test'>]><root>&x;</root>";
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reply = Task.Run(async () =>
        {
            var search = await responder.ReceiveAsync(guard.Token);
            await responder.SendAsync(Encoding.ASCII.GetBytes(Advertisement(new Uri("http://192.0.2.1/description.xml"))),
                search.RemoteEndPoint, guard.Token);
            await responder.SendAsync(Encoding.ASCII.GetBytes(Advertisement(renderer.DescriptionUri)), search.RemoteEndPoint, guard.Token);
        });
        Assert.Empty(await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(1), guard.Token, IPAddress.Loopback,
            (IPEndPoint)responder.Client.LocalEndPoint!));
        await reply;
        Assert.Single(renderer.Requests);
    }

    [Fact]
    public async Task Browse_cancellation_interrupts_receiving_without_a_real_interface()
    {
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancel = new CancellationTokenSource();
        var pending = DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(30), cancel.Token, IPAddress.Loopback,
            (IPEndPoint)responder.Client.LocalEndPoint!);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await responder.ReceiveAsync(guard.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Withdrawal_during_description_fetch_does_not_resurrect_renderer(bool byeBye)
    {
        await using var renderer = new FakeDlnaRenderer();
        renderer.Delays["Description"] = TimeSpan.FromMilliseconds(100);
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reply = Task.Run(async () =>
        {
            var search = await responder.ReceiveAsync(guard.Token);
            await responder.SendAsync(Encoding.ASCII.GetBytes(Advertisement(renderer.DescriptionUri)), search.RemoteEndPoint, guard.Token);
            while (renderer.Requests.IsEmpty) await Task.Delay(5, guard.Token);
            string withdrawal = byeBye ? ByeBye() : Advertisement(renderer.DescriptionUri).Replace("max-age=1800", "max-age=0");
            await responder.SendAsync(Encoding.ASCII.GetBytes(withdrawal), search.RemoteEndPoint, guard.Token);
        });
        Assert.Empty(await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(1), guard.Token, IPAddress.Loopback,
            (IPEndPoint)responder.Client.LocalEndPoint!));
        await reply;
    }

    [Fact]
    public async Task New_alive_after_byebye_can_complete_while_old_description_is_pending()
    {
        await using var renderer = new FakeDlnaRenderer();
        renderer.Delays["Description"] = TimeSpan.FromMilliseconds(100);
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reply = Task.Run(async () =>
        {
            var search = await responder.ReceiveAsync(guard.Token);
            byte[] alive = Encoding.ASCII.GetBytes(Advertisement(renderer.DescriptionUri));
            await responder.SendAsync(alive, search.RemoteEndPoint, guard.Token);
            while (renderer.Requests.IsEmpty) await Task.Delay(5, guard.Token);
            await responder.SendAsync(Encoding.ASCII.GetBytes(ByeBye()), search.RemoteEndPoint, guard.Token);
            await responder.SendAsync(alive, search.RemoteEndPoint, guard.Token);
        });
        Assert.Single(await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(1), guard.Token, IPAddress.Loopback,
            (IPEndPoint)responder.Client.LocalEndPoint!));
        await reply;
    }

    [Theory]
    [InlineData("LOCATION: http://192.0.2.1/description.xml")]
    [InlineData("LOCATION: http://user:pass@127.0.0.1/description.xml")]
    [InlineData("CACHE-CONTROL: max-age=-1")]
    [InlineData("CACHE-CONTROL: max-age=bad")]
    [InlineData("ST: urn:schemas-upnp-org:device:MediaServer:1")]
    [InlineData("USN: fake-renderer")]
    public void Advertisement_rejects_unsafe_or_malformed_fields(string replacement)
    {
        string name = replacement[..replacement.IndexOf(':')];
        var lines = Advertisement(new Uri("http://127.0.0.1/description.xml")).Split("\r\n");
        string text = string.Join("\r\n", lines.Select(l => l.StartsWith(name + ":") ? replacement : l));
        Assert.False(DlnaDiscovery.TryParseAdvertisement(Encoding.ASCII.GetBytes(text), IPAddress.Loopback, out _));
    }

    [Fact]
    public void Advertisement_and_cache_handle_duplicates_TTL_and_byebye()
    {
        var device = Parse(Description());
        var cache = new DlnaDiscovery.Cache();
        var now = DateTimeOffset.UtcNow;
        long revision = cache.ObserveAlive(device.Udn, device.Address);
        cache.Add(device, now.AddSeconds(10), revision);
        cache.Add(device with { Name = "Updated name" }, now.AddSeconds(20), revision);
        Assert.Equal("Updated name", Assert.Single(cache.Devices(now)).Name);
        Assert.Empty(cache.Devices(now.AddSeconds(21)));
        string bye = ByeBye();
        Assert.True(DlnaDiscovery.TryParseAdvertisement(Encoding.ASCII.GetBytes(bye), IPAddress.Loopback, out var advertisement));
        Assert.True(advertisement.ByeBye);
        cache.Remove(advertisement.Udn, advertisement.Address);
        Assert.Empty(cache.Devices(now));
        cache.Add(device, now.AddSeconds(30), revision);
        Assert.Empty(cache.Devices(now));
        revision = cache.ObserveAlive(device.Udn, device.Address);
        cache.Add(device, now.AddSeconds(30), revision);
        Assert.Single(cache.Devices(now));
    }

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:+1")]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:1\r\n")]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:0")]
    public void Service_version_is_digits_only(string type) =>
        Assert.False(DlnaDevice.HasVersion(type, "urn:schemas-upnp-org:service:AVTransport:"));

    [Fact]
    public void Advertisement_rejects_duplicate_headers_and_oversized_payloads()
    {
        string text = Advertisement(new Uri("http://127.0.0.1/description.xml"))
            .Replace("\r\n\r\n", "\r\nLOCATION: http://127.0.0.1/other\r\n\r\n");
        Assert.False(DlnaDiscovery.TryParseAdvertisement(Encoding.ASCII.GetBytes(text), IPAddress.Loopback, out _));
        Assert.False(DlnaDiscovery.TryParseAdvertisement(new byte[16 * 1024 + 1], IPAddress.Loopback, out _));
    }

    internal static string Advertisement(Uri location) => "HTTP/1.1 200 OK\r\n"
        + "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\nUSN: uuid:fake-renderer::urn:schemas-upnp-org:device:MediaRenderer:1\r\n"
        + $"LOCATION: {location}\r\nCACHE-CONTROL: max-age=1800\r\n\r\n";

    private static string ByeBye() => "NOTIFY * HTTP/1.1\r\nNT: urn:schemas-upnp-org:device:MediaRenderer:1\r\n"
        + "NTS: ssdp:byebye\r\nUSN: uuid:fake-renderer::urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";

    private static DlnaDevice Parse(string xml) => DlnaDevice.ParseDescription(Encoding.UTF8.GetBytes(xml),
        IPAddress.Loopback, new Uri("http://127.0.0.1:12345/device/description.xml"));
}
