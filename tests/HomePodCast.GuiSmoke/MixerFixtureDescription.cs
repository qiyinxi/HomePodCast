namespace HomePodCast.Tests;

// Only the description helpers used by the linked fake renderer; no test framework is loaded by the GUI host.
internal static class DlnaDiscoveryTests
{
    internal static string Description(string services) =>
        "<d:root xmlns:d='urn:schemas-upnp-org:device-1-0'><d:device><d:deviceType>urn:schemas-upnp-org:device:MediaRenderer:2</d:deviceType>" +
        "<d:friendlyName>Loopback &amp; renderer</d:friendlyName><d:UDN>uuid:fake-renderer</d:UDN><d:manufacturer>Test</d:manufacturer>" +
        "<d:modelName>Fake 1</d:modelName><d:serviceList>" + services + "</d:serviceList></d:device></d:root>";
    internal static string Service(string name, string control, int version = 1) =>
        $"<d:service><d:serviceType>urn:schemas-upnp-org:service:{name}:{version}</d:serviceType><d:controlURL>{control}</d:controlURL></d:service>";
}
