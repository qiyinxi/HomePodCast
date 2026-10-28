using System.Net.Sockets;

namespace HomePodCast.Net;

internal static class UdpSockets
{
    /// <summary>SIO_UDP_CONNRESET (_WSAIOW(IOC_VENDOR, 12)): whether an ICMP port unreachable is reported.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Stops Windows from reporting an ICMP port unreachable for an earlier send as WSAECONNRESET on the next receive.
    /// It does so on unconnected sockets too, by default, and a loop that ends on any receive error then ends because one
    /// peer's port was closed for a moment (a receiver's PTP daemon restarting, or shut down before our session was).
    /// Returns <paramref name="udp"/>.
    /// </summary>
    public static UdpClient IgnorePortUnreachable(this UdpClient udp)
    {
        if (OperatingSystem.IsWindows()) udp.Client.IOControl(SioUdpConnReset, new byte[4], null);
        return udp;
    }
}
