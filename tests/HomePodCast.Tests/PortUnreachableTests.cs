using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// UDP sockets that end a session on a receive error: Windows reports an ICMP port unreachable for an earlier send as
/// WSAECONNRESET on the next receive, also on unconnected sockets, so a peer whose port was closed must not reach them.
/// </summary>
public class PortUnreachableTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>A loopback port nothing listens on: bound once, then closed.</summary>
    private static IPEndPoint ClosedPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return (IPEndPoint)udp.Client.LocalEndPoint!;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > Guard) throw new TimeoutException($"Still waiting for {what} after {Guard.TotalSeconds:F0} s");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_peer_whose_PTP_ports_are_closed_does_not_stop_the_clock()
    {
        // A receiver's nqptp not listening (missing, restarting, or shut down before our session): every Sync to it comes
        // back as a port unreachable, and the first one ended the master, and through PtpClockPool every PTP receiver.
        var failures = new ConcurrentQueue<Exception>();
        using var master = new PtpMaster(IPAddress.Loopback, ClosedPort(), ClosedPort(), true, 0, 0, clockId: 123);
        master.Failed += failures.Enqueue;
        await Until(() => master.SyncMessagesSent >= 8 || !failures.IsEmpty, "eight Sync rounds");
        Assert.Empty(failures);

        // And it still answers a peer that does listen.
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        master.AddPeer((IPEndPoint)events.Client.LocalEndPoint!, (IPEndPoint)general.Client.LocalEndPoint!);
        using var deadline = new CancellationTokenSource(Guard);
        Assert.Equal(PtpPackets.Sync, (await events.ReceiveAsync(deadline.Token)).Buffer[0]);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task The_sessions_control_socket_does_not_fail_on_a_closed_port()
    {
        // PtpRealtimeSender reads resend requests from it and ends the session on any receive error.
        using var fake = new FakeAirPlayReceiver(false);
        using var guard = new CancellationTokenSource(Guard);
        using var client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port, new StreamOptions(250, 0),
            ChannelMode.Stereo, guard.Token);
        var control = client.StreamTarget!.Control;
        control.Send(new byte[28], 28, ClosedPort());
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        // Nothing arrives: the receive waits until it is given up, instead of failing with the port unreachable.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.ReceiveAsync(quiet.Token).AsTask());
    }
}
