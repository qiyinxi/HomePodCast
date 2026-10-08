using System.Globalization;
using System.Text.Json;
using HomePodCast.Players;

namespace HomePodCast.Tests;

/// <summary>Delay arithmetic: the sign and the seconds/ms conversion every player relies on.</summary>
public class PlayerDelayTests
{
    [Theory]
    [InlineData(236, -236)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)] // a player is never told to delay its sound
    public void Target_is_the_sound_lag_with_the_sign_flipped(int videoDelayMs, int expected) =>
        Assert.Equal(expected, PlayerDelay.TargetMs(videoDelayMs));

    [Fact]
    public void The_target_matches_the_local_api_value()
    {
        var cfg = new AppConfig { VideoDelayExtraMs = 36 };
        int videoDelay = UI.LatencyTuner.SoundLagMs(200, cfg, captureExtraMs: 0); // 影视: 200 ms + 36 ms
        Assert.Equal(236, videoDelay);
        Assert.Equal(-236, PlayerDelay.TargetMs(videoDelay));
        Assert.Equal(-0.236, PlayerDelay.ToSeconds(PlayerDelay.TargetMs(videoDelay)), 9);
    }

    [Theory]
    [InlineData(-236, "-0.236")]
    [InlineData(0, "0")]
    [InlineData(1500, "1.5")]
    [InlineData(-5, "-0.005")]
    [InlineData(-271, "-0.271")]
    public void Seconds_are_written_invariantly_without_exponent(int ms, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // decimal comma must not leak in
            Assert.Equal(expected, PlayerDelay.SecondsText(ms));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Theory]
    [InlineData(-0.236, -236)]
    [InlineData(-0.2355, -236)]
    [InlineData(0.0004, 0)]
    [InlineData(1.0, 1000)]
    public void Seconds_read_back_as_whole_ms(double seconds, int ms) => Assert.Equal(ms, PlayerDelay.FromSeconds(seconds));

    [Fact]
    public void Display_text_keeps_an_ascii_minus() => Assert.Equal("-236", PlayerDelay.Text(-236));
}

