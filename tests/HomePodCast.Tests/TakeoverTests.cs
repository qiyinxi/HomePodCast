using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>TakeoverPolicy's table, OwnSession's windows, and how a reading of several speakers combines.</summary>
public class TakeoverPolicyTests
{
    private const ConnectTrigger Manual = ConnectTrigger.Manual, TakeOver = ConnectTrigger.TakeOver,
        Automatic = ConnectTrigger.Automatic, Reconnect = ConnectTrigger.Reconnect;
    private const SpeakerBusy Free = SpeakerBusy.Free, Busy = SpeakerBusy.Busy, Unknown = SpeakerBusy.Unknown;
    private const TakeoverAction Connect = TakeoverAction.Connect, Ask = TakeoverAction.Ask, Wait = TakeoverAction.Wait;

    [Theory]
    // A click: asks before taking another sender's session over; a session that was ours a moment ago is no takeover.
    [InlineData(Manual, Free, false, Connect)]
    [InlineData(Manual, Unknown, false, Connect)]
    [InlineData(Manual, Busy, false, Ask)]
    [InlineData(Manual, Busy, true, Connect)]
    [InlineData(Manual, Free, true, Connect)]
    // The user said 仍然连接.
    [InlineData(TakeOver, Busy, false, Connect)]
    [InlineData(TakeOver, Busy, true, Connect)]
    [InlineData(TakeOver, Free, false, Connect)]
    // Startup, the first speaker found, another speaker picked: never take over; wait (also for our own stale session).
    [InlineData(Automatic, Free, false, Connect)]
    [InlineData(Automatic, Unknown, false, Connect)]
    [InlineData(Automatic, Busy, false, Wait)]
    [InlineData(Automatic, Busy, true, Wait)]
    [InlineData(Automatic, Free, true, Connect)]
    // Our own session again: not held back while the flag may be ours; long after it (a long sleep) like Automatic.
    [InlineData(Reconnect, Busy, true, Connect)]
    [InlineData(Reconnect, Free, true, Connect)]
    [InlineData(Reconnect, Busy, false, Wait)]
    [InlineData(Reconnect, Free, false, Connect)]
    [InlineData(Reconnect, Unknown, false, Connect)]
    public void Decides_by_trigger_flag_and_whether_the_session_was_ours(ConnectTrigger trigger, SpeakerBusy busy, bool ours, TakeoverAction expected) =>
        Assert.Equal(expected, TakeoverPolicy.Decide(trigger, busy, ours));

    [Theory]
    [InlineData(TakeOver, false, true)]
    [InlineData(TakeOver, true, true)]
    [InlineData(Reconnect, true, true)]
    [InlineData(Reconnect, false, false)]
    [InlineData(Manual, true, false)]
    [InlineData(Automatic, true, false)]
    public void Only_a_takeover_and_a_reconnect_of_our_live_session_skip_reading(ConnectTrigger trigger, bool ours, bool skips) =>
        Assert.Equal(skips, TakeoverPolicy.SkipsCheck(trigger, ours));

    [Fact]
    public void A_retry_is_our_own_reconnect_only_after_the_connection_has_streamed()
    {
        Assert.Equal(Automatic, TakeoverPolicy.RetryTrigger(Automatic, streamed: false)); // a startup that never got through
        Assert.Equal(Manual, TakeoverPolicy.RetryTrigger(Manual, streamed: false));
        Assert.Equal(TakeOver, TakeoverPolicy.RetryTrigger(TakeOver, streamed: false));
        foreach (var first in Enum.GetValues<ConnectTrigger>())
            Assert.Equal(Reconnect, TakeoverPolicy.RetryTrigger(first, streamed: true));
    }

