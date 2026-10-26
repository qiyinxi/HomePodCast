using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        // 2026-10-18 and 2026-10-20 CI (runs 39118623472 and 39334373985): this test gave up waiting for the withdrawal
        // to resume our clock. Both times the async tests running alongside took 2.5 to 170 times as long as usual
        // and ended within 15 ms of this failure: the test process had been starved for about 2 s, while every step
        // here shared one 2 s budget, gave the master a fixed 50 ms to handle an announce, and relied on the 3 s hold
        // lasting until the withdrawal. Freezing the process for 2 s reproduces the failure, and the master still
        // handles the withdrawal within 1 ms of its arrival. So each step now waits for what the master has handled,
        // under a guard of its own, and only the withdrawal can end the better clock's hold.
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var master = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, true, 0, 0, clockId: 123) { BetterClockHold = TimeSpan.FromHours(1) };
        var peer = new IPEndPoint(IPAddress.Loopback, master.GeneralPort);

        // The master handles one socket's datagrams in order and answers a delay request only while it is master, so a
        // request sent right behind an announce shows where the election stood once that announce had been handled.
        await general.SendAsync(PtpPackets.AnnounceMessage(456, 0, domain: 7, priority: 1), peer);
        await general.SendAsync(DelayRequest(1), peer);
        Assert.Equal(1, await NextDelayResponse(general));
        Assert.True(master.IsMaster);

        await general.SendAsync(PtpPackets.AnnounceMessage(456, 0, priority: 1), peer);
        await Until(() => !master.IsMaster, "the better clock to take over");
        long sent = master.SyncMessagesSent;
        await Task.Delay(200); // more than one 125 ms Sync interval
        Assert.Equal(sent, master.SyncMessagesSent);

        // A worse clock leaves us paused and the better one's withdrawal resumes us: request 2 follows the worse announce
        // and request 3 the withdrawal. The master answers in the order it handles them, so 3 first means 2 found us paused.
        await general.SendAsync(PtpPackets.AnnounceMessage(789, 0, priority: 200), peer);
        await general.SendAsync(DelayRequest(2), peer);
        await general.SendAsync(PtpPackets.AnnounceMessage(456, 1, priority: 200), peer);
        await general.SendAsync(DelayRequest(3), peer);
        Assert.Equal(3, await NextDelayResponse(general));
        Assert.True(master.IsMaster);
        await Until(() => master.SyncMessagesSent > sent, "Sync messages to resume");
    }

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static byte[] DelayRequest(ushort sequence) => PtpPackets.Header(PtpPackets.DelayRequest, 44, 999, sequence, 0, 0);

    /// <summary>The sequence number of the next delay response, skipping the master's announces and follow-ups.</summary>
    private static async Task<ushort> NextDelayResponse(UdpClient socket)
    {
        using var guard = new CancellationTokenSource(Guard);
        try
        {
            while (true)
            {
                var packet = (await socket.ReceiveAsync(guard.Token)).Buffer;
                if (packet[0] == PtpPackets.DelayResponse) return BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(30));
            }
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException($"No delay response within {Guard.TotalSeconds:F0} s");
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        // The condition is checked before the guard, so a wait that wakes up late still sees what happened meanwhile.
        while (!condition())
        {
            if (waited.Elapsed > Guard) throw new TimeoutException($"Still waiting for {what} after {Guard.TotalSeconds:F0} s");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_late_wake_up_still_finds_an_undisputed_clock_stable()
    {
        // 2026-10-15: CI run 38733438631 stalled the whole test process for 2.9 s while this wait slept between two
        // polls. It woke past its 2 s budget and gave up, although no other clock had announced itself at all.
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var master = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, true, 0, 0, clockId: 123);
        var stalled = new HeldContext();
        var wait = stalled.Start(() => master.WaitForStableMasterAsync(CancellationToken.None));
        await Task.Delay(2200);
        stalled.RunUntil(wait);
        await wait;
    }

    [Fact]
    public async Task A_dispute_the_wait_did_not_poll_through_still_restarts_the_stable_time()
    {
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var master = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, true, 0, 0, clockId: 123);
        var peer = new IPEndPoint(IPAddress.Loopback, master.GeneralPort);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stalled = new HeldContext();
        long started = Stopwatch.GetTimestamp();
        var wait = stalled.Start(() => master.WaitForStableMasterAsync(CancellationToken.None));
        // A receiver waking up (nqptp, 2026-10-14) claims the clock and then withdraws, while the wait is not polling.
        await general.SendAsync(PtpPackets.AnnounceMessage(456, 0, priority: 1), peer);
        while (master.IsMaster) await Task.Delay(5, timeout.Token);
        await Task.Delay(300, timeout.Token);
        long withdrawn = Stopwatch.GetTimestamp();
        await general.SendAsync(PtpPackets.AnnounceMessage(456, 1, priority: 200), peer);
        while (!master.IsMaster) await Task.Delay(5, timeout.Token);
        // Should this machine stall the steps above until the wait's 2 s budget is nearly gone, a withdrawal that late
        // rightly fails the wait, and this test has nothing left to show.
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(1500)) return;
        stalled.RunUntil(wait);
        await wait;
        Assert.True(Stopwatch.GetElapsedTime(withdrawn) >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task A_better_clock_that_stays_still_fails_the_wait()
    {
        using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var master = new PtpMaster(IPAddress.Loopback, (IPEndPoint)events.Client.LocalEndPoint!,
            (IPEndPoint)general.Client.LocalEndPoint!, true, 0, 0, clockId: 123);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = new IPEndPoint(IPAddress.Loopback, master.GeneralPort);
        // A better clock that keeps announcing itself, as a real one does, holds us off for longer than the 2 s budget.
        using var gone = new CancellationTokenSource();
        var announcing = Task.Run(async () =>
        {
            for (ushort sequence = 0; !gone.IsCancellationRequested; sequence++)
            {
                await general.SendAsync(PtpPackets.AnnounceMessage(456, sequence, priority: 1), peer);
                await Task.Delay(200);
            }
        });
        while (master.IsMaster) await Task.Delay(5, timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => master.WaitForStableMasterAsync(CancellationToken.None));
        Assert.Contains("stable master clock", error.Message);
        gone.Cancel();
        await announcing;
    }

    [Fact]
    public void Apple_sessions_cannot_start_the_PTP_prototype() =>
        Assert.Throws<ArgumentException>(() => new PtpMaster(IPAddress.Loopback, new IPEndPoint(IPAddress.Loopback, 319),
            new IPEndPoint(IPAddress.Loopback, 320), thirdPartySession: false));

    /// <summary>
    /// Holds every continuation posted to it until the test runs them: a process that stops scheduling awaiting code,
    /// as the CI runner did on 2026-10-15, without having to slow a real machine down.
    /// </summary>
    private sealed class HeldContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        /// <summary>Starts the work here, so that what it awaits comes back to this context.</summary>
        public Task Start(Func<Task> work)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return work(); }
            finally { SetSynchronizationContext(previous); }
        }

        /// <summary>Runs what was held, and what that posts in turn, on this thread until the task has finished.</summary>
        public void RunUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                Assert.True(_posted.TryTake(out var next, TimeSpan.FromSeconds(5)), "the wait posted nothing to run");
                var previous = Current;
                SetSynchronizationContext(this);
                try { next.Callback(next.State); }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }
}
