using System.Collections.Concurrent;
using System.Diagnostics;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>A group member without RTSP: records what the group asks of it; its RTP goes to a FakeReceiver.</summary>
internal sealed class FakeMember(string name, FakeReceiver rx, ConcurrentQueue<string> log, ChannelMode channels = ChannelMode.Stereo)
    : IGroupMember
{
    public string Name { get; } = name;
    public FakeReceiver Receiver { get; } = rx;
    public string? FailOn { get; init; }
    public RtpTarget Target { get; } = rx.Target(channels);
    public int LatencyFrames => 4630;
    public int? ArrivalToRenderMs { get; init; } = 90;
    public double? InitialVolumeDb { get; init; } = -15;
    public string? LostReason { get; set; }
    public event Action<string>? Lost;
    public List<double> Volumes { get; } = [];
    public (ushort Seq, uint Rtp)? Flushed { get; private set; }
    public bool Disposed { get; private set; }

    public void Record()
    {
        log.Enqueue($"record {Name}");
        if (FailOn == "record") throw new AirPlayException("RECORD failed: RTSP/1.0 500 Internal Server Error");
    }

    public void Flush(ushort firstSeq, uint rtpBase)
    {
        log.Enqueue($"flush {Name}");
        Flushed = (firstSeq, rtpBase);
    }

    public void SetVolumePercent(double percent)
    {
        lock (Volumes) Volumes.Add(percent);
    }

    public void Lose(string reason)
    {
        LostReason = reason;
        Lost?.Invoke(reason);
    }

    public void Dispose()
    {
        Disposed = true;
        log.Enqueue($"dispose {Name}");
    }

    public MemberSetup Setup(int volumeOffset = 0, int prepareDelayMs = 0) => new(Name, async ct =>
    {
        log.Enqueue($"prepare {Name}");
        if (prepareDelayMs > 0) await Task.Delay(prepareDelayMs, ct);
        if (FailOn == "prepare") throw new AirPlayException("找不到音箱");
        return this;
    }, volumeOffset);
}