    [Fact]
    public void Only_an_automatic_connect_waits_for_our_own_last_session()
    {
        Assert.True(TakeoverPolicy.WaitsForOwnSession(Automatic, oursRecently: true));
        Assert.False(TakeoverPolicy.WaitsForOwnSession(Automatic, oursRecently: false));
        Assert.False(TakeoverPolicy.WaitsForOwnSession(Reconnect, oursRecently: true));
        Assert.False(TakeoverPolicy.WaitsForOwnSession(Manual, oursRecently: true));
    }

    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Our_session_is_ours_while_live_and_for_a_window_after_it_ends()
    {
        var s = new OwnSession();
        Assert.False(s.IsRecent("A", T0));
        s.Began("A", T0);
        Assert.True(s.Live);
        Assert.True(s.IsRecent("A", T0.AddHours(5)));      // live: whatever the clock says
        Assert.False(s.IsRecent("B", T0));                  // another selection
        Assert.False(s.IsRecent(null, T0));

        s.Ended(T0.AddMinutes(10), clean: false);           // lost: the speaker keeps it until it times out
        Assert.False(s.Live);
        Assert.True(s.IsRecent("A", T0.AddMinutes(10) + OwnSession.StaleWindow - TimeSpan.FromSeconds(1)));
        Assert.False(s.IsRecent("A", T0.AddMinutes(10) + OwnSession.StaleWindow));
        Assert.Equal(TimeSpan.FromMinutes(2), s.Remaining("A", T0.AddMinutes(11)));

        s.Began("A", T0.AddMinutes(20));
        s.Ended(T0.AddMinutes(30), clean: true);            // our TEARDOWN: the flag drops at once
        Assert.True(s.IsRecent("A", T0.AddMinutes(30).AddSeconds(10)));
        Assert.False(s.IsRecent("A", T0.AddMinutes(30) + OwnSession.CleanWindow));
        Assert.False(s.IsRecent("A", T0.AddMinutes(29)));   // the clock went back: not ours
    }

    [Fact]
    public void A_session_the_speaker_ended_itself_is_not_ours()
    {
        var s = new OwnSession();
        s.Began("A", T0);
        s.Forget();
        Assert.False(s.IsRecent("A", T0));
        Assert.Null(s.Record(T0));
    }

    [Fact]
    public void A_sleep_ends_the_session_when_it_starts_not_when_the_reconnect_at_wake_stops_it()
    {
        var s = new OwnSession();
        s.Began("A", T0);
        s.Suspending(T0.AddMinutes(1));
        s.Ended(T0.AddHours(8), clean: true);               // the wake's restart stops the old loop: changes nothing
        Assert.False(s.IsRecent("A", T0.AddHours(8)));      // after a long sleep the flag is someone else's

        s.Began("A", T0);
        s.Suspending(T0.AddMinutes(1));
        Assert.True(s.IsRecent("A", T0.AddMinutes(2)));     // a short one: may still be ours
    }

    [Fact]
    public void The_saved_record_says_alive_now_while_streaming_and_restores_as_ended()
    {
        var s = new OwnSession();
        s.Began("A", T0);
        Assert.Equal(new OwnSessionRecord("A", T0.AddMinutes(5), Clean: false), s.Record(T0.AddMinutes(5)));

        // The app was killed: the next start knows the flag may still be ours for the stale window after the last save.
        var next = new OwnSession();
        next.Restore(s.Record(T0.AddMinutes(5)));
        Assert.False(next.Live);
        Assert.True(next.IsRecent("A", T0.AddMinutes(6)));
        Assert.False(next.IsRecent("A", T0.AddMinutes(5) + OwnSession.StaleWindow));

        // A clean quit: only the clean window.
        s.Ended(T0.AddMinutes(9), clean: true);
        var record = s.Record(T0.AddHours(1));
        Assert.Equal(new OwnSessionRecord("A", T0.AddMinutes(9), Clean: true), record);
        var after = new OwnSession();
        after.Restore(record);
        Assert.True(after.IsRecent("A", T0.AddMinutes(9).AddSeconds(5)));
        Assert.False(after.IsRecent("A", T0.AddMinutes(10)));

        after.Restore(null); // nothing saved: unchanged
        Assert.True(after.IsRecent("A", T0.AddMinutes(9).AddSeconds(5)));
    }