/// <summary>mpv's JSON IPC lines and where its pipe name comes from.</summary>
public class MpvIpcTests
{
    [Fact]
    public void Set_request_is_one_json_line_with_seconds()
    {
        var line = MpvIpc.SetDelayRequest(-236, 7);
        Assert.Equal("{\"command\":[\"set_property\",\"audio-delay\",-0.236],\"request_id\":7}\n", line);
        Assert.Single(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        var cmd = doc.RootElement.GetProperty("command");
        Assert.Equal("set_property", cmd[0].GetString());
        Assert.Equal("audio-delay", cmd[1].GetString());
        Assert.Equal(-0.236, cmd[2].GetDouble());
    }

    [Fact]
    public void Get_request_asks_for_audio_delay() =>
        Assert.Equal("{\"command\":[\"get_property\",\"audio-delay\"],\"request_id\":3}\n", MpvIpc.GetDelayRequest(3));

    [Fact]
    public void Reply_is_matched_by_request_id_and_events_are_skipped()
    {
        Assert.False(MpvIpc.TryParseReply("{\"event\":\"playback-restart\"}", 5, out _, out _, out _));
        Assert.False(MpvIpc.TryParseReply("{\"data\":1.0,\"request_id\":4,\"error\":\"success\"}", 5, out _, out _, out _));
        Assert.False(MpvIpc.TryParseReply("not json", 5, out _, out _, out _));
        Assert.False(MpvIpc.TryParseReply("", 5, out _, out _, out _));

        Assert.True(MpvIpc.TryParseReply("{\"data\":-0.236000,\"request_id\":5,\"error\":\"success\"}", 5, out bool ok, out double? data, out _));
        Assert.True(ok);
        Assert.Equal(-0.236, data!.Value, 9);

        Assert.True(MpvIpc.TryParseReply("{\"request_id\":5,\"error\":\"property not found\"}", 5, out ok, out data, out var error));
        Assert.False(ok);
        Assert.Null(data);
        Assert.Equal("property not found", error);

        // very old mpv: no request_id in the reply
        Assert.True(MpvIpc.TryParseReply("{\"data\":null,\"error\":\"success\"}", 9, out ok, out data, out _));
        Assert.True(ok);
        Assert.Null(data);
    }

    [Theory]
    [InlineData(@"--input-ipc-server=\\.\pipe\mpvsocket", "mpvsocket")]
    [InlineData(@"--input-ipc-server=\\?\pipe\mpv-1", "mpv-1")]
    [InlineData("--input-ipc-server=bare", "bare")]
    [InlineData("--input-ipc-server=\"quoted name\"", "quoted name")]
    public void Pipe_comes_from_the_option(string option, string pipe) =>
        Assert.Equal(pipe, MpvIpc.PipeFromArgs(["mpv.exe", option, "film.mkv"]));

    [Fact]
    public void Pipe_option_with_a_separate_value_and_the_last_one_wins()
    {
        Assert.Equal("two", MpvIpc.PipeFromArgs(["mpv", "--input-ipc-server", "one", "--input-ipc-server=two"]));
        Assert.Null(MpvIpc.PipeFromArgs(["mpv", "film.mkv", "--input-ipc-server-x=1"]));
        Assert.Null(MpvIpc.PipeFromArgs(["mpv", "input-ipc-server=notanoption"]));
        Assert.Null(MpvIpc.PipeFromArgs(["mpv", "--input-ipc-server="]));
    }

    [Fact]
    public void Pipes_are_read_from_mpv_conf()
    {
        const string conf = """
            # input-ipc-server=commented
            volume=50
            input-ipc-server=\\.\pipe\mpvsocket
            [hd]
              input-ipc-server = other  # trailing comment
            --input-ipc-server=dashed
            input-ipc-server-extra=nope
            """;
        Assert.Equal(new[] { "mpvsocket", "other", "dashed" }, MpvIpc.PipesFromConfig(conf.Replace("\n", "\r\n")).ToArray());
    }

    [Fact]
    public void Suggested_config_line_names_a_pipe() =>
        Assert.Equal("mpvsocket", MpvIpc.PipesFromConfig(MpvIpc.ConfigLine).Single());
}

/// <summary>VLC's web interface: the request, the status it returns, and where the password comes from.</summary>
public class VlcHttpTests
{
    [Fact]
    public void Set_request_uses_the_audiodelay_command_in_seconds()
    {
        Assert.Equal("/requests/status.xml?command=audiodelay&val=-0.236", VlcHttp.SetDelayPath(-236));
        Assert.Equal("/requests/status.xml?command=audiodelay&val=0", VlcHttp.SetDelayPath(0));
    }

    [Fact]
    public void Basic_auth_has_an_empty_user_name() =>
        Assert.Equal(":secret", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(VlcHttp.BasicAuth("secret"))));

    private const string Playing = """
        <?xml version="1.0" encoding="utf-8" standalone="yes" ?>
        <root>
        <fullscreen>false</fullscreen>
        <audiodelay>-0.236</audiodelay>
        <apiversion>3</apiversion>
        <currentplid>4</currentplid>
        <time>12</time>
        <volume>256</volume>
        <state>playing</state>
        <subtitledelay>0</subtitledelay>
        </root>
        """;

    [Fact]
    public void Status_reports_delay_and_item()
    {
        Assert.Equal(new PlayerReading(-236, "4"), VlcHttp.ParseStatus(Playing));
        Assert.Equal(new PlayerReading(0, "4"), VlcHttp.ParseStatus(Playing.Replace("-0.236", "0")));
        Assert.Equal(new PlayerReading(1500, "4"), VlcHttp.ParseStatus(Playing.Replace("-0.236", "1.5")));
    }

    [Fact]
    public void Stopped_vlc_cannot_say()
    {
        const string stopped = "<root><apiversion>3</apiversion><currentplid>-1</currentplid><state>stopped</state></root>";
        Assert.Equal(new PlayerReading(null, null), VlcHttp.ParseStatus(stopped));
        Assert.Null(VlcHttp.ParseStatus(Playing.Replace("playing", "stopped")).DelayMs);
    }

