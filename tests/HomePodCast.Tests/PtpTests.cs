using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class PtpTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(1_000_000_000UL)]
    [InlineData(1_799_999_999_123_456_789UL)]
    public void Timestamp_has_48_bit_seconds_and_nanosecond_fraction(ulong time)
    {
        var bytes = new byte[10];
        PtpPackets.WriteTimestamp(bytes, time);
        Assert.Equal(time, PtpPackets.ReadTimestamp(bytes));
        Assert.Equal(time % 1_000_000_000, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)));
    }

    [Fact]
    public void Sync_and_followup_share_identity_sequence_and_domain()
    {
        ulong clock = 0x021122fffe334455;
        var sync = PtpPackets.SyncMessage(clock, 0xffff, 7);
        var follow = PtpPackets.FollowUpMessage(clock, 0xffff, 1234567890123, 7);
        Assert.Equal(44, sync.Length);
        Assert.Equal(6, sync[6]);
        Assert.Equal(0, follow[6] & 2);
        Assert.Equal(sync[20..32], follow[20..32]);
        Assert.Equal(clock, BinaryPrimitives.ReadUInt64BigEndian(sync.AsSpan(20)));
        Assert.Equal(1234567890123UL, PtpPackets.ReadTimestamp(follow.AsSpan(34)));
        Assert.True(PtpPackets.Valid(sync, 7));
        Assert.False(PtpPackets.Valid(sync, 0));
    }

    [Fact]
    public void Delay_response_echoes_requester_sequence_and_correction()
    {
        var request = PtpPackets.Header(PtpPackets.DelayRequest, 44, 123, 42, 0, 0);
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(8), 0x987654);
        var response = PtpPackets.DelayResponseMessage(456, request, 987654321);
        Assert.Equal(54, response.Length);
        Assert.Equal(request[20..30], response[44..54]);
        Assert.Equal(request[30..32], response[30..32]);
        Assert.Equal(request[8..16], response[8..16]);
        Assert.Equal(987654321UL, PtpPackets.ReadTimestamp(response.AsSpan(34)));
    }

    [Fact]
    public void BMCA_prefers_priority_then_identity()
    {
        var own = PtpPackets.AnnounceMessage(100, 0);
        Assert.True(PtpPackets.CompareGrandmasters(PtpPackets.AnnounceMessage(200, 0, priority: 1), own) < 0);
        Assert.True(PtpPackets.CompareGrandmasters(PtpPackets.AnnounceMessage(50, 0), own) < 0);
        Assert.True(PtpPackets.CompareGrandmasters(PtpPackets.AnnounceMessage(200, 0), own) > 0);
        Assert.Equal(0, PtpPackets.CompareGrandmasters(own, own));
    }

    [Fact]
    public void Truncated_packets_and_invalid_versions_are_rejected()
    {
        var packet = PtpPackets.SyncMessage(123, 0);
        for (int length = 0; length < packet.Length; length++) Assert.False(PtpPackets.Valid(packet.AsSpan(0, length), 0));
        packet[1] = 1;
        Assert.False(PtpPackets.Valid(packet, 0));
    }

    [Fact]
    public async Task Loopback_receiver_observes_two_step_sync_and_gets_delay_reply()
    {
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var master = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, thirdPartySession: true, eventPort: 0, generalPort: 0, clockId: 123);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sync = (await events.ReceiveAsync(deadline.Token)).Buffer;
        Assert.Equal(PtpPackets.Sync, sync[0]);
        byte[] follow;
        do { follow = (await general.ReceiveAsync(deadline.Token)).Buffer; } while (follow[0] != PtpPackets.FollowUp);
        Assert.Equal(sync[30..32], follow[30..32]);
        var request = PtpPackets.Header(PtpPackets.DelayRequest, 44, 456, 78, 0, 0);
        await events.SendAsync(request, new IPEndPoint(IPAddress.Loopback, master.EventPort), deadline.Token);
        byte[] response;
        do { response = (await general.ReceiveAsync(deadline.Token)).Buffer; } while (response[0] != PtpPackets.DelayResponse);
        Assert.Equal(request[20..30], response[44..54]);
        Assert.Equal((ushort)78, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(30)));
        Assert.True(PtpPackets.ReadTimestamp(response.AsSpan(34)) >= PtpPackets.ReadTimestamp(follow.AsSpan(34)));
    }

    [Fact]
    public async Task BetterGrandmasterPausesOurClockButAnUnrelatedDomainDoesNot()
    {
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var master = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, true, 0, 0, clockId: 123);
        await general.SendAsync(PtpPackets.AnnounceMessage(456, 0, domain: 7, priority: 1),
            new IPEndPoint(IPAddress.Loopback, master.GeneralPort));
        await Task.Delay(50);
        Assert.True(master.IsMaster);
        await general.SendAsync(PtpPackets.AnnounceMessage(456, 0, priority: 1),
            new IPEndPoint(IPAddress.Loopback, master.GeneralPort));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (master.IsMaster) await Task.Delay(10, timeout.Token);
        long sent = master.SyncMessagesSent;
        await Task.Delay(200);
        Assert.Equal(sent, master.SyncMessagesSent);
    }

    [Fact]
    public void Apple_sessions_cannot_start_the_PTP_prototype() =>
        Assert.Throws<ArgumentException>(() => new PtpMaster(IPAddress.Loopback, new IPEndPoint(IPAddress.Loopback, 319),
            new IPEndPoint(IPAddress.Loopback, 320), thirdPartySession: false));
}
