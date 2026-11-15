using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;
using Xunit;

namespace HomePodCast.Tests;

/// <summary>
/// The realtime PTP sender on a timeline that is not this PC's own clock (the Apple TV experiment follows the
/// receiver's): its sync packets carry that timeline's time and clock ID, and it stops when the timeline is gone.
/// </summary>
public class PtpTimelineTests
{
    private static int Port(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;

    [Fact]
    public async Task SyncPacketsCarryTheTimelinesTimeAndClockId()
    {
        using var data = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var controlPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var control = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var timeline = new PtpTimeline(_ => 0x0102030405060708, () => 0x1122334455667788, () => true);
        using var sender = new PtpRealtimeSender(IPAddress.Loopback, IPAddress.Loopback, Port(data), Port(controlPeer), control,
            new byte[32], 7, 11025, false, new AudioFifo(44100, targetMs: 20, capMs: 60), null, timeline);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        sender.Start();
        byte[] sync = (await controlPeer.ReceiveAsync(timeout.Token)).Buffer;

        Assert.Equal(28, sync.Length);
        Assert.Equal(0xd7, sync[1]);
        Assert.Equal(0x0102030405060708ul, BinaryPrimitives.ReadUInt64BigEndian(sync.AsSpan(8)));
        Assert.Equal(0x1122334455667788ul, BinaryPrimitives.ReadUInt64BigEndian(sync.AsSpan(20)));
    }

    [Fact]
    public async Task ATimelineThatIsGoneEndsTheStream()
    {
        using var data = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var controlPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var control = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var timeline = new PtpTimeline(_ => 1, () => 2, () => false);
        using var sender = new PtpRealtimeSender(IPAddress.Loopback, IPAddress.Loopback, Port(data), Port(controlPeer), control,
            new byte[32], 7, 11025, false, new AudioFifo(44100, targetMs: 20, capMs: 60), null, timeline);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Failed += error => failed.TrySetResult(error);

        sender.Start();

        Assert.IsType<IOException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, sender.PacketsSent);
    }
}
