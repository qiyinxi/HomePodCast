using System.Collections.Concurrent;
using System.Text;
using HomePodCast.Players;

namespace HomePodCast.Tests;

/// <summary>
/// The VLC web-interface password: the one typed in 设置 is kept only as a DPAPI blob in config.json; VLC's own
/// password comes first and the typed one is the fallback; and no password ever reaches the log, the `players`
/// output, a status text or an exception. Every password here is random, so a hit can only be a leak.
/// </summary>
public class VlcPasswordTests
{
    private static string Secret(string tag) => $"{tag}-{Guid.NewGuid():N}";

    /// <summary>A VLC endpoint for the fake (it listens in this process, so the real port-owner check passes).</summary>
    private static VlcEndpoint Endpoint(FakeVlc vlc, string? own, Func<string?>? typed = null) =>
        new(Environment.ProcessId, vlc.EndPoint, own, typed);

    private static PlayerSyncInput Streaming(int videoDelayMs = 236) => new(true, StreamState.Streaming, videoDelayMs);

    // ---------------------------------------------------------------- DPAPI and config.json

    [Fact]
    public void Typed_password_round_trips_through_config_json_and_is_never_stored_in_clear()
    {
        var ascii = Secret("typed");
        var password = ascii + " 密码 ü";
        var dir = Path.Combine(Path.GetTempPath(), "hpc-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "config.json");
        try
        {
            new AppConfig { VlcPasswordProtected = VlcPassword.Protect(password) }.Save(path);
            var json = File.ReadAllText(path);
            Assert.Contains("\"VlcPasswordProtected\"", json);
            Assert.DoesNotContain(ascii, json);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), json);

            var back = AppConfig.Load(path);
            Assert.Equal(password, VlcPassword.Unprotect(back.VlcPasswordProtected));
            Assert.Null(AppConfig.Load(Path.Combine(dir, "missing.json")).VlcPasswordProtected); // none by default
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Only_this_app_can_read_the_blob_back_and_a_damaged_one_reads_as_none()
    {
        var password = Secret("x");
        var stored = VlcPassword.Protect(password);
        var blob = Convert.FromBase64String(stored);
        Assert.Equal(-1, blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)));
        Assert.NotEqual(stored, VlcPassword.Protect(password)); // DPAPI salts every blob

