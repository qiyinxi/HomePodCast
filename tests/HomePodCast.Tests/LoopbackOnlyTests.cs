using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// The tests listen on loopback only. Listening on any other address makes Windows ask for a firewall rule, and a rule
/// belongs to one program path: each new worktree's test build asked again. 2026-10-16: 30 rules on the maintainer's PC,
/// one per worktree, and a prompt left open at night that held up the maintainer's other automation.
/// </summary>
public sealed class LoopbackOnlyTests
{
    [Fact]
    public async Task Discovery_in_the_tests_listens_on_loopback_only()
    {
        // A private group and port, as ReceiverDiscoveryTests use: nothing on the LAN is asked.
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var group = new IPEndPoint(IPAddress.Parse("239.255.77.78"), port);
        // The browse lasts until the sampling is over, not a fixed time. 2026-10-16 (CI run 38970600060): an 800 ms browse,
        // sampled once after a 250 ms delay, found no socket at all; a stalled runner timed out two Cinema tests at the same
        // time. When the delay's continuation comes that late, the browse has closed its sockets: blocking the thread
        // pool's threads beside this test reproduced the empty sample.
        using var stop = new CancellationTokenSource();
        var browse = Mdns.BrowseNetworkAsync(TimeSpan.FromMinutes(1), [IPAddress.Loopback], group, stop.Token);
        var open = new HashSet<IPEndPoint>();
        bool seen = false;
        try
        {
            // Every listener of this process, including those of tests running alongside: sampled for 800 ms. The browse's
            // listener is bound before BrowseNetworkAsync returns, so the first sample has it; it may take longer all the same.
            var watched = Stopwatch.StartNew();
            while (watched.Elapsed < TimeSpan.FromMilliseconds(800) || (!seen && watched.Elapsed < TimeSpan.FromSeconds(30)))
            {
                var sample = Listening();
                seen |= sample.Any(e => e.Port == port);
                open.UnionWith(sample);
                await Task.Delay(20);
            }
        }
        finally
        {
            stop.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browse);
        Assert.True(seen, $"the browse's listener on port {port} never showed up; this process had [{string.Join(", ", open)}]");
        // A UDP socket that only Connect bound shows as 0.0.0.0 too, and the table can't tell it from a listener: the tests'
        // code mustn't open one (RouteLookupTests).
        Assert.All(open, e => Assert.True(IPAddress.IsLoopback(e.Address), $"this test process listens on {e}"));
    }

    /// <summary>This process's UDP sockets and listening TCP sockets, IPv4 and IPv6.</summary>
    internal static List<IPEndPoint> Listening()
    {
        uint pid = (uint)Environment.ProcessId;
        var found = new List<IPEndPoint>();
        // MIB_UDPROW_OWNER_PID: addr, port, pid. MIB_UDP6ROW_OWNER_PID: addr[16], scope, port, pid.
        Read(udp: true, AF_INET, 12, row => (new IPAddress((uint)Marshal.ReadInt32(row, 0)), Marshal.ReadInt32(row, 4), Marshal.ReadInt32(row, 8)));
        Read(udp: true, AF_INET6, 28, row => (new IPAddress(Bytes(row, 0, 16)), Marshal.ReadInt32(row, 20), Marshal.ReadInt32(row, 24)));
        // MIB_TCPROW_OWNER_PID: state, addr, port, remote addr, remote port, pid.
        // MIB_TCP6ROW_OWNER_PID: addr[16], scope, port, remote addr[16], remote scope, remote port, state, pid.
        Read(udp: false, AF_INET, 24, row => (new IPAddress((uint)Marshal.ReadInt32(row, 4)), Marshal.ReadInt32(row, 8), Marshal.ReadInt32(row, 20)));
        Read(udp: false, AF_INET6, 56, row => (new IPAddress(Bytes(row, 0, 16)), Marshal.ReadInt32(row, 20), Marshal.ReadInt32(row, 52)));
        return found;

        void Read(bool udp, int family, int rowSize, Func<IntPtr, (IPAddress Address, int Port, int Pid)> parse)
        {
            int size = 0;
            uint status = Call(IntPtr.Zero, ref size);
            // The whole machine's table: with the tests opening sockets alongside, it may grow between the two calls.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var table = Marshal.AllocHGlobal(size);
                try
                {
                    status = Call(table, ref size);
                    if (status == ErrorInsufficientBuffer) continue; // size is the new one
                    if (status != 0) break;
                    int count = Marshal.ReadInt32(table);
                    for (int i = 0; i < count; i++)
                    {
                        var (address, rawPort, owner) = parse(table + 4 + i * rowSize);
                        int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF); // network byte order
                        if ((uint)owner == pid) found.Add(new IPEndPoint(address, port));
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(table);
                }
            }
            throw new InvalidOperationException($"the {(udp ? "UDP" : "TCP")} table can't be read ({status})");

            uint Call(IntPtr buffer, ref int length) => udp
                ? GetExtendedUdpTable(buffer, ref length, false, family, 1, 0)  // UDP_TABLE_OWNER_PID
                : GetExtendedTcpTable(buffer, ref length, false, family, 3, 0); // TCP_TABLE_OWNER_PID_LISTENER
        }
    }

    private static byte[] Bytes(IntPtr row, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(row + offset, bytes, 0, length);
        return bytes;
    }

    private const int AF_INET = 2, AF_INET6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