    [Fact]
    public void Password_and_port_come_from_vlcrc_then_the_command_line()
    {
        const string vlcrc = """
            [core] # core program
            # HTTP server port (integer)
            #http-port=8080
            # Password (string)
            #http-password=
            [lua] # Lua interpreter
            http-password=s3cret = with equals
            """;
        var fromFile = VlcSettings.FromVlcrc(vlcrc.Replace("\n", "\r\n"));
        Assert.Equal(new VlcSettings("s3cret = with equals", 8080), fromFile);
        Assert.Equal(new VlcSettings(null, 9090), VlcSettings.FromVlcrc("http-port=9090\n"));
        Assert.Equal(VlcSettings.Default, VlcSettings.FromVlcrc("http-port=99999\n"));

        Assert.Equal(new VlcSettings("cli", 1234), fromFile.WithArgs(["vlc.exe", "--http-password=cli", "--http-port", "1234", "film.mkv"]));
        Assert.Equal(fromFile, fromFile.WithArgs(["vlc.exe", "-I", "http", "film.mkv"]));
    }

    [Fact]
    public void Listening_on_all_addresses_connects_to_loopback()
    {
        Assert.Equal(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 8080),
            VlcFinder.Loopback([new(System.Net.IPAddress.Any, 8080)]));
        Assert.Equal(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, 8080),
            VlcFinder.Loopback([new(System.Net.IPAddress.IPv6Any, 8080)]));
        var lan = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.5"), 8080);
        Assert.Equal(lan, VlcFinder.Loopback([lan]));
    }
}

/// <summary>The apply / restore state machine with fake players and a fake clock.</summary>
public class PlayerSyncTests
{
    private sealed class FakePlayer(string key, PlayerKind kind = PlayerKind.Mpv, int? delay = 0, string? item = null) : IPlayerEndpoint
    {
        public string Key { get; } = key;
        public PlayerKind Kind { get; } = kind;
        public string Name => Kind == PlayerKind.Vlc ? "VLC" : "mpv";
        public int? Delay { get; set; } = delay;
        public string? Item { get; set; } = item;
        public List<int> Writes { get; } = [];
        public int Reads { get; private set; }
        public PlayerProblem? Refuse { get; set; }
        public bool Broken { get; set; }

        public Task<PlayerReading> ReadAsync(CancellationToken ct)
        {
            Reads++;
            if (Refuse is { } p) throw new PlayerAccessException(p, "refused");
            if (Broken) throw new IOException("broken pipe");
            return Task.FromResult(new PlayerReading(Delay, Item));
        }