    [Fact]
    public void The_last_session_survives_a_config_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hpc-takeover-{Guid.NewGuid():N}.json");
        try
        {
            new AppConfig { LastOwnSession = new OwnSessionRecord("AABBCC", T0, Clean: false) }.Save(path);
            var loaded = AppConfig.Load(path).LastOwnSession;
            Assert.Equal(new OwnSessionRecord("AABBCC", T0, Clean: false), loaded);
            Assert.Equal(DateTimeKind.Utc, loaded!.AliveUtc.Kind);
            Assert.Null(AppConfig.Load(Path.Combine(Path.GetTempPath(), $"hpc-missing-{Guid.NewGuid():N}.json")).LastOwnSession);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Keys_name_the_selection()
    {
        Assert.Equal("AABBCC", OwnSession.KeyOf("aa:bb:cc"));
        var pair = new GroupPlan(GroupKind.StereoPair, "卧室", "abc", [], false, false, new Dictionary<string, int>());
        Assert.Equal(OwnSession.KeyOf("pair:ABC"), OwnSession.KeyOf(pair));
        var multi = new GroupPlan(GroupKind.MultiRoom, "A + B", null,
            [new GroupMemberRef("a:1", "A", null), new GroupMemberRef("b:2", "B", null)], false, false, new Dictionary<string, int>());
        Assert.Equal("A1+B2", OwnSession.KeyOf(multi));

        Assert.Null(OwnSession.KeyOf(new AppConfig()));
        Assert.Equal("AABBCC", OwnSession.KeyOf(new AppConfig { DeviceId = "AA:BB:CC" }));
        Assert.Equal("AABBCC+DDEEFF", OwnSession.KeyOf(new AppConfig { DeviceId = "AA:BB:CC", MultiRoomDeviceId = "DD:EE:FF" }));
    }

    [Fact]
    public void A_group_is_busy_when_any_member_is_and_names_it()
    {
        var reading = BusyReading.Combine([new("左", Free, "l"), new("右", Busy, "r")]);
        Assert.Equal(Busy, reading.State);
        Assert.Equal(["右"], reading.BusyNames);
        Assert.Equal("l; r", reading.Detail);

        Assert.Equal(Busy, BusyReading.Combine([new("左", Unknown, ""), new("右", Busy, "")]).State);
        Assert.Equal(Unknown, BusyReading.Combine([new("左", Unknown, ""), new("右", Free, "")]).State);
        Assert.Equal(Free, BusyReading.Combine([new("左", Free, ""), new("右", Free, "")]).State);
        Assert.Equal(Unknown, BusyReading.Combine([]).State);
        Assert.Equal(["左", "右"], BusyReading.Combine([new("左", Busy, ""), new("右", Busy, "")]).BusyNames);
    }

    [Fact]
    public void A_member_reads_busy_from_statusFlags_or_the_TXT_inside_info()
    {
        var target = new BusyTarget("02:DC:8E:68:21:FB", "fallback", IPAddress.Parse("192.168.50.226"));
        var busy = BusyReading.Of(target, Ready(new() { ["name"] = "卧室", ["statusFlags"] = 0x1A0404L }));
        Assert.Equal(("卧室", Busy), (busy.Name, busy.State));
        Assert.Contains("192.168.50.226", busy.Detail);

        var free = BusyReading.Of(target, Ready(new() { ["statusFlags"] = 0x80404L }));
        Assert.Equal(("fallback", Free), (free.Name, free.State));

        var txtOnly = BusyReading.Of(target, Ready(new() { ["txtAirPlay"] = SpeakerInfoTests.Txt("flags=0x1a0404") }));
        Assert.Equal(Busy, txtOnly.State);

        Assert.Equal(Unknown, BusyReading.Of(target, Ready(new() { ["name"] = "卧室" })).State);
        var failed = BusyReading.Of(target, new SpeakerInfoEntry(SpeakerInfoState.Failed, new Dictionary<string, object?> { ["statusFlags"] = 0x1A0404L },
            null, "no answer in 2 s"));
        Assert.Equal(Unknown, failed.State); // an old reading never counts
        Assert.Contains("no answer", failed.Detail);
    }

    private static SpeakerInfoEntry Ready(Dictionary<string, object?> info) => new(SpeakerInfoState.Ready, info, DateTime.UtcNow, null);

    [Fact]
    public async Task The_busy_reader_reads_every_member_through_the_info_cache()
    {
        var bedroom = IPAddress.Parse("192.168.50.226");
        var cache = new SpeakerInfoCache((address, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(address.Equals(bedroom)
            ? SpeakerInfoTests.BedroomInfo()
            : new Dictionary<string, object?> { ["name"] = "厨房", ["statusFlags"] = 0x80404L }));
        var reader = new InfoBusyReader(cache);

        var reading = await reader.ReadAsync([new BusyTarget("A", "a", bedroom), new BusyTarget("B", "b", IPAddress.Parse("192.168.50.20"))],
            CancellationToken.None);
        Assert.Equal(Busy, reading.State);
        Assert.Equal(["卧室"], reading.BusyNames);
        Assert.NotNull(cache.Get("B")); // 设置 → 音箱信息 shows what the check read

        var offline = await reader.ReadAsync([new BusyTarget("C", "书房", null)], CancellationToken.None);
        Assert.Equal(Unknown, offline.State);
    }
}

/// <summary>
/// The check inside StreamController, on a group of fake speakers (no mDNS, no RTSP, no audio device) and a fake flag
/// reader: what waits, what connects, and what is never read.
/// </summary>
public class TakeoverControllerTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeReceiver> _receivers = [];
    private readonly List<FakeMember> _members = [];
    private readonly List<StreamController> _controllers = [];
    private int _captures;

    public void Dispose()
    {
        foreach (var c in _controllers) c.Dispose();
        foreach (var rx in _receivers) rx.Dispose();
    }

    /// <summary>Answers from a mutable set of busy speaker names; counts the reads.</summary>
    private sealed class FakeBusyReader : ISpeakerBusyReader
    {
        public readonly HashSet<string> Busy = [];
        public int Reads;

        public Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            lock (Busy)
                return Task.FromResult(BusyReading.Combine(targets.Select(t =>
                    new BusyReading.Member(t.Name, Busy.Contains(t.Name) ? SpeakerBusy.Busy : SpeakerBusy.Free, t.Name)).ToList()));
        }

        public void Set(params string[] busy)
        {
            lock (Busy)
            {
                Busy.Clear();
                Busy.UnionWith(busy);
            }
        }
    }

