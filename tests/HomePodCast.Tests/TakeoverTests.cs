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
    public void Decides_by_trigger_flag_and_whether_the_session_was_ours(ConnectTrigger trigger, SpeakerBusy busy, bool ours, TakeoverAction expected)
    {
        Assert.Equal(expected, TakeoverPolicy.Decide(trigger, busy, ours));
        // Another sender's session: the home-theater setting changes nothing.
        foreach (var mode in Enum.GetValues<HomeTheaterMode>())
            Assert.Equal(expected, TakeoverPolicy.Decide(trigger, busy, ours, BusyHolder.OtherSender, mode));
    }

    private const BusyHolder Theater = BusyHolder.HomeTheater, Other = BusyHolder.OtherSender;
    private const HomeTheaterMode TakeOverTemporarily = HomeTheaterMode.TakeOver, AskFirst = HomeTheaterMode.Ask;

    [Theory]
    // 临时接管 (the default): a HomePod its home-theater Apple TV holds is taken over by every connect...
    [InlineData(Manual, false, TakeOverTemporarily, false, Connect)]
    [InlineData(Automatic, false, TakeOverTemporarily, false, Connect)]
    [InlineData(Automatic, true, TakeOverTemporarily, false, Connect)]
    [InlineData(Reconnect, false, TakeOverTemporarily, false, Connect)]  // after a long sleep
    // ...but never taken back after the Apple TV ended our session itself.
    [InlineData(Reconnect, false, TakeOverTemporarily, true, Wait)]
    [InlineData(Automatic, false, TakeOverTemporarily, true, Wait)]
    // 先询问: like any other sender's session.
    [InlineData(Manual, false, AskFirst, false, Ask)]
    [InlineData(Manual, true, AskFirst, false, Connect)]
    [InlineData(Automatic, false, AskFirst, false, Wait)]
    [InlineData(Reconnect, false, AskFirst, false, Wait)]
    [InlineData(TakeOver, false, AskFirst, false, Connect)]
    // Our own live session: never read, whatever the setting.
    [InlineData(Reconnect, true, AskFirst, true, Connect)]
    public void A_HomePod_held_by_its_home_theater_Apple_TV_is_taken_over_unless_the_setting_says_ask(ConnectTrigger trigger, bool ours,
        HomeTheaterMode mode, bool endedBySpeaker, TakeoverAction expected) =>
        Assert.Equal(expected, TakeoverPolicy.Decide(trigger, Busy, ours, Theater, mode, endedBySpeaker));

    [Fact]
    public void Only_a_busy_reading_held_by_the_home_theater_with_the_takeover_setting_takes_over_without_asking()
    {
        Assert.True(TakeoverPolicy.TakesOverHomeTheater(Theater, TakeOverTemporarily));
        Assert.False(TakeoverPolicy.TakesOverHomeTheater(Theater, TakeOverTemporarily, endedBySpeaker: true));
        Assert.False(TakeoverPolicy.TakesOverHomeTheater(Theater, AskFirst));
        Assert.False(TakeoverPolicy.TakesOverHomeTheater(Other, TakeOverTemporarily)); // an iPhone or a Mac: never
        Assert.False(TakeoverPolicy.TakesOverHomeTheater(BusyHolder.None, TakeOverTemporarily));
        // Free or unreadable speakers connect anyway.
        Assert.Equal(Connect, TakeoverPolicy.Decide(Automatic, Free, false, BusyHolder.None, AskFirst));
        Assert.Equal(Connect, TakeoverPolicy.Decide(Automatic, Unknown, false, BusyHolder.None, AskFirst));
    }

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

    /// <summary>Answers from a mutable set of busy speaker names (held by <see cref="HeldBy"/> when set); counts the reads.</summary>
    private sealed class FakeBusyReader : ISpeakerBusyReader
    {
        public readonly HashSet<string> Busy = [];
        public int Reads;

        /// <summary>The home-theater Apple TV holding every busy speaker; null: another sender.</summary>
        public volatile AirPlayDevice? HeldBy;

        public Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            lock (Busy)
                return Task.FromResult(BusyReading.Combine(targets.Select(t => Busy.Contains(t.Name)
                    ? new BusyReading.Member(t.Name, SpeakerBusy.Busy, t.Name, HeldBy)
                    : new BusyReading.Member(t.Name, SpeakerBusy.Free, t.Name)).ToList()));
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

    // ---------------------------------------------------------------- an Apple TV's home-theater HomePod

    private static readonly AirPlayDevice Tv = HomeTheaterTests.AppleTv("电视");
    private const string HeldText = "卧室是 Apple TV「电视」的家庭影院音箱，Apple TV 正连着它";

    [Fact]
    public async Task An_automatic_connect_takes_a_HomePod_over_from_its_home_theater_Apple_TV_and_ends_every_session_on_disconnect()
    {
        var (c, reader) = Controller();                          // the default setting: 临时接管
        Assert.Equal(HomeTheaterMode.TakeOver, c.HomeTheaterMode);
        reader.HeldBy = Tv;
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "streaming");
        Assert.Equal("已连接 · 卧室 + 客厅（临时从 Apple TV「电视」接管，断开后交还）", c.StatusText);
        Assert.Equal("电视", c.TakenFromAppleTv);
        Assert.Equal(2, Members().Count);

        // 断开 (and quit, which stops the controller too): every session ends with its TEARDOWN (the real member's
        // Dispose), so nothing lingers on the HomePod and the Apple TV can connect again.
        c.Stop();
        Assert.All(Members(), m => Assert.True(m.Disposed));
        Assert.False(c.OwnSession.Live);
        Assert.Equal("未连接", c.StatusText);

        // Next time the speaker is free: an ordinary connection again.
        reader.Set();
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming && Members().Count == 4, "streaming again");
        Assert.Null(c.TakenFromAppleTv);
        Assert.Equal("已连接 · 卧室 + 客厅（实验性）", c.StatusText);
    }

    [Fact]
    public async Task With_ask_first_an_automatic_connect_waits_naming_the_Apple_TV_and_the_takeover_setting_connects_at_once()
    {
        var (c, reader) = Controller();
        c.HomeTheaterMode = HomeTheaterMode.Ask;
        c.BusyPollInterval = TimeSpan.FromHours(1);              // only RecheckSpeaker reads again in this test
        reader.HeldBy = Tv;
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.StatusText == HeldText, "waiting");
        Assert.True(c.WaitingForSpeaker);
        Assert.Equal(StreamState.Idle, c.State);
        Assert.Empty(Members());

        c.HomeTheaterMode = HomeTheaterMode.TakeOver;            // 设置 → 临时接管
        c.RecheckSpeaker();
        await Until(() => c.State == StreamState.Streaming, "streaming after the setting changed");
        Assert.Equal("电视", c.TakenFromAppleTv);
    }

    [Fact]
    public async Task A_waiting_connect_reads_again_as_soon_as_a_scan_found_the_Apple_TV()
    {
        var (c, reader) = Controller();
        c.BusyPollInterval = TimeSpan.FromHours(1);
        reader.Set("卧室");                                      // no scan yet: the Apple TV is not known, so "another device"
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.StatusText == "卧室正在被其他设备使用，空闲后自动连接", "waiting");

        reader.HeldBy = Tv;                                      // the scan arrived (TrayApp.RefreshDevices calls RecheckSpeaker)
        c.RecheckSpeaker();
        await Until(() => c.State == StreamState.Streaming, "taken over right after the scan");
    }

    [Fact]
    public async Task When_the_Apple_TV_takes_its_HomePod_back_a_retry_waits_instead_of_taking_it_again()
    {
        var (c, reader) = Controller();
        reader.HeldBy = Tv;
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "streaming");

        Members().Last(m => m.Name == "A").Lose(EventChannel.ClosedBySpeaker); // the Apple TV played something
        await Until(() => c.StatusText == HeldText, "waiting instead of fighting the Apple TV");
        Assert.True(c.WaitingForSpeaker);
        Assert.Equal(2, Members().Count);
    }

    [Fact]
    public async Task A_confirmed_takeover_of_a_home_theater_HomePod_says_so_without_reading()
    {
        var (c, reader) = Controller();
        c.HomeTheaterMode = HomeTheaterMode.Ask;
        reader.HeldBy = Tv;
        reader.Set("卧室");
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.TakeOver, takenFrom: "电视"); // 先询问 → 仍然连接
        await Until(() => c.State == StreamState.Streaming, "streaming");
        Assert.Equal(0, reader.Reads);
        Assert.Equal("已连接 · 卧室 + 客厅（临时从 Apple TV「电视」接管，断开后交还）", c.StatusText);
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

    // ---------------------------------------------------------------- taken after the 15 s grace: no ping-pong

    [Theory]
    [InlineData(true)]  // the Apple TV took its home-theater HomePod back (it played something)
    [InlineData(false)] // an iPhone took the speaker
    public async Task A_speaker_taken_after_the_grace_ends_the_connection_for_good_and_nothing_takes_it_back(bool appleTv)
    {
        var (c, reader) = Controller();
        c.TakeoverGrace = TimeSpan.Zero;                          // "more than 15 s after connecting", without the wait
        if (appleTv)
        {
            reader.HeldBy = Tv;
            reader.Set("卧室");
        }
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "streaming");
        Assert.Equal(appleTv ? "电视" : null, c.TakenFromAppleTv);

        reader.HeldBy = appleTv ? Tv : null;
        reader.Set("卧室");                                        // the other sender holds it now
        int reads = reader.Reads;
        Members().Last(m => m.Name == "A").Lose(EventChannel.ClosedBySpeaker);
        await Until(() => c.StatusText == "已断开（音箱可能被其他设备占用）", "backing off");
        Assert.Equal(StreamState.Idle, c.State);
        Assert.False(c.WaitingForSpeaker);
        Assert.True(UI.TrayApp.GivesUp(c.State, c.StatusText));   // the app stops wanting the connection: no reconnect
                                                                   // on a network change or a wake from sleep
        await Task.Delay(200);                                     // ten poll intervals: nothing reads, nothing connects
        Assert.Equal(reads, reader.Reads);
        Assert.Equal(2, Members().Count);
        Assert.All(Members(), m => Assert.True(m.Disposed));
        Assert.Null(c.Capture);                                    // nothing captured or sent any more
        Assert.False(c.OwnSession.IsRecent(OwnSession.KeyOf(Plan), DateTime.UtcNow)); // the flag is the other sender's
    }

    [Fact]
    public void Only_the_taken_over_state_makes_the_app_give_up_the_connection()
    {
        Assert.True(UI.TrayApp.GivesUp(StreamState.Idle, StreamController.TakenOverText));
        Assert.False(UI.TrayApp.GivesUp(StreamState.Idle, "未连接"));
        Assert.False(UI.TrayApp.GivesUp(StreamState.Idle, "卧室正在被其他设备使用，空闲后自动连接")); // waiting: still wanted
        Assert.False(UI.TrayApp.GivesUp(StreamState.Retrying, StreamController.TakenOverText));
        Assert.Contains("占用", L.T(StreamController.TakenOverText)); // what the tray tip shows
    }

    [Fact]
    public async Task After_a_short_sleep_the_reconnect_at_wake_connects_without_waiting_for_its_own_stale_session()
    {
        var (c, reader) = Controller();
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "streaming");
        int reads = reader.Reads;

        c.OwnSession.Suspending(DateTime.UtcNow);                  // TrayApp.OnSuspending
        reader.Set("卧室", "客厅");                                  // our session, not ended yet on the speakers
        c.StartGroup(Plan, 150, 40, trigger: ConnectTrigger.Reconnect); // TrayApp: a few seconds after the wake
        await Until(() => Members().Count == 4 && c.State == StreamState.Streaming, "streaming again");
        Assert.Equal(reads, reader.Reads);                         // its own flag: not read, not waited for
        Assert.True(c.OwnSession.Live);
    }
}