        public Task WriteAsync(int delayMs, CancellationToken ct)
        {
            if (Broken) throw new IOException("broken pipe");
            Writes.Add(delayMs);
            Delay = delayMs;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScanner : IPlayerScanner
    {
        public List<FakePlayer> Running { get; } = [];
        public List<PlayerNote> Notes { get; } = [];
        public int Scans { get; private set; }

        public Task<PlayerScan> ScanAsync(CancellationToken ct)
        {
            Scans++;
            return Task.FromResult(new PlayerScan([.. Running], [.. Notes]));
        }
    }

    private readonly FakeScanner _scanner = new();
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly PlayerSync _sync;

    public PlayerSyncTests() => _sync = new PlayerSync(_scanner, () => _now);

    private static PlayerSyncInput Streaming(int videoDelayMs = 236) => new(true, StreamState.Streaming, videoDelayMs);

    private Task Step(PlayerSyncInput input) => _sync.StepAsync(input, CancellationToken.None);

    [Fact]
    public async Task Applies_minus_the_sound_lag_and_restores_on_scene_change()
    {
        var mpv = new FakePlayer("mpv:1", delay: 50);
        _scanner.Running.Add(mpv);

        await Step(Streaming(236));
        Assert.Equal(new[] { -236 }, mpv.Writes);
        Assert.Equal(-236, _sync.TargetMs);
        Assert.Equal(new PlayerStatus(PlayerKind.Mpv, "mpv", PlayerState.Applied, -236), Assert.Single(_sync.Statuses));
        Assert.Equal("mpv：已设为 -236 ms", PlayerText.Line(_sync.Statuses[0]));

        await Step(Streaming(236)); // nothing changed: nothing written
        Assert.Single(mpv.Writes);

        await Step(Streaming(236) with { Enabled = false }); // scene switched away from 影视
        Assert.Equal(new[] { -236, 50 }, mpv.Writes);
        Assert.Empty(_sync.Statuses);
        Assert.Null(_sync.TargetMs);
        Assert.Equal(0, _sync.TrackedCount);
    }

    [Fact]
    public async Task Setting_off_or_another_scene_never_touches_a_player()
    {
        var mpv = new FakePlayer("mpv:1");
        _scanner.Running.Add(mpv);
        await Step(new PlayerSyncInput(false, StreamState.Streaming, 236));
        await Step(new PlayerSyncInput(false, StreamState.Idle, 0));
        Assert.Equal(0, _scanner.Scans);
        Assert.Equal(0, mpv.Reads);
        Assert.Empty(mpv.Writes);
    }

    [Fact]
    public async Task Nothing_happens_before_the_stream_is_up()
    {
        var mpv = new FakePlayer("mpv:1");
        _scanner.Running.Add(mpv);
        await Step(new PlayerSyncInput(true, StreamState.Connecting, 0));
        await Step(new PlayerSyncInput(true, StreamState.Idle, 0));
        Assert.Equal(0, _scanner.Scans);
        Assert.Empty(mpv.Writes);
    }

    [Fact]
    public async Task Disconnect_restores_at_once()
    {
        var vlc = new FakePlayer("vlc:2", PlayerKind.Vlc, delay: 0, item: "4");
        _scanner.Running.Add(vlc);
        await Step(Streaming());
        await Step(new PlayerSyncInput(true, StreamState.Idle, 0));
        Assert.Equal(new[] { -236, 0 }, vlc.Writes);
    }

    [Fact]
    public async Task A_short_reconnect_keeps_the_values_then_restores_after_the_grace()
    {
        var mpv = new FakePlayer("mpv:1");
        _scanner.Running.Add(mpv);
        await Step(Streaming());
        int scans = _scanner.Scans;

        _now += TimeSpan.FromSeconds(5);
        await Step(new PlayerSyncInput(true, StreamState.Retrying, 0));
        _now += TimeSpan.FromSeconds(5);
        await Step(new PlayerSyncInput(true, StreamState.Connecting, 0));
        Assert.Equal(new[] { -236 }, mpv.Writes); // held, not restored, nothing re-applied
        Assert.Equal(scans, _scanner.Scans);

        await Step(Streaming()); // back: still the same value, nothing to write
        Assert.Equal(new[] { -236 }, mpv.Writes);

        _now += TimeSpan.FromSeconds(1);
        await Step(new PlayerSyncInput(true, StreamState.Retrying, 0));
        _now += PlayerSync.RetryGrace;
        await Step(new PlayerSyncInput(true, StreamState.Retrying, 0));
        Assert.Equal(new[] { -236, 0 }, mpv.Writes);
    }

    [Fact]
    public async Task A_new_latency_is_applied_and_the_original_still_comes_back()
    {
        var mpv = new FakePlayer("mpv:1", delay: 20);
        _scanner.Running.Add(mpv);
        await Step(Streaming(236));
        await Step(Streaming(271)); // per-app routing switched on: +35 ms
        Assert.Equal(new[] { -236, -271 }, mpv.Writes);
        Assert.Equal(-271, _sync.Statuses.Single().DelayMs);
        await Step(Streaming(271) with { Enabled = false });
        Assert.Equal(new[] { -236, -271, 20 }, mpv.Writes);
    }

    [Fact]
    public async Task A_player_that_exits_is_forgotten_and_a_new_one_starts_fresh()
    {
        var first = new FakePlayer("mpv:1", delay: 10);
        _scanner.Running.Add(first);
        await Step(Streaming());
        _scanner.Running.Clear();
        await Step(Streaming());
        Assert.Equal(0, _sync.TrackedCount);
        Assert.Empty(_sync.Statuses);

        var second = new FakePlayer("mpv:9", delay: 0);
        _scanner.Running.Add(second);
        await Step(Streaming());
        await Step(Streaming() with { Enabled = false });
        Assert.Equal(new[] { -236 }, first.Writes); // never restored: it is gone
        Assert.Equal(new[] { -236, 0 }, second.Writes);
    }

    [Fact]
    public async Task A_hand_made_change_is_left_alone_until_the_latency_changes()
    {
        var mpv = new FakePlayer("mpv:1", delay: 0);
        _scanner.Running.Add(mpv);
        await Step(Streaming(236));
        mpv.Delay = -250; // the user fine-tunes in mpv (Ctrl+-)
        await Step(Streaming(236));
        await Step(Streaming(236));
        Assert.Equal(new[] { -236 }, mpv.Writes);
        Assert.Equal(new PlayerStatus(PlayerKind.Mpv, "mpv", PlayerState.UserChanged, -250), _sync.Statuses.Single());
        Assert.Equal("mpv：已被手动改为 -250 ms", PlayerText.Line(_sync.Statuses[0]));

        await Step(Streaming(271)); // a new target wins again
        Assert.Equal(new[] { -236, -271 }, mpv.Writes);
        Assert.Equal(PlayerState.Applied, _sync.Statuses.Single().State);
        await Step(Streaming(271) with { Enabled = false });
        Assert.Equal(0, mpv.Delay);
    }

    [Fact]
    public async Task Vlc_opening_the_next_item_gets_the_delay_again()
    {
        var vlc = new FakePlayer("vlc:2", PlayerKind.Vlc, delay: 0, item: "4");
        _scanner.Running.Add(vlc);
        await Step(Streaming());

        // next item: VLC starts it at its default delay
        vlc.Delay = 0;
        vlc.Item = "5";
        await Step(Streaming());
        Assert.Equal(new[] { -236, -236 }, vlc.Writes);

        // no item id, but back at its own value: the same
        vlc.Delay = 0;
        vlc.Item = null;
        await Step(Streaming());
        Assert.Equal(new[] { -236, -236, -236 }, vlc.Writes);

        // new item whose default differs from the value we found first
        vlc.Delay = 100;
        vlc.Item = "6";
        await Step(Streaming());
        Assert.Equal(-236, vlc.Delay);
        Assert.Equal(PlayerState.Applied, _sync.Statuses.Single().State);
    }

    [Fact]
    public async Task Nothing_open_waits_and_applies_once_playing()
    {
        var vlc = new FakePlayer("vlc:2", PlayerKind.Vlc, delay: null);
        _scanner.Running.Add(vlc);
        await Step(Streaming());
        Assert.Empty(vlc.Writes);
        Assert.Equal(PlayerState.Waiting, _sync.Statuses.Single().State);
        Assert.Equal(0, _sync.TrackedCount);
        Assert.Equal("VLC：开始播放后自动调整", PlayerText.Line(_sync.Statuses[0]));

        vlc.Delay = 30;
        vlc.Item = "1";
        await Step(Streaming());
        Assert.Equal(new[] { -236 }, vlc.Writes);
        await Step(Streaming() with { Enabled = false });
        Assert.Equal(new[] { -236, 30 }, vlc.Writes);
    }

    [Fact]
    public async Task Problems_and_manual_players_show_up_without_being_touched()
    {
        var vlc = new FakePlayer("vlc:2", PlayerKind.Vlc) { Refuse = PlayerProblem.LoginFailed };
        var broken = new FakePlayer("mpv:3") { Broken = true };
        _scanner.Running.AddRange([vlc, broken]);
        _scanner.Notes.Add(new PlayerNote(PlayerKind.Mpv, "mpv", PlayerProblem.NoInterface));
        _scanner.Notes.Add(new PlayerNote(PlayerKind.PotPlayer, "PotPlayer", PlayerProblem.Manual));

        await Step(Streaming(236));
        Assert.Empty(vlc.Writes);
        Assert.Equal(0, _sync.TrackedCount);
        Assert.Equal(new[]
        {
            new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.LoginFailed),
            new PlayerStatus(PlayerKind.Mpv, "mpv", PlayerState.Failed),
            new PlayerStatus(PlayerKind.Mpv, "mpv", PlayerState.NoInterface),
            new PlayerStatus(PlayerKind.PotPlayer, "PotPlayer", PlayerState.Manual, -236),
        }, _sync.Statuses);
        Assert.Equal(new[]
        {
            "VLC：密码错误，请在设置里重新输入",
            "mpv：调整失败",
            "mpv：未开启 IPC（见「设置」）",
            "PotPlayer：请手动设为 -236 ms",
        }, _sync.Statuses.Select(PlayerText.Line));
    }

