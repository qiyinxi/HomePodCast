using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class PtpClockPoolTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private sealed class Peer : IDisposable
    {
        public readonly UdpClient Events = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UdpClient General = new(new IPEndPoint(IPAddress.Loopback, 0));
        public PtpPorts Ports => new(0, 0, ((IPEndPoint)Events.Client.LocalEndPoint!).Port, ((IPEndPoint)General.Client.LocalEndPoint!).Port);
        public async Task<byte[]> ReceiveSync()
        {
            using var guard = new CancellationTokenSource(Guard);
            while (true)
            {
                var packet = (await Events.ReceiveAsync(guard.Token)).Buffer;
                if ((packet[0] & 15) == PtpPackets.Sync) return packet;
            }
        }
        public async Task<byte[]> ReceiveGeneral(byte type, ushort? sequence = null)
        {
            using var guard = new CancellationTokenSource(Guard);
            while (true)
            {
                var packet = (await General.ReceiveAsync(guard.Token)).Buffer;
                if ((packet[0] & 15) == type && (sequence == null || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(30)) == sequence)) return packet;
            }
        }
        public void Dispose() { Events.Dispose(); General.Dispose(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(5, guard.Token);
    }

    [Fact]
    public async Task Two_receivers_share_ports_and_clock_identity_without_owning_each_others_lifetime()
    {
        using var first = new Peer();
        using var second = new Peer();
        using var pool = new PtpClockPool();
        using var a = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, first.Ports);
        using var b = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, second.Ports);
        Assert.Same(a.Clock, b.Clock);
        Assert.Equal(2, a.Clock.PeerCount);
        byte[] syncA = await first.ReceiveSync(), syncB = await second.ReceiveSync();
        Assert.Equal(a.ClockId, BinaryPrimitives.ReadUInt64BigEndian(syncA.AsSpan(20)));
        Assert.Equal(a.ClockId, BinaryPrimitives.ReadUInt64BigEndian(syncB.AsSpan(20)));
        a.Dispose();
        Assert.Equal(1, b.Clock.PeerCount);
        await second.ReceiveSync();
        Assert.True(b.Clock.IsMaster);
        int events = b.Clock.EventPort, general = b.Clock.GeneralPort;
        b.Dispose();
        using var reboundEvents = new UdpClient(new IPEndPoint(IPAddress.Loopback, events));
        using var reboundGeneral = new UdpClient(new IPEndPoint(IPAddress.Loopback, general));
    }

    [Fact]
    public async Task Shared_peer_leases_do_not_unregister_a_receiver_until_the_last_reference()
    {
        using var peer = new Peer();
        using var pool = new PtpClockPool();
        using var a = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, peer.Ports);
        using var b = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, peer.Ports);
        Assert.Equal(1, a.Clock.PeerCount);
        a.Dispose();
        Assert.Equal(1, b.Clock.PeerCount);
        await peer.ReceiveSync();
        pool.Dispose();
        b.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, peer.Ports));
    }

    [Fact]
    public async Task Delay_responses_return_to_the_registered_peers_own_general_port()
    {
        using var first = new Peer();
        using var second = new Peer();
        using var pool = new PtpClockPool();
        using var a = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, first.Ports);
        using var b = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, second.Ports);
        byte[] request = PtpPackets.SyncMessage(0x1234, 77);
        request[0] = PtpPackets.DelayRequest;
        await second.Events.SendAsync(request, new IPEndPoint(IPAddress.Loopback, a.Clock.EventPort));
        byte[] response = await second.ReceiveGeneral(PtpPackets.DelayResponse, 77);
        Assert.Equal(a.ClockId, BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(20)));
        Assert.Equal(0x1234UL, BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(44)));
    }

    [Fact]
    public async Task One_peer_withdrawing_does_not_clear_another_peers_better_clock()
    {
        using var first = new Peer();
        using var second = new Peer();
        using var pool = new PtpClockPool();
        using var a = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, first.Ports);
        using var b = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, second.Ports);
        var receiver = new IPEndPoint(IPAddress.Loopback, a.Clock.GeneralPort);
        await first.General.SendAsync(PtpPackets.AnnounceMessage(101, 0, priority: 1), receiver);
        await second.General.SendAsync(PtpPackets.AnnounceMessage(102, 0, priority: 1), receiver);
        await Until(() => a.Clock.CompetingClockCount == 2);
        await first.General.SendAsync(PtpPackets.AnnounceMessage(101, 1, priority: 200), receiver);
        await Until(() => a.Clock.CompetingClockCount == 1);
        Assert.False(a.Clock.IsMaster);
        await second.General.SendAsync(PtpPackets.AnnounceMessage(102, 1, priority: 200), receiver);
        await Until(() => a.Clock.IsMaster);
    }

    [Fact]
    public async Task Releasing_the_peer_that_claimed_a_better_clock_removes_only_its_claim()
    {
        using var first = new Peer();
        using var second = new Peer();
        using var pool = new PtpClockPool();
        using var a = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, first.Ports);
        using var b = pool.Acquire(IPAddress.Loopback, IPAddress.Loopback, second.Ports);
        await first.General.SendAsync(PtpPackets.AnnounceMessage(101, 0, priority: 1), new IPEndPoint(IPAddress.Loopback, a.Clock.GeneralPort));
        await Until(() => !a.Clock.IsMaster);
        a.Dispose();
        Assert.True(b.Clock.IsMaster);
        await second.ReceiveSync();
    }
}