        Assert.Null(Dpapi.Unprotect(blob, "another app"u8)); // other entropy
        blob[^1] ^= 0xFF;
        Assert.Null(VlcPassword.Unprotect(Convert.ToBase64String(blob)));
        Assert.Null(VlcPassword.Unprotect("not base64!"));
        Assert.Null(VlcPassword.Unprotect(null));
        Assert.Null(VlcPassword.Unprotect(""));
        Assert.Throws<ArgumentException>(() => VlcPassword.Protect(""));
    }

    [Fact]
    public void The_reader_follows_the_config_as_it_changes()
    {
        var cfg = new AppConfig();
        var read = VlcPassword.From(() => cfg);
        Assert.Null(read());
        var password = Secret("typed");
        cfg.VlcPasswordProtected = VlcPassword.Protect(password);
        Assert.Equal(password, read());
        cfg.VlcPasswordProtected = null;
        Assert.Null(read());
    }

    // ---------------------------------------------------------------- which password is used

    [Fact]
    public async Task Vlc_settings_password_comes_first_and_the_typed_one_is_not_even_decrypted()
    {
        var own = Secret("own");
        using var vlc = new FakeVlc(own, delaySeconds: 0.02);
        int decrypted = 0;
        var e = Endpoint(vlc, own, () => { decrypted++; return Secret("typed"); });

        Assert.Equal(new PlayerReading(20, "3"), await e.ReadAsync(CancellationToken.None));
        await e.WriteAsync(-236, CancellationToken.None);
        Assert.Equal(-0.236, vlc.Delay, 9);
        Assert.Equal(PasswordSource.Player, e.PasswordInUse);
        Assert.Equal(0, decrypted);
        Assert.All(vlc.Requests, r => Assert.Equal(FakeVlc.AuthorizationFor(own), r.Authorization));
    }

    [Fact]
    public async Task A_refused_vlc_password_falls_back_to_the_typed_one()
    {
        var typed = Secret("typed");
        using var vlc = new FakeVlc(typed, delaySeconds: 0.02); // the password in vlcrc is out of date
        var e = Endpoint(vlc, Secret("stale"), () => typed);

        Assert.Equal(20, (await e.ReadAsync(CancellationToken.None)).DelayMs);
        Assert.Equal(PasswordSource.Manual, e.PasswordInUse);
        await e.WriteAsync(-236, CancellationToken.None);
        Assert.Equal(-0.236, vlc.Delay, 9);
        var auth = vlc.Requests.Select(r => r.Authorization).ToArray();
        Assert.Equal(3, auth.Length); // refused once, then only the typed password for this scan
        Assert.NotEqual(FakeVlc.AuthorizationFor(typed), auth[0]);
        Assert.All(auth[1..], a => Assert.Equal(FakeVlc.AuthorizationFor(typed), a));

        // The next scan tries VLC's own password first again (it may have been fixed in VLC meanwhile).
        var next = Endpoint(vlc, Secret("stale"), () => typed);
        await next.ReadAsync(CancellationToken.None);
        Assert.Equal(5, vlc.Requests.Count);
        Assert.Equal(PasswordSource.Manual, next.PasswordInUse);
    }

    [Fact]
    public async Task Without_a_password_in_vlc_settings_the_typed_one_is_used()
    {
        var typed = Secret("typed");
        using var vlc = new FakeVlc(typed);
        var e = Endpoint(vlc, null, () => typed);
        await e.ReadAsync(CancellationToken.None);
        Assert.Equal(PasswordSource.Manual, e.PasswordInUse);
        Assert.Equal(FakeVlc.AuthorizationFor(typed), Assert.Single(vlc.Requests).Authorization);
    }

    [Fact]
    public async Task Both_refused_is_a_wrong_password_and_none_at_all_sends_nothing()
    {
        using var vlc = new FakeVlc(Secret("right"));
        var both = Endpoint(vlc, Secret("own"), () => Secret("typed"));
        var ex = await Assert.ThrowsAsync<PlayerAccessException>(() => both.ReadAsync(CancellationToken.None));
        Assert.Equal(PlayerProblem.LoginFailed, ex.Problem);
        Assert.Null(both.PasswordInUse);
        Assert.Equal(2, vlc.Requests.Count);

        var none = Endpoint(vlc, null, () => null);
        Assert.Equal(PlayerProblem.NoPassword, (await Assert.ThrowsAsync<PlayerAccessException>(() => none.ReadAsync(CancellationToken.None))).Problem);
        Assert.Equal(2, vlc.Requests.Count); // nothing sent

        using var unset = new FakeVlc(""); // VLC itself has no password: 403 to everything
        var e = Endpoint(unset, Secret("own"), () => Secret("typed"));
        Assert.Equal(PlayerProblem.NoPassword, (await Assert.ThrowsAsync<PlayerAccessException>(() => e.ReadAsync(CancellationToken.None))).Problem);
        Assert.Equal(2, unset.Requests.Count);
    }

    [Fact]
    public async Task Settings_line_says_which_password_is_in_use_without_showing_it()
    {
        var own = Secret("own");
        var typed = Secret("typed");
        using var takesOwn = new FakeVlc(own);
        using var takesTyped = new FakeVlc(typed);

        async Task<(string Text, bool Problem)> LineFor(IPlayerEndpoint endpoint)
        {
            var sync = new PlayerSync(new FixedScanner(endpoint));
            await sync.StepAsync(Streaming(), CancellationToken.None);
            return PlayerText.VlcPasswordStatus(sync.Statuses, manualSaved: true);
        }

        Assert.Equal(("使用 VLC 设置里的密码", false), await LineFor(Endpoint(takesOwn, own, () => typed)));
        Assert.Equal(("使用手动输入的密码", false), await LineFor(Endpoint(takesTyped, own, () => typed)));
        Assert.Equal(("使用手动输入的密码", false), await LineFor(Endpoint(takesTyped, null, () => typed)));
        Assert.Equal(("密码错误，请在设置里重新输入", true), await LineFor(Endpoint(takesTyped, own, () => Secret("typo"))));
        Assert.Equal(("没有找到密码，请在这里输入", true), await LineFor(Endpoint(takesOwn, null, () => null)));

        // Not adjusting VLC right now: whether one is saved.
        Assert.Equal(("已保存手动输入的密码（加密保存，只有当前 Windows 用户能解密）。", false), PlayerText.VlcPasswordStatus([], true));
        Assert.Equal(("先用 VLC 设置里的密码；读不到或不对时用这里输入的密码。密码加密保存在本机。", false), PlayerText.VlcPasswordStatus([], false));
        // The home page points to 设置 when VLC refuses.
        Assert.Equal("VLC：密码错误，请在设置里重新输入", PlayerText.Line(new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.LoginFailed)));
        Assert.Equal("VLC：没有找到网页接口密码（见「设置」）", PlayerText.Line(new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.NoPassword)));
    }

    [Fact]
    public async Task A_delay_changed_by_hand_in_vlc_still_wins_with_the_typed_password()
    {
        var typed = Secret("typed");
        using var vlc = new FakeVlc(typed, delaySeconds: 0.05);
        var sync = new PlayerSync(new FixedScanner(Endpoint(vlc, Secret("stale"), () => typed)));

        await sync.StepAsync(Streaming(236), CancellationToken.None);
        Assert.Equal(-0.236, vlc.Delay, 9);
        Assert.Equal(new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.Applied, -236, PasswordSource.Manual), sync.Statuses.Single());

        vlc.Delay = -0.25; // the user fine-tunes in VLC
        await sync.StepAsync(Streaming(236), CancellationToken.None);
        await sync.StepAsync(Streaming(236), CancellationToken.None);
        Assert.Equal(-0.25, vlc.Delay, 9);
        Assert.Equal(new PlayerStatus(PlayerKind.Vlc, "VLC", PlayerState.UserChanged, -250, PasswordSource.Manual), sync.Statuses.Single());

        await sync.StepAsync(Streaming(236) with { Enabled = false }, CancellationToken.None);
        Assert.Equal(0.05, vlc.Delay, 9); // its own value comes back
    }

    // ---------------------------------------------------------------- nothing leaks

    [Fact]
    public async Task No_password_reaches_the_log_the_cli_a_status_text_or_an_exception()
    {
        var own = Secret("own-pw");     // stale password from VLC's settings
        var typed = Secret("typed-pw"); // the one VLC takes, typed in 设置
        var secrets = new[] { own, typed }
            .SelectMany(p => new[] { p, Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + p)), Convert.ToBase64String(Encoding.UTF8.GetBytes(p)) })
            .ToArray();
        var seen = new ConcurrentQueue<string>();
        void Capture(string line) => seen.Enqueue(line);

        using var vlc = new FakeVlc(typed, delaySeconds: 0.02);
        using var refuses = new FakeVlc(Secret("unknown"));
        using var unset = new FakeVlc("");
        Log.Written += Capture;
        try
        {
            // `players --apply`: list, apply, restore; with the fallback working, with every password refused,
            // and with a VLC that has none.
            foreach (var fake in new[] { vlc, refuses, unset })
                PlayersCommand.Run(new FixedScanner(Endpoint(fake, own, () => typed)), 236, TimeSpan.Zero, TimeSpan.Zero);

            // The app's sync with its log lines (applied, changed by hand, restored) and every text it shows.
            var sync = new PlayerSync(new FixedScanner(Endpoint(vlc, own, () => typed), Endpoint(refuses, own, () => typed)));
            await sync.StepAsync(Streaming(), CancellationToken.None);
            vlc.Delay = -0.3;
            await sync.StepAsync(Streaming(), CancellationToken.None);
            var statuses = sync.Statuses;
            await sync.StepAsync(Streaming() with { Enabled = false }, CancellationToken.None);
            foreach (var s in statuses)
            {
                seen.Enqueue(PlayerText.Line(s));
                seen.Enqueue(s.ToString());
            }
            seen.Enqueue(PlayerText.MovieHint(true, true, statuses, 236));
            seen.Enqueue(PlayerText.VlcPasswordStatus(statuses, true).Text);
            Assert.Contains(statuses, s => s.Password == PasswordSource.Manual);
            Assert.Contains(statuses, s => s.State == PlayerState.LoginFailed);

            // Exceptions, as the log and the CLI would print them.
            seen.Enqueue((await Record.ExceptionAsync(() => Endpoint(refuses, own, () => typed).ReadAsync(CancellationToken.None)))!.ToString());
            seen.Enqueue((await Record.ExceptionAsync(() => Endpoint(unset, own, () => typed).WriteAsync(-236, CancellationToken.None)))!.ToString());
            seen.Enqueue((await Record.ExceptionAsync(() =>
                new VlcEndpoint(Environment.ProcessId + 1, vlc.EndPoint, own, () => typed).ReadAsync(CancellationToken.None)))!.ToString());

            // Objects that hold a password, printed.
            seen.Enqueue(new VlcSettings(own, 8080).ToString());
            seen.Enqueue($"{VlcSettings.FromVlcrc($"http-password={own}\n").WithArgs(["vlc.exe", "--http-password=" + typed])}");
            seen.Enqueue(Endpoint(vlc, own, () => typed).ToString()!);
        }
        finally
        {
            Log.Written -= Capture;
        }

        Assert.Contains(seen, l => l.Contains("(password entered in HomePodCast)")); // the CLI ran and said which one
        Assert.Contains(seen, l => l.Contains("VlcSettings { Password = set, Port = 8080 }"));
        foreach (var line in seen)
            foreach (var secret in secrets)
                Assert.False(line.Contains(secret, StringComparison.Ordinal), $"a password leaked into: {line}");
    }
}