    [Fact]
    public async Task Already_at_the_target_is_not_rewritten_but_still_restored()
    {
        var mpv = new FakePlayer("mpv:1", delay: -236);
        _scanner.Running.Add(mpv);
        await Step(Streaming(236));
        Assert.Empty(mpv.Writes);
        Assert.Equal(1, _sync.TrackedCount);
        await Step(Streaming(236) with { Enabled = false });
        Assert.Equal(new[] { -236 }, mpv.Writes); // its own value, which happened to be the same
    }

    [Fact]
    public async Task Shutdown_restores_every_player()
    {
        var mpv = new FakePlayer("mpv:1", delay: 0);
        var vlc = new FakePlayer("vlc:2", PlayerKind.Vlc, delay: 0, item: "1");
        _scanner.Running.AddRange([mpv, vlc]);
        await Step(Streaming());
        _sync.Shutdown(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { -236, 0 }, mpv.Writes);
        Assert.Equal(new[] { -236, 0 }, vlc.Writes);
        Assert.Equal(0, _sync.TrackedCount);
    }

    [Fact]
    public async Task Changed_is_raised_only_when_something_changed()
    {
        int changes = 0;
        _sync.Changed += () => changes++;
        _scanner.Running.Add(new FakePlayer("mpv:1"));
        await Step(Streaming());
        await Step(Streaming());
        Assert.Equal(1, changes);
        await Step(Streaming() with { Enabled = false });
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Home_page_hint_follows_the_setting_and_the_stream()
    {
        Assert.Equal("本地播放器：把音频延迟设为 -236 ms；网页视频：用浏览器插件自动对齐。",
            PlayerText.MovieHint(enabled: false, streaming: true, [], 236));
        Assert.Equal("推流时自动调整 mpv、VLC 的音频延迟。\n其他播放器：把音频延迟设为 -236 ms；网页视频：用浏览器插件自动对齐。",
            PlayerText.MovieHint(enabled: true, streaming: false, [], 236));
        Assert.Equal("没有发现开着的 mpv、VLC。\n其他播放器：把音频延迟设为 -236 ms；网页视频：用浏览器插件自动对齐。",
            PlayerText.MovieHint(enabled: true, streaming: true, [], 236));
        var vlcOff = new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.NoInterface);
        Assert.Equal("mpv：已设为 -236 ms\nVLC：未开启网页接口（见「设置」）\n其他播放器：把音频延迟设为 -236 ms；网页视频：用浏览器插件自动对齐。",
            PlayerText.MovieHint(true, true, [new(PlayerKind.Mpv, "mpv", PlayerState.Applied, -236), vlcOff, vlcOff], 236));
    }