public class SpeakerGroupTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeReceiver> _receivers = [];

    private FakeMember Member(string name, ChannelMode channels = ChannelMode.Stereo, string? failOn = null)
    {
        var rx = new FakeReceiver();
        _receivers.Add(rx);
        return new FakeMember(name, rx, _log, channels) { FailOn = failOn };
    }

    private static AudioFifo Fifo() => new(44100, targetMs: 20, capMs: 60);

    public void Dispose()
    {
        foreach (var rx in _receivers) rx.Dispose();
    }

    [Fact]
    public async Task Records_on_every_speaker_before_one_shared_timeline_starts()
    {
        var left = Member("L", ChannelMode.LeftOnly);
        var right = Member("R", ChannelMode.RightOnly);
        using (var group = await SpeakerGroup.ConnectAsync([left.Setup(), right.Setup(volumeOffset: -10)], Fifo(), null, default))
        {
            var steps = _log.ToList();
            int lastRecord = Math.Max(steps.IndexOf("record L"), steps.IndexOf("record R"));
            int firstFlush = Math.Min(steps.IndexOf("flush L"), steps.IndexOf("flush R"));
            Assert.True(lastRecord >= 0 && firstFlush > lastRecord, string.Join(", ", steps));

            var sender = group.Sender!;
            Assert.Equal(2, sender.Streams.Count);
            Assert.Equal((sender.Streams[0].FirstSeq, sender.RtpBase), left.Flushed);
            Assert.Equal((sender.Streams[1].FirstSeq, sender.RtpBase), right.Flushed);
            Assert.Equal(ChannelMode.LeftOnly, sender.Streams[0].Channels);
            Assert.Equal(ChannelMode.RightOnly, sender.Streams[1].Channels);

            // Linked volume from the first speaker's own volume (-15 dB = 50 %), plus each one's offset.
            Assert.Equal(50, group.MasterVolume, 3);
            Assert.Equal([50.0], left.Volumes);
            Assert.Equal([40.0], right.Volumes);
            group.SetVolumePercent(5);
            Assert.Equal(5, left.Volumes[^1]);
            Assert.Equal(0, right.Volumes[^1]);

            Thread.Sleep(300); // packet 0 is due 250 ms after the start
            var a = left.Receiver.Audio.ToArray();
            var b = right.Receiver.Audio.ToArray();
            Assert.NotEmpty(a);
            Assert.NotEmpty(b);
            // Each speaker's first packet is exactly what its FLUSH announced.
            Assert.Equal(left.Flushed!.Value.Seq, FakeReceiver.Seq(a[0].Bytes));
            Assert.Equal(right.Flushed!.Value.Seq, FakeReceiver.Seq(b[0].Bytes));
            Assert.Equal(sender.RtpBase, FakeReceiver.Rtp(a[0].Bytes));
            Assert.Equal(sender.RtpBase, FakeReceiver.Rtp(b[0].Bytes));
        }
        Assert.True(left.Disposed && right.Disposed);
    }

    [Fact]
    public async Task A_speaker_that_fails_to_set_up_takes_the_whole_group_down()
    {
        var ok = Member("A");
        var bad = Member("B", failOn: "prepare");
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<GroupMemberException>(() =>
            SpeakerGroup.ConnectAsync([ok.Setup(prepareDelayMs: 10_000), bad.Setup()], Fifo(), null, default));

        Assert.True(sw.ElapsedMilliseconds < 3000, "the slow member was not cancelled");
        Assert.Equal("B", ex.Member);          // the real failure, not the cancelled sibling
        Assert.Equal("找不到音箱", ex.InnerException!.Message);
        Assert.DoesNotContain(_log, s => s.StartsWith("record"));
        Assert.Empty(ok.Receiver.Audio);
        Assert.Empty(ok.Receiver.Sync);
    }

    [Fact]
    public async Task A_failed_record_disposes_both_sessions_and_nothing_plays()
    {
        var a = Member("A");
        var b = Member("B", failOn: "record");
        var ex = await Assert.ThrowsAsync<GroupMemberException>(() =>
            SpeakerGroup.ConnectAsync([a.Setup(), b.Setup()], Fifo(), 30, default));

        Assert.Equal("B", ex.Member);
        Assert.True(a.Disposed && b.Disposed);
        Assert.DoesNotContain(_log, s => s.StartsWith("flush"));
        Thread.Sleep(300);
        Assert.Empty(a.Receiver.Audio);
        Assert.Empty(a.Receiver.Sync);
    }

    [Fact]
    public async Task Losing_either_speaker_ends_the_group()
    {
        var a = Member("A");
        var b = Member("B");
        using var group = await SpeakerGroup.ConnectAsync([a.Setup(), b.Setup()], Fifo(), 30, default);
        Assert.False(group.Lost.IsCompleted);

        b.Lose("音箱没有响应（网络中断？）");
        var text = await group.Lost.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("B", text);
        Assert.Equal("音箱没有响应（网络中断？）", group.LostReason);

        a.Lose("later"); // only the first loss counts
        Assert.Equal("音箱没有响应（网络中断？）", group.LostReason);

        group.Dispose();
        Assert.True(a.Disposed && b.Disposed);
    }

    [Fact]
    public async Task A_speaker_lost_during_setup_fails_the_connect()
    {
        var a = Member("A");
        var b = Member("B");
        b.LostReason = EventChannel.ClosedBySpeaker; // Lost fired before the group subscribed
        await Assert.ThrowsAsync<AirPlayException>(() => SpeakerGroup.ConnectAsync([a.Setup(), b.Setup()], Fifo(), 30, default));
        Assert.True(a.Disposed && b.Disposed);
    }

    [Fact]
    public void Member_volume_is_linked_with_an_offset_and_clamped()
    {
        Assert.Equal(60, SpeakerGroup.MemberVolume(50, 10));
        Assert.Equal(100, SpeakerGroup.MemberVolume(95, 10));
        Assert.Equal(0, SpeakerGroup.MemberVolume(5, -10));
        Assert.Equal(0, SpeakerGroup.MemberVolume(0, 10)); // muted stays muted
    }
}