    private sealed class FakeCapture : ICaptureSource
    {
        public string? DeviceName => "fake";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    private static readonly GroupPlan Plan = new(GroupKind.MultiRoom, "卧室 + 客厅", null,
        [new GroupMemberRef("A", "卧室", null), new GroupMemberRef("B", "客厅", null)], false, false, new Dictionary<string, int>());

    private (StreamController Controller, FakeBusyReader Reader) Controller()
    {
        var reader = new FakeBusyReader();
        var c = new StreamController
        {
            CaptureFactory = _ =>
            {
                Interlocked.Increment(ref _captures);
                return new FakeCapture();
            },
            ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members
                .Select(m => new StreamController.ResolvedMember(m.DeviceId, m.Name, IPAddress.Loopback, 7000)).ToList()),
            BusyReader = reader,
            BusyPollInterval = TimeSpan.FromMilliseconds(20),
        };
        c.PrepareGroupMember = (m, _, channels, _) =>
        {
            var rx = new FakeReceiver();
            var member = new FakeMember(m.DeviceId, rx, _log, channels);
            lock (_members)
            {
                _receivers.Add(rx);
                _members.Add(member);
            }
            return Task.FromResult<IGroupMember>(member);
        };
        _controllers.Add(c);
        return (c, reader);
    }

    private List<FakeMember> Members()
    {
        lock (_members) return [.. _members];
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task An_automatic_connect_waits_while_a_member_is_busy_names_it_and_connects_once_it_is_free()
    {
        var (c, reader) = Controller();
        reader.Set("客厅");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.StatusText == "客厅正在被其他设备使用，空闲后自动连接", "waiting");
        Assert.True(c.WaitingForSpeaker);
        Assert.Equal(StreamState.Idle, c.State);                 // 连接 stays available (it asks)
        await Until(() => reader.Reads >= 3, "reading again");
        Assert.Equal(0, Volatile.Read(ref _captures));           // nothing captured while waiting
        Assert.Empty(Members());                                 // and no session opened

        reader.Set();
        await Until(() => c.State == StreamState.Streaming, "streaming");
        Assert.False(c.WaitingForSpeaker);
        Assert.True(c.OwnSession.Live);
        Assert.True(c.OwnSession.IsRecent(OwnSession.KeyOf(Plan), DateTime.UtcNow));

        c.Stop();
        Assert.False(c.OwnSession.Live);
        Assert.True(c.OwnSession.IsRecent(OwnSession.KeyOf(Plan), DateTime.UtcNow)); // our TEARDOWN: briefly ours
    }