    [Fact]
    public void Player_executables_are_recognised()
    {
        Assert.True(PlayerProcesses.TryIdentify("mpv", out var kind, out var name));
        Assert.Equal((PlayerKind.Mpv, "mpv"), (kind, name));
        Assert.True(PlayerProcesses.TryIdentify("mpvnet", out kind, out _));
        Assert.Equal(PlayerKind.Mpv, kind);
        Assert.True(PlayerProcesses.TryIdentify("vlc", out kind, out _));
        Assert.Equal(PlayerKind.Vlc, kind);
        Assert.True(PlayerProcesses.TryIdentify("potplayermini64", out kind, out _));
        Assert.Equal(PlayerKind.PotPlayer, kind);
        Assert.True(PlayerProcesses.TryIdentify("mpc-hc64", out kind, out _));
        Assert.Equal(PlayerKind.MpcHc, kind);
        Assert.True(PlayerProcesses.TryIdentify("mpc-be64", out kind, out _));
        Assert.Equal(PlayerKind.MpcBe, kind);
        Assert.False(PlayerProcesses.TryIdentify("mpv.com", out _, out _)); // the console wrapper; mpv.exe is the player
        Assert.False(PlayerProcesses.TryIdentify("chrome", out _, out _));
    }

    [Fact]
    public void Movie_player_sync_is_on_by_default_and_saved()
    {
        Assert.True(new AppConfig().MoviePlayerSync);
        var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { MoviePlayerSync = false }))!;
        Assert.False(back.MoviePlayerSync);
        Assert.True(JsonSerializer.Deserialize<AppConfig>("{}")!.MoviePlayerSync); // configs from before the setting
    }
}