public class GroupRunnerTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeReceiver> _receivers = [];

    public void Dispose()
    {
        foreach (var rx in _receivers) rx.Dispose();
    }

    private FakeMember Member(string name, string? failOn = null)
    {
        var rx = new FakeReceiver();
        _receivers.Add(rx);
        return new FakeMember(name, rx, _log) { FailOn = failOn };
    }

    private static async Task Until(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Reconnects_both_speakers_after_a_failed_connect_and_after_a_lost_session()
    {
        var attempts = new List<(FakeMember A, FakeMember B)>();
        var statuses = new ConcurrentQueue<(StreamState State, string Text)>();
        var delays = new ConcurrentQueue<TimeSpan>();
        var fifo = new AudioFifo(44100, 20, 60);

        var runner = new GroupRunner("客厅",
            ct =>
            {
                int n = attempts.Count + 1;
                var pair = (Member($"A{n}"), Member($"B{n}", failOn: n == 1 ? "prepare" : null));
                attempts.Add(pair);
                return SpeakerGroup.ConnectAsync([pair.Item1.Setup(), pair.Item2.Setup()], fifo, 40, ct);
            },
            (state, text) => statuses.Enqueue((state, text)),
            (wait, _) => { delays.Enqueue(wait); return Task.CompletedTask; });

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        // 1st attempt: B1 can't be found -> A1 is torn down too, retry. 2nd attempt streams.
        await Until(() => runner.Current != null && attempts.Count == 2);
        Assert.True(attempts[0].A.Disposed);
        Assert.Contains(statuses, s => s.State == StreamState.Retrying && s.Text.Contains("B1"));

        // A2 drops -> both A2 and B2 are torn down, and a fresh pair (A3, B3) connects.
        attempts[1].A.Lose("音箱没有响应（网络中断？）");
        await Until(() => attempts.Count == 3 && runner.Current != null);
        Assert.True(attempts[1].A.Disposed && attempts[1].B.Disposed);
        Assert.False(attempts[2].A.Disposed || attempts[2].B.Disposed);
        Assert.Contains(_log, s => s == "record A3");
        Assert.Contains(_log, s => s == "record B3");

        cts.Cancel();
        Assert.False(await run);
        Assert.True(attempts[2].A.Disposed && attempts[2].B.Disposed);
        Assert.Null(runner.Current);
        Assert.Equal(2, delays.Count);
        Assert.Equal(3, statuses.Count(s => s.State == StreamState.Connecting));
        Assert.Equal(2, statuses.Count(s => s.State == StreamState.Streaming));
    }

    [Fact]
    public async Task Backs_off_for_good_when_another_sender_takes_a_speaker_over()
    {
        int connects = 0;
        var statuses = new ConcurrentQueue<(StreamState State, string Text)>();
        FakeMember? a = null;
        var runner = new GroupRunner("客厅",
            ct =>
            {
                connects++;
                a = Member("A");
                return SpeakerGroup.ConnectAsync([a.Setup(), Member("B").Setup()], new AudioFifo(44100, 20, 60), 40, ct);
            },
            (state, text) => statuses.Enqueue((state, text)),
            (_, _) => Task.CompletedTask,
            takeoverGrace: TimeSpan.Zero);

        var run = runner.RunAsync(CancellationToken.None);
        await Until(() => runner.Current != null);
        a!.Lose(EventChannel.ClosedBySpeaker);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, connects);
        Assert.True(a.Disposed);
        Assert.Equal(StreamState.Idle, statuses.Last().State);
        Assert.Contains("占用", statuses.Last().Text);
    }
}