    [Fact]
    public async Task A_reconnect_of_our_live_session_never_reads_and_takes_its_own_flag()
    {
        var (c, reader) = Controller();
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "streaming");
        int reads = reader.Reads;

        reader.Set("卧室", "客厅");                               // our own session sets the flag
        c.StartGroup(Plan, 160, 40, trigger: ConnectTrigger.Reconnect); // a new latency: restart
        await Until(() => Members().Count == 4 && c.State == StreamState.Streaming, "streaming again");
        Assert.Equal(reads, reader.Reads);
    }

    [Fact]
    public async Task A_reconnect_long_after_our_session_waits_like_an_automatic_connect()
    {
        var (c, reader) = Controller();
        c.OwnSession.Began(OwnSession.KeyOf(Plan), DateTime.UtcNow.AddHours(-2));
        c.OwnSession.Suspending(DateTime.UtcNow.AddHours(-2));     // slept for two hours
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Reconnect);
        await Until(() => c.StatusText == "卧室正在被其他设备使用，空闲后自动连接", "waiting");
        Assert.True(c.WaitingForSpeaker);
    }

    [Fact]
    public async Task A_confirmed_takeover_never_reads()
    {
        var (c, reader) = Controller();
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.TakeOver);
        await Until(() => c.State == StreamState.Streaming, "streaming");
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task A_click_whose_speaker_turned_busy_waits_here_since_only_the_ui_can_ask()
    {
        var (c, reader) = Controller();
        reader.Set("卧室", "客厅");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Manual);
        await Until(() => c.StatusText == "卧室 / 客厅正在被其他设备使用，空闲后自动连接", "waiting");
        Assert.True(c.WaitingForSpeaker);

        c.Stop();                                                  // 断开 (or a new 连接) ends the wait
        Assert.False(c.WaitingForSpeaker);
        Assert.Equal(StreamState.Idle, c.State);
        Assert.Equal("未连接", c.StatusText);
    }

    [Fact]
    public async Task At_startup_a_flag_our_crashed_session_may_hold_is_waited_for_with_a_bound()
    {
        var (c, reader) = Controller();
        var now = DateTime.UtcNow;
        c.UtcNow = () => now;
        c.OwnSession.Restore(new OwnSessionRecord(OwnSession.KeyOf(Plan), now.AddSeconds(-30), Clean: false));
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.StatusText == "卧室可能还保留着这台电脑上次的连接，结束后自动连接（最多再等 150 秒）", "waiting for our own session");
        Assert.True(c.WaitingForSpeaker);

        now = now.AddMinutes(3);                                   // past the window: the flag is someone else's
        await Until(() => c.StatusText == "卧室正在被其他设备使用，空闲后自动连接", "the other sender's wait");
        Assert.Equal(StreamState.Idle, c.State);

        reader.Set();
        await Until(() => c.State == StreamState.Streaming, "streaming");
    }

    [Fact]
    public async Task After_the_speaker_ends_our_session_a_retry_checks_the_flag_again()
    {
        var (c, reader) = Controller();
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "streaming");

        reader.Set("卧室");                                        // an iPhone took it over within the grace period
        Members().Last(m => m.Name == "A").Lose(EventChannel.ClosedBySpeaker);
        await Until(() => c.StatusText == "卧室正在被其他设备使用，空闲后自动连接", "waiting instead of taking it back");
        Assert.False(c.OwnSession.IsRecent(OwnSession.KeyOf(Plan), DateTime.UtcNow));
        Assert.Equal(2, Members().Count);
    }
}
