using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomePodCast.Tests;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// One run of the app: <c>idle</c> (a fresh config, no speaker), <c>retrying</c> (a speaker address nobody answers, with
/// automatic connect: 「找不到音箱…，N 秒后重试」), <c>streaming</c> (connected to the tests' fake HomePod on 127.0.0.1:7000)
/// <c>runtime</c> (streaming on a private .NET that an update then replaces: 立即重启, Walker.Runtime.cs), or
/// <c>group-outputs</c> (offline saved output settings, without starting audio), in a UI language, at a display scale,
/// in a theme. <see cref="Narrow"/> walks the pages again at the window's minimum width;
/// <see cref="DpiChange"/> changes the display scale to 100 % and back while the app runs (as a move to another monitor).
/// </summary>
internal sealed record Scenario(string Kind, string Language, int Scale, string Theme = "auto", bool Narrow = false, bool DpiChange = false,
    bool RequiredScale = false)
{
    public string Id => $"{Kind}-{Language}-{Scale}" + (Theme == "auto" ? "" : "-" + Theme);
}

/// <summary>A step that can't go on (a window that never appeared, the app gone): the scenario stops there.</summary>
internal sealed class StepFailed(string message) : Exception(message);

/// <summary>Drives one scenario: starts the app, walks it through UI Automation, checks, takes screenshots, quits it.</summary>
internal sealed partial class Walker
{
    /// <summary>
    /// HOMEPODCAST_PROFILE for the app: null on a CI runner (the default config folder, as a fresh install: the
    /// screenshots show what a user sees), "gui-smoke" anywhere else (its own config folder and single-instance name).
    /// </summary>
    public static string? Profile { get; set; } = "gui-smoke";

    /// <summary>The extracted viewing window module (the build's HomePodCast-video-window zip); null: not checked.</summary>
    public static string? VideoWindowDir { get; set; }

    private const string VideoWindowProcess = "HomePodCast.VideoWindow";

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Profile == null ? "HomePodCast" : "HomePodCast-" + Profile);

    private static string LogPath => Path.Combine(ConfigDir, "homepodcast.log");

    private static readonly string[] Pages = ["首页", "混音器", "麦克风", "均衡", "音箱信息", "多音箱（实验性）", "音画同步", "诊断", "使用配置", "设置", "关于"];

    private readonly Scenario _s;
    private readonly Texts _t;
    private readonly Report _report;
    private readonly string _exe;
    private readonly string _outRoot;
    private readonly string _dir;
    private readonly List<string> _userData = [];
    private readonly HashSet<string> _promptsShot = [];
    private Process? _app;
    private FakeAirPlayReceiver? _fake;
    private int _shots;

    public Walker(Scenario scenario, string exe, string outRoot, Report report)
    {
        _s = scenario;
        _t = new Texts(scenario.Language);
        _report = report;
        _exe = exe;
        _outRoot = outRoot;
        _dir = Path.Combine(outRoot, scenario.Id);
        Directory.CreateDirectory(_dir);
    }

    private string T(string zh) => _t.T(zh);
    private string Where(string? step = null) => step == null ? _s.Id : $"{_s.Id} / {step}";

    public void Run()
    {
        Uia.Noted = text => _report.Note(Where("ui automation"), text); // this scenario's thread
        try
        {
            switch (_s.Kind)
            {
                case "idle": RunIdle(); break;
                case "retrying": RunRetrying(); break;
                case "streaming": RunStreaming(); break;
                case "compatibility": RunCompatibility(); break;
                case "runtime": RunRuntime(); break; // Walker.Runtime.cs
                case "group-outputs": RunGroupOutputs(); break; // Walker.GroupOutputs.cs
                default: throw new ArgumentException($"unknown scenario kind {_s.Kind}");
            }
        }
        catch (StepFailed ex)
        {
            _report.Fail(Where(), "stopped: " + ex.Message);
            RecordProcessState();
        }
        catch (Exception ex)
        {
            _report.Fail(Where(), $"stopped by {ex.GetType().Name}: {ex.Message}");
            File.WriteAllText(Path.Combine(_dir, "exception.txt"), ex.ToString());
            RecordProcessState();
        }
        finally
        {
            StopApp();
            SaveLog();
            _fake?.Dispose();
            Uia.Noted = null;
        }
    }

    private void RecordProcessState()
    {
        if (_app == null) return;
        try
        {
            if (_app.HasExited) _report.Note(Where("process"), $"exited: 0x{_app.ExitCode:X8}");
            else _report.Note(Where("process"), "windows: " + string.Join("; ", Native.TopLevelWindows(_app.Id, visibleOnly: false)
                .Select(w => $"{w:X} {Native.ClassName(w)} visible={Native.IsWindowVisible(w)} title=「{Native.WindowText(w)}」")));
        }
        catch (Exception ex) { _report.Note(Where("process"), "diagnostics unavailable: " + ex.Message); }
    }

    /// <summary>The app is gone for good (also when a scenario overran its time).</summary>
    public void Kill()
    {
        try { if (_app is { HasExited: false }) _app.Kill(); } catch (Exception) { }
    }

    // ---------------------------------------------------------------- scenarios

    private Dictionary<string, object?> BaseConfig() => new()
    {
        ["Language"] = _s.Language,
        ["Theme"] = _s.Theme,
        ["Scene"] = "Recommended", // as a fresh install
        ["LocalApiPort"] = 0,      // a random port: never the real instance's
        ["AutoConnect"] = false,
        ["VideoWindowModuleDirectory"] = VideoWindowDir, // 打开 starts it without asking for the folder
    };

    private void RunIdle()
    {
        Start(BaseConfig());
        WalkEverything(connected: false);
        if (_s.DpiChange) DpiRoundTrip();
        Quit();
        CheckLog(idle: true);
    }

    private void RunCompatibility()
    {
        const string id = "02:43:4F:4D:50:54";
        var config = BaseConfig();
        config["DeviceId"] = id; config["DeviceName"] = "CI compatibility receiver"; config["Host"] = "127.0.0.1";
        config["ReceiverSettings"] = new Dictionary<string, object?>
        {
            [id.Replace(":", "")] = new Dictionary<string, object?>
            {
                ["Name"] = "CI compatibility receiver", ["Host"] = "127.0.0.1", ["Port"] = 7001,
                ["Model"] = "AppleTV3,2", ["Txt"] = new Dictionary<string, string> { ["srcvers"] = "220.68" },
                ["HasAirPlay"] = true, ["RaopPort"] = 7001, ["CompatibilityLatencyMs"] = 2000,
                ["CompatibilityNoticeShown"] = true,
            },
        };
        _userData.Add("CI compatibility receiver");
        Start(config); // AutoConnect stays false: these checks render controls without starting a session.
        foreach (var page in Pages)
        {
            Navigate(page);
            var tree = Uia.Snapshot(MainWindow());
            CheckNavigation(tree);
            if (page == "首页")
            {
                CheckHome(tree, false);
                Expect(tree.Descendants().Any(n => n.Shown && n.Name == T("兼容模式延迟（实验性）")), page,
                    "compatibility latency selector is missing");
                Expect(Has(tree, "2000 ms"), page, "compatibility default is not 2000 ms");
            }
            if (page == "音箱信息") tree = CheckSpeakers(false, compatibility: true);
            if (page == "关于") tree = CheckAbout();
            if (page is "麦克风" or "均衡") CheckMicrophoneAndEqualizer(tree, page);
            if (page == "设置") CheckSettings();
            if (page == "音画同步") CheckAvSync(tree);
            if (page == "诊断") CheckDiagnostics(tree);
            Layout(tree, page); ShootPage(page, "");
            if (page == "首页") Flyout(false, compatibility: true);
        }
        WalkNarrow();
        Quit(); CheckLog(idle: true);
    }

    private void RunRetrying()
    {
        var config = BaseConfig();
        config["AutoConnect"] = true;
        config["DeviceId"] = "02:53:4D:4F:4B:45";
        config["DeviceName"] = "Smoke Test Speaker";
        config["Host"] = "127.0.0.2"; // nobody listens on 7000 there, and no speaker has this id
        _userData.Add("Smoke Test Speaker");
        Start(config);

        string notFound = T("找不到音箱：请确认它已通电、和电脑在同一网络");
        var retry = _t.Pattern("{0}，{1:F0} 秒后重试");
        bool Retrying(string status) => status.StartsWith(notFound, StringComparison.Ordinal) && retry.IsMatch(status);

        // 首页: while it waits to retry, the button offers 连接 (2026-10-12: it said 断开).
        var (status, button) = WaitForStatus(MainWindow, "首页", Retrying, 90_000);
        Expect(button == T("连接"), "首页", $"the button reads 「{button}」 while 「{status}」 (expected 「{T("连接")}」)");
        Shoot(MainWindow(), "home-retrying", $"首页 while retrying: {status}");
        Layout(Uia.Snapshot(MainWindow()), "首页 (retrying)");

        // The tray flyout, the same.
        var flyout = OpenFlyout();
        var (flyoutStatus, flyoutButton) = WaitForStatus(() => FindFlyout(), "flyout", Retrying, 90_000);
        Expect(flyoutButton == T("连接"), "flyout", $"the button reads 「{flyoutButton}」 while 「{flyoutStatus}」 (expected 「{T("连接")}」)");
        Shoot(flyout, "flyout-retrying", $"tray flyout while retrying: {flyoutStatus}");
        Layout(Uia.Snapshot(flyout), "flyout (retrying)");
        CloseFlyout(flyout);

        // A click there connects at once instead of waiting for the retry. Clicked right after a retry wait began (one of
        // 4 s or more by now), so that the click can't land in the next attempt's 正在重连 (where the button disconnects).
        var reconnecting = _t.Pattern("正在重连（第 {0} 次）…");
        WaitForStatus(MainWindow, "首页", s => reconnecting.IsMatch(s), 90_000);
        WaitForStatus(MainWindow, "首页", Retrying, 30_000);
        var connect = Find(Uia.Snapshot(MainWindow()), n => n.Type == "Button" && n.Says(T("连接")) && n.Shown)
                      ?? throw new StepFailed("首页 has no 连接 button");
        Uia.Invoke(connect);
        var (after, _) = WaitForStatus(MainWindow, "首页", s => s == T("正在连接…") || reconnecting.IsMatch(s), 6000);
        Expect(after == T("正在连接…"), "首页", $"after 连接 during the retry wait the status is 「{after}」 (expected 「{T("正在连接…")}」 at once)");
        if (after == T("正在连接…")) _report.Pass(Where("首页"), "连接 during the retry wait connects at once");

        Quit();
        CheckLog(idle: false);
    }

    private void RunStreaming()
    {
        try
        {
            // 85 ms in the speaker, as a real HomePod 2 reports it (the fake's 140 would raise 首页's latency floor to 155 ms).
            _fake = new FakeAirPlayReceiver(appleTv: false, port: 7000) { Name = "Smoke Test HomePod", ArrivalToRenderMs = 85 };
        }
        catch (SocketException ex)
        {
            throw new StepFailed($"the fake HomePod can't listen on 127.0.0.1:7000 ({ex.Message})");
        }
        var config = BaseConfig();
        config["AutoConnect"] = true;
        config["DeviceId"] = _fake.DeviceId;
        config["DeviceName"] = _fake.Name;
        config["Host"] = "127.0.0.1";
        config["Volume"] = 20.0;
        _userData.Add(_fake.Name);
        Start(config);

        var connected = _t.Pattern("已连接 · {0}");
        var (status, button) = WaitForStatus(MainWindow, "首页", s => connected.IsMatch(s), 60_000);
        Expect(button == T("断开"), "首页", $"the button reads 「{button}」 while 「{status}」 (expected 「{T("断开")}」)");
        _report.Pass(Where("首页"), status);
        WalkEverything(connected: true);
        Quit();
        CheckLog(idle: false);

        var requests = _fake.Requests.ToArray();
        Expect(requests.Contains("SETUP stream [encrypted]") && requests.Contains("RECORD [encrypted]"), "fake HomePod",
            $"the session was not set up: {string.Join(", ", requests.Distinct())}");
        Expect(requests.Contains("TEARDOWN [encrypted]"), "fake HomePod", "quitting sent no TEARDOWN");
        Expect(!_fake.Rtp.Audio.IsEmpty, "fake HomePod", "no audio packets arrived");
        foreach (var error in _fake.Errors) _report.Fail(Where("fake HomePod"), error);
    }

    /// <summary>Every page (scrolled through), the tray flyout and the dialogs; the narrow window if asked.</summary>
    private void WalkEverything(bool connected)
    {
        foreach (var page in Pages)
        {
            Navigate(page);
            var tree = Uia.Snapshot(MainWindow());
            if (page == "首页") CheckHome(tree, connected);
            if (page == "设置") CheckSettings();
            if (page == "音画同步") CheckAvSync(tree);
            if (page == "诊断") CheckDiagnostics(tree, connected);
            if (page == "音箱信息") tree = CheckSpeakers(connected);
            if (page == "关于") tree = CheckAbout();
            if (page is "麦克风" or "均衡") CheckMicrophoneAndEqualizer(tree, page);
            if (page == "多音箱（实验性）") tree = CheckCinema(tree, page);
            CheckNavigation(tree);
            Layout(tree, page);
            ShootPage(page, "");
            if (page == "首页") Flyout(connected);
        }

        Navigate("设置");
        Dialog("hotkeys", Button("快捷键…"), "全局快捷键", "关闭", CheckHotkeys);
        Navigate("首页");
        Uia.Invoke(Button("管理配置"));
        WaitUntil(() => TryPagePane(Uia.Snapshot(MainWindow()), "使用配置") != null, "profile management page", 8000);
        Layout(Uia.Snapshot(MainWindow()), "使用配置");
        CheckThemeSwitch();
        if (VideoWindowDir != null) ViewingWindow(connected);

        WalkNarrow();
    }

    private void CheckThemeSwitch()
    {
        Navigate("关于");
        for (int i = 0; i < 2; i++)
        {
            var tree = Uia.Snapshot(MainWindow());
            var toggle = Find(tree, n => n.Type == "Button" && (n.Name == T("切换到浅色") || n.Name == T("切换到深色")))
                ?? throw new StepFailed("no accessible theme switch");
            string before = toggle.Name;
            Uia.Invoke(toggle);
            WaitUntil(() => Find(Uia.Snapshot(MainWindow()), n => n.Type == "Button" &&
                (n.Name == T("切换到浅色") || n.Name == T("切换到深色")) && n.Name != before) != null, "theme switch", 5000);
            Shoot(MainWindow(), "theme-fade-" + i, "client theme transition");
            Thread.Sleep(300);
            Layout(Uia.Snapshot(MainWindow()), "theme switch");
            Shoot(MainWindow(), "theme-toggle-" + i, "client fade completed");
        }
    }

    private void WalkNarrow()
    {
        if (_s.Narrow)
        {
            var main = MainWindow();
            var r = Native.WindowRect(main);
            int width = 600 * _s.Scale / 100; // MainWindow.MinimumSize: Dp(600), compact navigation
            Native.SetWindowPos(main, IntPtr.Zero, 0, 0, width, r.Height, Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Thread.Sleep(800);
            _report.Pass(Where("narrow"), $"window {Native.WindowRect(main).Width}px wide");
            foreach (var page in Pages)
            {
                Navigate(page);
                var tree = Uia.Snapshot(MainWindow());
                CheckNavigation(tree, compact: true);
                if (page is "麦克风" or "均衡") CheckMicrophoneAndEqualizer(tree, page);
                Layout(tree, page + " (narrow)");
                ShootPage(page, "narrow");
            }
        }
    }

    /// <summary>
    /// The window moved to a monitor with another scale. 2026-10-15, 1.1.0-beta.5 between the maintainer's 150 % and 100 %
    /// monitors: 「未响应」 for seconds, half repainted (navigation items and rows drawn twice). The runner has one screen, so
    /// its scale goes to 100 % and back while the app runs, which sends the window the same WM_DPICHANGED. Each time: the
    /// window answers within Windows' 5 s (after that it shows as not responding), its controls are at the new DPI, the
    /// layout checks pass, and repainting the whole window changes nothing (no stale picture left from before).
    /// </summary>
    private void DpiRoundTrip()
    {
        Navigate("麦克风"); // the page in the maintainer's screenshot
        try
        {
            foreach (int scale in new[] { 100, _s.Scale })
            {
                string step = $"DPI {scale}%";
                var (ok, detail) = Display.SetScale(scale);
                if (!ok)
                {
                    _report.Warn(Where(step), $"the scale can't change on this runner: {detail}");
                    return;
                }
                var watch = Stopwatch.StartNew();
                var main = MainWindow();
                uint want = (uint)(96 * scale / 100);
                WaitUntil(() => Native.GetDpiForWindow(main) == want, $"the window at {want} dpi", 10_000, fail: false);
                if (Native.GetDpiForWindow(main) != want)
                {
                    _report.Warn(Where(step), $"the window stayed at {Native.GetDpiForWindow(main)} dpi: Windows did not move it to the new scale");
                    return;
                }
                // A message waits behind the DPI change's work on the UI thread.
                bool answered = Native.SendMessageTimeout(main, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0, 30_000, out _) != IntPtr.Zero;
                long answer = watch.ElapsedMilliseconds;
                Expect(answered, step, "the window did not answer within 30 s after the scale changed");
                // Laid out at the new DPI: the navigation items are 38 px high at 96 dpi.
                int itemHeight = (int)(38 * want / 96);
                WaitUntil(() => Find(Uia.Snapshot(main), n => n.Type == "TabItem" && n.Name == T("设置")) is { } item &&
                                Math.Abs(item.Rect.Height - itemHeight) <= 1, $"the navigation at {want} dpi ({itemHeight} px items)", 15_000);
                long laidOut = watch.ElapsedMilliseconds;
                Expect(answer < 5000, step, $"the window took {answer} ms to answer after the scale changed (Windows shows it as not responding after 5 s)");
                _report.Note(Where(step), $"{want} dpi: the window answered after {answer} ms and was laid out after {laidOut} ms");
                Thread.Sleep(800);
                var tree = Uia.Snapshot(main);
                CheckNavigation(tree);
                Layout(tree, $"麦克风 at {scale}%");
                CheckRepainted(main, step);
                Shoot(main, $"dpi-{scale}", $"麦克风 after the scale changed to {scale}%");
                Navigate("设置");
                Layout(Uia.Snapshot(main), $"设置 at {scale}%");
                Navigate("麦克风");
            }
        }
        finally
        {
            if (Display.Scale()?.Current != _s.Scale) Display.SetScale(_s.Scale); // the next scenarios run at this scale
        }
    }

    /// <summary>
    /// Nothing on the window is a leftover: repainted in full, it looks the same (a control drawn at its old place and not
    /// painted over since would differ).
    /// </summary>
    private void CheckRepainted(IntPtr hwnd, string step)
    {
        using var before = Native.Capture(hwnd);
        Native.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
        Thread.Sleep(400);
        using var after = Native.Capture(hwnd);
        if (before.Size != after.Size)
        {
            _report.Fail(Where(step), $"the window changed size while it was repainted ({before.Size} → {after.Size})");
            return;
        }
        int differ = 0, total = 0;
        for (int y = 0; y < before.Height; y += 2)
        for (int x = 0; x < before.Width; x += 2, total++)
            if (before.GetPixel(x, y) != after.GetPixel(x, y)) differ++;
        double share = 100.0 * differ / Math.Max(1, total);
        if (share > 1)
        {
            string name = step.Replace("%", "").Replace(' ', '-').ToLowerInvariant();
            before.Save(Path.Combine(_dir, $"stale-{name}-before.png"), ImageFormat.Png);
            after.Save(Path.Combine(_dir, $"stale-{name}-after.png"), ImageFormat.Png);
            _report.Fail(Where(step), $"{share:F1}% of the window changed when it was repainted: a stale picture was left after the scale change");
        }
        else
        {
            _report.Pass(Where(step), $"repainted in full, {share:F2}% of the window changed");
        }
    }

    // ---------------------------------------------------------------- page checks

    private void CheckMicrophoneAndEqualizer(Node tree, string name)
    {
        var page = PagePane(tree, name);
        if (name == "麦克风")
        {
            foreach (string text in new[] { "麦克风监听", "输入设备", "输出到", "本机输出设备", "麦克风监听已关闭。",
                         "每次启动 HomePodCast 时麦克风默认关闭。", "衰减（Room Size）", "高频阻尼（Damping）",
                         "干湿比（Wet/Dry）", "预延迟（Pre-delay）", "麦克风均衡" })
                Expect(Has(page, T(text)), name, $"missing microphone control or explanation: {T(text)}");
            Expect(!Has(page, T("HomePod 输出均衡")), name, "output EQ is still on the microphone page");
            var route = Find(page, n => n.Name == T("输出到") && n.Type == "List");
            Expect(route != null && Has(route, T("本机")) && Has(route, T("两者")), name, "output choices are missing");
            var hint = _t.Pattern("你的声音会和电脑声音一起从 HomePod 播出，比实际晚约 {0} ms。");
            Expect(page.Descendants().Any(n => n.Shown && hint.IsMatch(n.Name)), name, "HomePod destination explanation is missing");
        }
        else
        {
            foreach (string text in new[] { "HomePod 输出均衡", "预设", "作用于推送到 HomePod 的所有声音，包括电脑声音和麦克风。" })
                Expect(Has(page, T(text)), name, $"missing output EQ control or scope: {T(text)}");
            Expect(!Has(page, T("麦克风均衡")), name, "microphone EQ is on the output page");
        }
    }

    private void CheckNavigation(Node tree, bool compact = false)
    {
        var settings = Find(tree, n => n.Type == "TabItem" && n.Name == T("关于"));
        Expect(settings?.Parent != null, "navigation", "no settings navigation item");
        if (settings?.Parent is not { } rail) return;
        int dpi = (int)Native.GetDpiForWindow(MainWindow());
        int gap = 8 * dpi / 96;
        Expect(Math.Abs(rail.Rect.Bottom - settings.Rect.Bottom - gap) <= 2, "navigation",
            "About is not pinned to the bottom of the rail");
        if (compact)
            Expect(rail.Rect.Width <= 64 * dpi / 96 + 1, "navigation", "narrow window did not compact the navigation rail");
    }

    private void CheckHome(Node tree, bool connected)
    {
        var page = PagePane(tree, "首页");
        Expect(page.Descendants().Count(n => n.Type == "Text" && n.Hwnd != IntPtr.Zero
            && new[] { T("实际延迟"), T("缓冲"), T("音箱处理"), T("重传") }.Contains(n.Name)) == 4,
            "首页", "the four playback metric tiles are missing or duplicated");
        foreach (var caption in new[] { "首页", "推流状态" })
            Expect(page.Descendants().Any(n => n.Type == "Text" && n.Says(T(caption)) && n.Hwnd == IntPtr.Zero && n.HasSize),
                "首页", $"parent-painted 「{T(caption)}」 has no UI Automation text node/bounds, or created a window");
        var buttons = page.Descendants().Where(n => n.Type == "Button" && n.Shown).ToList();
        string expected = connected ? T("断开") : T("连接");
        Expect(buttons.Any(b => b.Says(expected)), "首页", $"no 「{expected}」 button (buttons: {string.Join(", ", buttons.Select(b => b.Name))})");
        foreach (var gone in new[] { "刚才卡了", "导出诊断" }) // moved: 设置 → 工具, and a hotkey
            Expect(buttons.All(b => !b.Says(T(gone))), "首页", $"「{T(gone)}」 is on 首页 again");
        Expect(buttons.Any(b => b.Says(T("HomePod 静音")) && b.Enabled), "首页",
            "the clickable HomePod mute icon is missing or disabled");
        // The output switch moved here from 设置. Only inspect it: switching it on would mute the runner's output.
        string onlyHomePodTitle = T("仅 HomePod 输出");
        var onlyHomePod = Find(page, n => n.Name == onlyHomePodTitle && n.Type == "CheckBox" && n.Hwnd != IntPtr.Zero);
        var onlyHomePodState = onlyHomePod == null ? null : Native.MsaaState(onlyHomePod.Hwnd);
        Expect(onlyHomePodState is { } os && (os & Native.STATE_SYSTEM_CHECKED) == 0, "首页",
            $"「{onlyHomePodTitle}」 is not off on a fresh config (state {onlyHomePodState?.ToString("X") ?? "not found"})");
        Expect(Find(page, n => n.Name == T("夜间模式") && n.Type == "CheckBox") != null, "首页",
            "the night-mode switch beside HomePod only is missing");
        if (!connected)
        {
            Expect(Has(page, T("未连接")), "首页", $"the status doesn't say 「{T("未连接")}」");
            return;
        }

        // The tiles show this connection's values (MetricTile: the caption is the name, the value the description).
        var deadline = DateTime.UtcNow.AddSeconds(8);
        foreach (var caption in new[] { "实际延迟", "缓冲", "音箱处理", "重传" })
        {
            string? value = null;
            do
            {
                var tile = Find(Uia.Snapshot(MainWindow()), n => n.Name == T(caption) && n.Hwnd != IntPtr.Zero && n.Shown);
                value = tile == null ? null : Native.MsaaDescription(tile.Hwnd);
                if (!string.IsNullOrEmpty(value) && value != "—") break;
                Thread.Sleep(250);
            } while (DateTime.UtcNow < deadline);
            Expect(!string.IsNullOrEmpty(value) && value != "—", "首页", $"the tile 「{T(caption)}」 shows 「{value ?? "(not found)"}」 while connected");
            if (!string.IsNullOrEmpty(value) && value != "—") _report.Pass(Where("首页"), $"{T(caption)} = {value}");
        }
    }

    private Node CheckSpeakers(bool connected, bool compatibility = false)
    {
        // 音箱信息 reads the speaker when the page is shown: wait for 读取中… to go.
        var tree = Uia.Snapshot(MainWindow());
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Has(tree, T("读取中…")) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            tree = Uia.Snapshot(MainWindow());
        }
        var page = PagePane(tree, "音箱信息");
        Expect(Has(page, T("手动添加 AirPlay 设备（实验性）")), "设置", "manual AirPlay receiver row missing"); // experimental since 1.1
        if (connected)
        {
            Expect(Has(page, _fake!.Name), "设置", $"音箱信息 doesn't name 「{_fake.Name}」");
            Expect(!Has(page, T("无法读取（音箱不在线？）")), "设置", "音箱信息 could not read the speaker");
        }
        else if (compatibility)
        {
            Expect(Has(page, "CI compatibility receiver"), "设置", "音箱信息 doesn't name the selected receiver");
            Expect(!Has(page, T("还没有选择音箱")), "设置", "音箱信息 lost the selected receiver");
            Expect(Has(page, T("连接方式（实验性）")), "设置", "connection override is missing"); // experimental since 1.1
            // The kind only in the card's 设备类型 line (named after its caption): until 2026-10-15 a note above the card
            // repeated it.
            string kind = T("AirPlay 1 · 兼容模式（实验性）"); // experimental since 1.1
            int kinds = page.Descendants().Count(n => n.Type == "Text" && n.Shown && (n.Name.Contains(kind, StringComparison.Ordinal)
                || n.Text.Contains(kind, StringComparison.Ordinal)));
            Expect(kinds == 1, "设置", $"the receiver's kind 「{kind}」 is shown {kinds} times, not once");
        }
        else
        {
            Expect(Has(page, T("还没有选择音箱")), "设置", $"音箱信息 doesn't say 「{T("还没有选择音箱")}」");
        }

        return tree;
    }

    private void CheckSettings()
    {
        var page = PagePane(Uia.Snapshot(MainWindow()), "设置");
        foreach (var moved in new[] { "导出诊断", "软件更新", "使用配置", "手动添加 AirPlay 设备（实验性）", "本地播放器", "影视场景自动调整播放器", "VLC 网页接口密码（自动读取失败时填写）" })
            Expect(!Has(page, T(moved)), "设置", $"moved feature still present: {T(moved)}");
        // The volume-key description still names the Home switch; match the control, not that help text.
        Expect(Find(page, n => n.Type == "CheckBox" && n.Name == T("仅 HomePod 输出")) == null, "设置",
            "the HomePod-only output switch is duplicated on Settings");
        // 夜间自动断开 is off on a fresh config. Not switched on here: inside its window on an idle runner it would end the
        // connection to the fake HomePod that the next steps check.
        var night = Find(page, n => n.Name == T("夜间自动断开（实验性）") && n.Type != "Text" && n.Hwnd != IntPtr.Zero);
        var nightState = night == null ? null : Native.MsaaState(night.Hwnd);
        Expect(nightState is { } ns && (ns & Native.STATE_SYSTEM_CHECKED) == 0, "设置",
            $"「{T("夜间自动断开（实验性）")}」 is not off on a fresh config (state {nightState?.ToString("X") ?? "not found"})");
    }

    private void CheckAvSync(Node tree)
    {
        var pane = PagePane(tree, "音画同步").Hwnd;
        string[] texts = [T("本地播放器"), T("影视场景自动调整播放器"), T("VLC 网页接口密码（自动读取失败时填写）"),
            T("同步观影窗口（实验性）"), "mpv", "VLC", "PotPlayer · MPC-HC · MPC-BE"];
        string[] actions = [T("选择模块…"), T("打开")];
        var seen = new HashSet<string>();
        var buttons = new HashSet<string>();
        Scroll(pane, Native.SB_TOP);
        try
        {
            for (int i = 0; i < 12; i++)
            {
                // The title scrolls out of view: keep the pane handle rather than locating it again by its title.
                var page = Find(Uia.Snapshot(MainWindow()), node => node.Hwnd == pane)
                    ?? throw new StepFailed("the A/V sync scroll pane disappeared");
                foreach (string text in texts) if (Has(page, text)) seen.Add(text);
                foreach (string action in actions)
                    if (Find(page, node => node.Shown && node.Type == "Button" && node.Says(action)) != null) buttons.Add(action);
                var info = Native.VerticalScroll(pane);
                if ((Native.Style(pane) & Native.WS_VSCROLL) == 0 || info is not { } position || position.nPos + position.nPage > position.nMax) break;
                Scroll(pane, Native.SB_PAGEDOWN);
                if (Native.VerticalScroll(pane) is { } after && after.nPos == position.nPos) break;
            }
        }
        finally { Scroll(pane, Native.SB_TOP); }
        foreach (string text in texts) Expect(seen.Contains(text), "音画同步", $"section or row was never shown: {text}");
        foreach (string action in actions) Expect(buttons.Contains(action), "音画同步", $"module action was never shown: {action}");
    }

    private Node CheckAbout()
    {
        var tree = Uia.Snapshot(MainWindow());
        var page = PagePane(tree, "关于");
        foreach (var moved in new[] { "日志", "诊断报告", "导出诊断", "打开日志文件夹" })
            Expect(!page.Descendants().Any(n => n.Says(T(moved))), "关于", $"moved diagnostic feature remains: {T(moved)}");
        Expect(Has(page, T("许可证")), "关于", "license missing");
        // 软件更新 is one row, as in Windows Update: 「检查更新」 sits in the row 「当前版本 X」, and an offered update would
        // put 更新 / 查看说明 / 跳过此版本 in its place there. Nothing is offered here (no check was made, and a release
        // build can't be shown a fake one): UpdateStatusRowTests checks the row with an update.
        // Only in the row: German 「Aktualisieren」 is also 音箱信息's 刷新.
        var versionPattern = _t.Pattern("当前版本 {0}");
        var updateRow = Find(page, n => n.Type == "Text" && n.Shown && versionPattern.IsMatch(n.Name))?.Parent;
        Expect(updateRow != null && Find(updateRow, n => n.Type == "Button" && n.Shown && n.Says(T("检查更新"))) != null,
            "设置", $"「{T("检查更新")}」 is not in the row 「{T("当前版本 {0}")}」");
        foreach (var offer in new[] { "更新", "查看说明", "跳过此版本" })
            Expect(updateRow == null || !updateRow.Descendants().Any(n => n.Type == "Button" && n.Shown && n.Says(T(offer))), "设置",
                $"「{T(offer)}」 is in the row 「{T("当前版本 {0}")}」 although no update was offered");
        Expect(!page.Descendants().Any(n => n.Type == "Button" && n.Shown && n.Says(T("跳过此版本"))), "设置",
            $"「{T("跳过此版本")}」 is shown although no update was offered");

        // Updates on a fresh config: no automatic checks; the channel follows the build (a prerelease: 测试版).
        var auto = Find(page, n => n.Name == T("自动检查更新") && n.Type != "Text" && n.Hwnd != IntPtr.Zero);
        var autoState = auto == null ? null : Native.MsaaState(auto.Hwnd);
        Expect(autoState is { } st && (st & Native.STATE_SYSTEM_CHECKED) == 0, "设置",
            $"「{T("自动检查更新")}」 is not off on a fresh config (state {autoState?.ToString("X") ?? "not found"})");
        var channel = Find(page, n => n.Name == T("更新渠道") && n.Type != "Text" && n.Hwnd != IntPtr.Zero);
        string? channelValue = channel == null ? null : Native.MsaaValue(channel.Hwnd);
        string version = (FileVersionInfo.GetVersionInfo(_exe).ProductVersion ?? "").Split('+')[0];
        if (version.Length == 0)
        {
            _report.Warn(Where("设置"), "the exe has no product version: the update channel's default is not checked");
            return tree;
        }
        string expectedChannel = version.Contains('-') ? T("测试版") : T("正式版");
        Expect(channelValue == expectedChannel, "设置", $"「{T("更新渠道")}」 is 「{channelValue ?? "not found"}」 in build {version} (expected 「{expectedChannel}」)");
        return tree;
    }

    private void CheckHotkeys(Node tree, string where)
    {
        var caption = Find(tree, n => n.Type == "Text" && n.Name == T("标记刚才的卡顿"));
        Expect(caption != null, where, $"「{T("标记刚才的卡顿")}」 is not listed");
        var row = caption?.Parent;
        Expect(row != null && Has(row, T("未设置")), where, $"「{T("标记刚才的卡顿")}」 is not 「{T("未设置")}」 by default");
    }

    /// <summary>The unified page: source switching, shared per-device rows, no file playback or measurement on opening.</summary>
    private Node CheckCinema(Node tree, string where)
    {
        Expect(Find(tree, n => n.Type == "ComboBox" && n.Name == T("音源")) != null, where, "no shared source selector");
        Expect(tree.Descendants().All(n => !n.Says(T("电脑端分声道")) && !n.Says(T("交换左右"))), where, "legacy split/swap controls remain");
        foreach (var disabled in new[] { "播放", "暂停", "停止", "测量音箱延迟" })
        {
            var button = Find(tree, n => n.Type == "Button" && n.Says(T(disabled)));
            Expect(button is { Enabled: false }, where,
                button == null ? $"no 「{T(disabled)}」 button" : $"「{T(disabled)}」 is enabled before a file plays");
        }
        Expect(Find(tree, n => n.Type == "Button" && n.Says(T("选择影音文件"))) is { Enabled: false }, where, $"「{T("选择影音文件")}」 is enabled for live capture");
        var source = Find(tree, n => n.Type == "ComboBox" && n.Name == T("音源"));
        if (source != null)
        {
            Native.Msaa(source.Hwnd)?.set_accValue(0, T("本地影音文件"));
            WaitUntil(() => Find(Uia.Snapshot(MainWindow()), n => n.Type == "Button" && n.Says(T("选择影音文件"))) is { Enabled: true },
                "file source enables its picker", 5000);
            tree = Uia.Snapshot(MainWindow());
            Layout(tree, where + " / file source");
            ShootPage(where, "file-source");
            Native.Msaa(source.Hwnd)?.set_accValue(0, T("电脑实时声音"));
            WaitUntil(() => Find(Uia.Snapshot(MainWindow()), n => n.Type == "Button" && n.Says(T("选择影音文件"))) is { Enabled: false },
                "live source disables its picker", 5000);
        }
        return Uia.Snapshot(MainWindow());
    }

    // ---------------------------------------------------------------- 同步观影窗口 (modules/VideoWindow)

    /// <summary>
    /// 音画同步 →「同步观影窗口（实验性）」→ 打开 starts the module from the folder in the config: no folder picker,
    /// no capture, the idle hint, at this scenario's DPI (per-monitor aware). Then the module on its own with generated 4K
    /// pictures (--smoke-source) against the app's local API: frames through the GPU queue and the swap chain, halved to
    /// 1080p, shown at the picture's aspect ratio, and the delay followed from the app. The window picker is never opened.
    /// </summary>
    private void ViewingWindow(bool connected)
    {
        string title = T("同步观影窗口（实验性）"); // experimental since 1.1
        Navigate("音画同步");
        try
        {
            Uia.Invoke(Button("打开"));
            var viewer = WaitWindow(() => ViewerWindow(title, null), "the viewing window started from 音画同步", 20_000);
            Thread.Sleep(1200);
            var tree = Uia.Snapshot(viewer);
            Dump(tree, "viewer");
            Shoot(viewer, "viewer", title);
            Layout(tree, "viewer");
            Checks.OnScreen(viewer, Where("viewer"), _report);
            CheckDpi(viewer, "viewer");
            foreach (var button in new[] { "选择窗口…", "停止采集", "全屏 / F11" })
                Expect(Find(tree, n => n.Type == "Button" && n.Says(T(button))) != null, "viewer", $"no 「{T(button)}」 button");
            string hint = T("选择要观看的窗口；这里只延迟画面，音频仍由 HomePodCast 发送。");
            Expect(Has(tree, hint), "viewer", $"the status line doesn't say 「{hint}」 before a window is chosen");
            CloseViewer(viewer, null, "viewer");

            int port = ApiPort() ?? throw new StepFailed("no 「local API on http://127.0.0.1:…」 line in the log");
            string result = Path.Combine(_dir, "viewer-pictures.json");
            var start = new ProcessStartInfo(Path.Combine(VideoWindowDir!, VideoWindowProcess + ".exe")) { UseShellExecute = false };
            foreach (var arg in new[] { "--smoke-source", "--status-port", port.ToString(), "--delay-ms", "0", "--language", _s.Language, "--smoke-result", result })
                start.ArgumentList.Add(arg);
            using var smoke = Process.Start(start) ?? throw new StepFailed("the viewing window did not start");
            try
            {
                var hwnd = WaitWindow(() => ViewerWindow(title, smoke.Id), "the viewing window with generated pictures", 20_000);
                var line = new Regex("^" + Regex.Replace(Regex.Escape(T("画面延迟 {0} ms · {1} · 缓存 {2} 帧 / {3} MiB")), @"\\\{(\d)}",
                    m => m.Groups[1].Value == "0" ? "(?<delay>\\d+)" : m.Groups[1].Value == "1" ? "(?<mode>.+?)" : ".+?") + "$");
                string following = T("已跟随本机音频状态");
                Match? status = null;
                WaitUntil(() => (status = StatusLine(hwnd, line)) is { Success: true } s && s.Groups["mode"].Value == following,
                    "the viewing window to follow the app's delay", 10_000, fail: false);
                Expect(status is { Success: true } && status.Groups["mode"].Value == following, "viewer (pictures)",
                    $"the status line is 「{status?.Value ?? "(none)"}」: it should follow the app (「{following}」)");
                if (status is { Success: true } && int.TryParse(status.Groups["delay"].Value, out int delay))
                {
                    Expect(connected ? delay > 0 : delay == 0, "viewer (pictures)",
                        $"picture delay {delay} ms while {(connected ? "streaming (expected the app's sound lag)" : "not streaming (expected 0)")}");
                    _report.Pass(Where("viewer (pictures)"), status.Value);
                }
                Thread.Sleep(1500);
                var pictures = Uia.Snapshot(hwnd);
                Dump(pictures, "viewer-pictures");
                Shoot(hwnd, "viewer-pictures", $"{title}: generated 4K pictures (--smoke-source)");
                Layout(pictures, "viewer (pictures)");
                CloseViewer(hwnd, smoke, "viewer (pictures)");
            }
            finally
            {
                if (!smoke.HasExited) { smoke.Kill(); smoke.WaitForExit(5000); }
            }

            // What the module counted: frames through the queue and the presenter, halved on the GPU, letterboxed, this DPI.
            if (!File.Exists(result)) { _report.Fail(Where("viewer (pictures)"), "the module wrote no result"); return; }
            var r = JsonDocument.Parse(File.ReadAllText(result)).RootElement;
            int shown = r.GetProperty("shown").GetInt32(), presents = r.GetProperty("presents").GetInt32();
            int width = r.GetProperty("frameWidth").GetInt32(), height = r.GetProperty("frameHeight").GetInt32();
            int surfaceWidth = r.GetProperty("surfaceWidth").GetInt32(), surfaceHeight = r.GetProperty("surfaceHeight").GetInt32();
            string? message = r.GetProperty("message").GetString();
            Expect(message == null, "viewer (pictures)", $"the module says 「{message}」");
            Expect(shown >= 20 && presents >= shown, "viewer (pictures)", $"{shown} frames shown in {presents} presents (expected at least 20)");
            Expect(width == 1920 && height == 1080, "viewer (pictures)", $"3840 × 2160 pictures were queued at {width} × {height} (expected 1920 × 1080)");
            Expect(surfaceHeight > 0 && Math.Abs(surfaceWidth * 9 - surfaceHeight * 16) <= 32, "viewer (pictures)",
                $"the picture is shown at {surfaceWidth} × {surfaceHeight}, not 16:9");
            Expect(r.GetProperty("dpi").GetInt32() == 96 * _s.Scale / 100, "viewer (pictures)", $"the module ran at {r.GetProperty("dpi")} dpi");
            _report.Pass(Where("viewer (pictures)"), $"{shown} frames in {presents} presents, queued at {width} × {height}, shown at {surfaceWidth} × {surfaceHeight}, " +
                                                    $"display latency {r.GetProperty("leadMs")} ms ({(r.GetProperty("leadMeasured").GetBoolean() ? "measured" : "one refresh")})");
        }
        finally { KillViewers(); }
    }

    private static Match? StatusLine(IntPtr hwnd, Regex line)
    {
        foreach (var n in Uia.Snapshot(hwnd).Descendants())
            if (n.Type == "Text" && line.Match(n.Name) is { Success: true } m) return m;
        return null;
    }

    /// <summary>A viewing window (of <paramref name="pid"/>, or of any module process); a message box of the module fails the step.</summary>
    private IntPtr ViewerWindow(string title, int? pid)
    {
        foreach (var p in Process.GetProcessesByName(VideoWindowProcess))
        {
            using (p)
            {
                if (pid != null && p.Id != pid) continue;
                foreach (var h in Native.TopLevelWindows(p.Id))
                {
                    if (!Native.IsWindowVisible(h)) continue;
                    if (Native.ClassName(h) == "#32770")
                        throw new StepFailed($"the viewing window showed a message box: {string.Join(" ", Uia.Snapshot(h).Descendants().Where(n => n.Type == "Text").Select(n => n.Name))}");
                    if (Native.WindowText(h).StartsWith(title, StringComparison.Ordinal)) return h;
                }
            }
        }
        return IntPtr.Zero;
    }

    private void CloseViewer(IntPtr hwnd, Process? process, string step)
    {
        Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        WaitUntil(() => !Native.IsWindow(hwnd), "the viewing window to close", 10_000);
        if (process != null)
        {
            if (!process.WaitForExit(10_000)) _report.Fail(Where(step), "the viewing window's process did not exit within 10 s");
            else Expect(process.ExitCode == 0, step, $"the viewing window exited with {process.ExitCode}");
        }
    }

    private static void KillViewers()
    {
        foreach (var p in Process.GetProcessesByName(VideoWindowProcess))
        {
            try { p.Kill(); p.WaitForExit(5000); }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
    }

    private void CheckDpi(IntPtr hwnd, string step)
    {
        uint dpi = Native.GetDpiForWindow(hwnd), want = (uint)(96 * _s.Scale / 100);
        Expect(dpi == want, step, $"the window is at {dpi} dpi, expected {want} ({_s.Scale}%): not per-monitor aware?");
    }

    /// <summary>The app's local API port (a random one in these configs), from its log.</summary>
    private int? ApiPort()
    {
        foreach (var line in ReadLog())
            if (Regex.Match(line, @"local API on http://127\.0\.0\.1:(\d+)/") is { Success: true } m) return int.Parse(m.Groups[1].Value);
        return null;
    }

    // ---------------------------------------------------------------- flyout

    private void Flyout(bool connected, bool compatibility = false)
    {
        var flyout = OpenFlyout();
        Thread.Sleep(500);
        var tree = Uia.Snapshot(flyout);
        Dump(tree, "flyout");
        string expected = connected ? T("断开") : T("连接");
        Expect(tree.Descendants().Any(n => n.Type == "Button" && n.Says(expected)), "flyout", $"no 「{expected}」 button");
        string name = compatibility ? "CI compatibility receiver" : connected ? _fake!.Name : T("还没有选择音箱");
        Expect(Has(tree, name), "flyout", $"doesn't show 「{name}」");
        if (compatibility)
        {
            Expect(tree.Descendants().Any(n => n.Shown && n.Name == T("兼容模式延迟（实验性）")), "flyout",
                "compatibility latency selector is missing"); // experimental since 1.1
            Expect(Has(tree, "2000 ms"), "flyout", "compatibility default is not 2000 ms");
        }
        Shoot(flyout, "flyout", "tray flyout");
        Layout(tree, "flyout");
        Checks.OnScreen(flyout, Where("flyout"), _report);

        // 打开主界面: the flyout goes, the window shows 首页.
        var open = Find(tree, n => n.Type == "Button" && n.Says(T("打开主界面")));
        if (open == null)
        {
            _report.Fail(Where("flyout"), $"no 「{T("打开主界面")}」 button");
            CloseFlyout(flyout);
            return;
        }
        Uia.Invoke(open);
        WaitUntil(() => !Native.IsWindowVisible(flyout), "the flyout to close after 打开主界面", 5000);
    }

    private IntPtr FindFlyout() => Native.TopLevelWindows(App.Id).FirstOrDefault(h =>
        (Native.ExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0 && Native.WindowText(h) == T("HomePod 音响"));

    /// <summary>A second <c>gui --flyout</c> asks the running copy to open its tray flyout (Program.RunGui).</summary>
    private IntPtr OpenFlyout()
    {
        var open = FindFlyout();
        if (open != IntPtr.Zero) return open;
        HandlePrompts(); // a message box still open would swallow the request: the flyout never came (2026-10-14)
        using (var second = Process.Start(StartInfo("gui --flyout"))!)
        {
            if (!second.WaitForExit(15_000)) throw new StepFailed("a second gui --flyout did not exit");
        }
        return WaitWindow(FindFlyout, "the tray flyout", 10_000);
    }

    private void CloseFlyout(IntPtr flyout)
    {
        if (!Native.IsWindowVisible(flyout)) return;
        Native.PostMessage(flyout, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        WaitUntil(() => !Native.IsWindowVisible(flyout), "the flyout to close", 5000);
    }

    // ---------------------------------------------------------------- dialogs

    private Node Button(string text) =>
        Find(Uia.Snapshot(MainWindow()), n => n.Type == "Button" && n.Says(T(text)) && n.Shown)
        ?? throw new StepFailed($"no 「{T(text)}」 button on {CurrentPageName()}");

    /// <summary>The button <paramref name="text"/> in the settings row titled <paramref name="title"/>.</summary>
    private Node ButtonInRow(string title, string text)
    {
        var tree = Uia.Snapshot(MainWindow());
        foreach (var caption in tree.Descendants().Where(n => n.Type == "Text" && n.Name == T(title) && n.Shown))
            if (caption.Parent is { } row && Find(row, n => n.Type == "Button" && n.Says(T(text))) is { } button)
                return button;
        throw new StepFailed($"no 「{T(text)}」 button in the row 「{T(title)}」");
    }

    private void Dialog(string what, Node opener, string titleKey, string closeKey, Action<Node, string>? check = null,
        Func<Node, bool>? ready = null)
    {
        string title = T(titleKey);
        Uia.Invoke(opener);
        var hwnd = WaitWindow(() => Native.TopLevelWindows(App.Id).FirstOrDefault(h => Native.WindowText(h) == title && Native.ClassName(h) != "#32770"),
            $"the 「{title}」 dialog", 10_000);
        Thread.Sleep(600);
        var tree = Uia.Snapshot(hwnd);
        if (ready != null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!ready(tree) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);
                tree = Uia.Snapshot(hwnd);
            }
            Thread.Sleep(300);
            tree = Uia.Snapshot(hwnd);
        }
        Dump(tree, what);
        Shoot(hwnd, what, title);
        Layout(tree, what);
        Checks.OnScreen(hwnd, Where(what), _report);
        check?.Invoke(tree, what);

        var close = Find(tree, n => n.Type == "Button" && n.Says(T(closeKey)));
        if (close != null) Uia.Invoke(close);
        else
        {
            _report.Fail(Where(what), $"no 「{T(closeKey)}」 button");
            Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        WaitUntil(() => !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd), $"the 「{title}」 dialog to close", 5000);
        if (App.HasExited)
            throw new StepFailed($"the app exited after closing 「{title}」: 0x{App.ExitCode:X8}");
        if (MainWindow() == IntPtr.Zero)
            throw new StepFailed($"the main window is no longer visible after closing 「{title}」");
    }

    // ---------------------------------------------------------------- pages

    private void Navigate(string page)
    {
        var tree = Uia.Snapshot(MainWindow());
        var item = Find(tree, n => n.Type == "TabItem" && n.Name == T(page))
                   ?? Find(tree, n => n.Name == T(page) && n.Parent?.Type == "Tab")
                   ?? throw new StepFailed($"no navigation item 「{T(page)}」");
        Uia.Invoke(item);
        WaitUntil(() => TryPagePane(Uia.Snapshot(MainWindow()), page) != null, $"the page 「{T(page)}」", 8000);
        Thread.Sleep(400); // pages refresh when shown
    }

    /// <summary>The visible page: the outermost pane (a window of its own) that has <paramref name="page"/> as a text.</summary>
    private Node? TryPagePane(Node tree, string page)
    {
        var level = tree.Children;
        while (level.Count > 0)
        {
            var pane = level.FirstOrDefault(c => c.Type == "Pane" && c.Hwnd != IntPtr.Zero && c.Shown &&
                                                 c.Descendants().Any(d => d.Type == "Text" && d.Name == T(page) && d.Shown));
            if (pane != null) return pane;
            level = level.SelectMany(c => c.Children).ToList();
        }
        return null;
    }

    private Node PagePane(Node tree, string page) => TryPagePane(tree, page) ?? throw new StepFailed($"the page 「{T(page)}」 is not shown");

    private string CurrentPageName()
    {
        var tree = Uia.Snapshot(MainWindow());
        return Pages.FirstOrDefault(p => TryPagePane(tree, p) != null) ?? "?";
    }

    /// <summary>Screenshots of the whole page, a screen at a time (it scrolls the page, then back to the top).</summary>
    private void ShootPage(string page, string label, bool checkEachScroll = false)
    {
        var main = MainWindow();
        var pane = PagePane(Uia.Snapshot(main), page).Hwnd;
        string name = PageFileName(page) + (label.Length > 0 ? "-" + label : "");
        Scroll(pane, Native.SB_TOP);
        Dump(Uia.Snapshot(main), name);
        for (int i = 1; i <= 12; i++)
        {
            if (checkEachScroll)
            {
                var tree = Uia.Snapshot(main);
                Dump(tree, $"{name}-{i}");
                Layout(tree, $"{page} ({label}) / scroll {i}");
            }
            Shoot(main, $"{name}-{i}", $"{page}{(label.Length > 0 ? " (" + label + ")" : "")} {i}");
            var info = Native.VerticalScroll(pane);
            if ((Native.Style(pane) & Native.WS_VSCROLL) == 0 || info is not { } si || si.nPos + si.nPage > si.nMax) break;
            int before = si.nPos;
            Scroll(pane, Native.SB_PAGEDOWN);
            if (Native.VerticalScroll(pane) is { } after && after.nPos == before) break;
        }
        Scroll(pane, Native.SB_TOP);
    }

    private static string PageFileName(string page) => page switch
    {
        "首页" => "home",
        "混音器" => "mixer",
        "麦克风" => "microphone",
        "均衡" => "equalizer",
        "音箱信息" => "speakers",
        "多音箱（实验性）" => "groups",
        "音画同步" => "av-sync",
        "诊断" => "diagnostics",
        "使用配置" => "profiles",
        "关于" => "about",
        _ => "settings",
    };

    private static void Scroll(IntPtr pane, int code)
    {
        Native.SendMessageTimeout(pane, Native.WM_VSCROLL, new IntPtr(code), IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 5000, out _);
        Thread.Sleep(350);
    }

    // ---------------------------------------------------------------- the app

    private Process App => _app ?? throw new StepFailed("the app is not running");

    private ProcessStartInfo StartInfo(string arguments)
    {
        var psi = new ProcessStartInfo(_exe, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exe)! };
        if (Profile != null) psi.Environment["HOMEPODCAST_PROFILE"] = Profile;
        else psi.Environment.Remove("HOMEPODCAST_PROFILE");
        if (_dotnetRoot != null) psi.Environment["DOTNET_ROOT"] = psi.Environment["DOTNET_ROOT_X64"] = _dotnetRoot; // Walker.Runtime.cs
        return psi;
    }

    /// <summary>A fresh config folder with <paramref name="config"/>, then the app; waits for its window and the startup prompts.</summary>
    private void Start(Dictionary<string, object?> config)
    {
        KillLeftovers();
        if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, recursive: true);
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path.Combine(ConfigDir, "config.json"), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        Cursor.Position = Point.Empty; // no hover effects or tooltips on the app's controls

        var watch = Stopwatch.StartNew();
        _app = Process.Start(StartInfo("gui")) ?? throw new StepFailed("the app did not start");
        var main = WaitWindow(MainWindow, "the main window", 30_000);
        _report.Note(Where(), $"main window after {watch.ElapsedMilliseconds} ms ({FileVersionInfo.GetVersionInfo(_exe).ProductVersion})");

        // The firewall check at startup may ask about a rule (none on the runner): answer it before anything else.
        WaitUntil(() => ReadLog().Any(l => l.Contains(" network: ", StringComparison.Ordinal)), "the startup firewall check in the log", 20_000, fail: false);
        Thread.Sleep(1500);
        HandlePrompts();

        uint dpi = Native.GetDpiForWindow(main), want = (uint)(96 * _s.Scale / 100);
        Expect(dpi == want, "window", $"the window is at {dpi} dpi, expected {want} ({_s.Scale}%)");
    }

    private IntPtr MainWindow()
    {
        var h = Native.TopLevelWindows(App.Id).FirstOrDefault(w =>
            Native.WindowText(w) == "HomePodCast" && (Native.ExStyle(w) & Native.WS_EX_TOOLWINDOW) == 0 &&
            Native.ClassName(w).StartsWith("WindowsForms10.", StringComparison.Ordinal));
        return h;
    }

    private void CheckAlive()
    {
        if (_app is { HasExited: true } app) throw new StepFailed($"the app exited (code {app.ExitCode})");
    }

    /// <summary>Quits from the tray flyout's 退出 (the way a user does); a window close message if that fails.</summary>
    private void Quit()
    {
        if (_app == null || _app.HasExited) return;
        try
        {
            var flyout = OpenFlyout();
            var quit = Find(Uia.Snapshot(flyout), n => n.Type == "Button" && n.Says(T("退出"))) ?? throw new StepFailed($"no 「{T("退出")}」 in the flyout");
            Uia.Invoke(quit);
        }
        catch (Exception ex) when (ex is StepFailed or InvalidOperationException or System.Windows.Automation.ElementNotAvailableException)
        {
            _report.Fail(Where("quit"), $"quitting from the flyout failed: {ex.Message}");
            var main = MainWindow();
            if (main != IntPtr.Zero) Native.PostMessage(main, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        if (!_app.WaitForExit(30_000))
        {
            _report.Fail(Where("quit"), "the app did not exit within 30 s");
            Kill();
            return;
        }
        Expect(_app.ExitCode == 0, "quit", $"exit code {_app.ExitCode}");
    }

    private void StopApp()
    {
        if (_app is { HasExited: false })
        {
            Kill();
            _app.WaitForExit(10_000);
        }
    }

    private void KillLeftovers()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_exe)))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(10_000);
                }
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Message boxes: the firewall ones are expected on a runner without the MSI's rule (answered No), any other one fails.</summary>
    private void HandlePrompts()
    {
        if (_app == null || _app.HasExited || _inPrompts) return;
        _inPrompts = true; // waiting for a box to close goes through WaitUntil, which comes back here
        try { AnswerPrompts(); }
        finally { _inPrompts = false; }
    }

    private bool _inPrompts;

    private void AnswerPrompts()
    {
        foreach (var h in Native.TopLevelWindows(App.Id))
        {
            // A closed native picker can remain as a hidden window briefly while its owner disposes it.
            // Only a visible, sized dialog can block the user; closing windows are not new error prompts.
            if (Native.ClassName(h) != "#32770" || !Native.IsWindowVisible(h)
                || Native.WindowRect(h) is not { Width: > 0, Height: > 0 }) continue;
            string title = Native.WindowText(h);
            Node tree;
            try { tree = Uia.Snapshot(h); }
            catch (Exception) { continue; }
            string text = string.Join(" ", tree.Descendants().Where(n => n.Type == "Text").Select(n => n.Name));
            bool expected = title == T("需要防火墙放行") || title == T("当前是公用网络");
            if (_promptsShot.Add(title)) Shoot(h, "prompt", $"message box 「{title}」");
            if (expected) _report.Note(Where(), $"answered No to 「{title}」 (the runner has no firewall rule for the exe)");
            else _report.Fail(Where(), $"unexpected message box 「{title}」: {text}");
            var button = new[] { "7", "2", "1" } // IDNO, IDCANCEL, IDOK
                .Select(id => Find(tree, n => n.Type == "Button" && n.AutomationId == id)).FirstOrDefault(b => b != null);
            if (button != null) Uia.Invoke(button);
            else Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            WaitUntil(() => !Native.IsWindow(h) || !Native.IsWindowVisible(h), $"the message box 「{title}」 to close", 5000);
        }
    }

    // ---------------------------------------------------------------- log

    private List<string> ReadLog()
    {
        try
        {
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void SaveLog()
    {
        var lines = ReadLog();
        if (lines.Count > 0) File.WriteAllLines(Path.Combine(_dir, "homepodcast.log"), lines);
    }

    private void CheckLog(bool idle)
    {
        var lines = ReadLog();
        Expect(lines.Count > 0, "log", $"{LogPath} is empty or missing");
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^\S+ ERROR ") || line.Contains("Unhandled exception", StringComparison.Ordinal))
                _report.Fail(Where("log"), line);
        }
        if (idle)
        {
            Expect(lines.Any(l => l.Contains("update: automatic checks are off", StringComparison.Ordinal)), "log",
                "no 「update: automatic checks are off」 line on a fresh config");
            Expect(!lines.Any(l => l.Contains("api.github.com", StringComparison.Ordinal)), "log", "the app contacted GitHub without being asked");
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Waits for the status line of 首页 or the flyout (by a predicate on its text); returns it and the connect button's text.</summary>
    private (string Status, string Button) WaitForStatus(Func<IntPtr> window, string where, Func<string, bool> match, int timeoutMs)
    {
        string connect = T("连接"), disconnect = T("断开");
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string last = "";
        while (true)
        {
            CheckAlive();
            HandlePrompts();
            var hwnd = window();
            if (hwnd != IntPtr.Zero)
            {
                var tree = Uia.Snapshot(hwnd);
                var status = tree.Descendants().FirstOrDefault(n => n.Type == "Text" && n.Shown && match(n.Name));
                var button = tree.Descendants().FirstOrDefault(n => n.Type == "Button" && n.Shown && (n.Name == connect || n.Name == disconnect));
                if (status != null && button != null)
                {
                    // The status and the button change together; read again to be sure neither changed in between.
                    var again = Uia.Snapshot(hwnd);
                    var status2 = again.Descendants().FirstOrDefault(n => n.Type == "Text" && n.Shown && n.Name == status.Name);
                    var button2 = again.Descendants().FirstOrDefault(n => n.Type == "Button" && n.Shown && (n.Name == connect || n.Name == disconnect));
                    if (status2 != null && button2?.Name == button.Name) return (status.Name, button.Name);
                }
                last = string.Join(" | ", tree.Descendants().Where(n => n.Type == "Text" && n.Shown && n.Name.Length > 0).Take(12).Select(n => Node.Shorten(n.Name)));
            }
            if (DateTime.UtcNow > deadline)
            {
                if (hwnd != IntPtr.Zero) Dump(Uia.Snapshot(hwnd), $"timeout-{where}");
                throw new StepFailed($"{where}: the status never matched (texts: {last})");
            }
            Thread.Sleep(250);
        }
    }

    private IntPtr WaitWindow(Func<IntPtr> find, string what, int timeoutMs, bool handlePrompts = true)
    {
        IntPtr found = IntPtr.Zero;
        WaitUntil(() => (found = find()) != IntPtr.Zero, what, timeoutMs, handlePrompts: handlePrompts);
        return found;
    }

    private void WaitUntil(Func<bool> condition, string what, int timeoutMs, bool fail = true, bool handlePrompts = true)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            CheckAlive();
            if (condition()) return;
            if (DateTime.UtcNow > deadline)
            {
                if (fail) throw new StepFailed($"waited {timeoutMs / 1000} s for {what}");
                _report.Warn(Where(), $"waited {timeoutMs / 1000} s for {what}");
                return;
            }
            if (handlePrompts) HandlePrompts();
            Thread.Sleep(200);
        }
    }

    private static Node? Find(Node root, Func<Node, bool> match) => root.Descendants().FirstOrDefault(match);

    private static bool Has(Node root, string text) => root.Descendants().Any(n => n.Shown && n.Name.Contains(text, StringComparison.Ordinal));

    private void Expect(bool ok, string step, string failure)
    {
        if (!ok) _report.Fail(Where(step), failure);
    }

    /// <summary>
    /// The layout checks; a failure is reported only if a second look a moment later still finds it (the controls are read
    /// one by one, and a status line that changes meanwhile re-lays the card out between two reads).
    /// </summary>
    private void Layout(Node tree, string step)
    {
        var first = new Report { Silent = true };
        Checks.Layout(tree, Where(step), _t, first, _userData);
        if (first.Failures.Count > 0 && tree.Hwnd != IntPtr.Zero && Native.IsWindowVisible(tree.Hwnd))
        {
            // A firewall message box can come up late, a child of the main window over the page. 2026-10-14: twice a
            // runner's network change brought 「当前是公用网络」 up during 首页's checks. Answer it, then look again.
            HandlePrompts();
            Thread.Sleep(800);
            first = new Report { Silent = true };
            Checks.Layout(Uia.Snapshot(tree.Hwnd), Where(step), _t, first, _userData);
        }
        foreach (var f in first.Failures) _report.Fail(f.Where, f.Text);
        foreach (var w in first.Warnings) _report.Warn(w.Where, w.Text);
    }

    private void Shoot(IntPtr hwnd, string name, string caption)
    {
        try
        {
            using var bitmap = Native.Capture(hwnd);
            string file = $"{++_shots:00}-{name}.png";
            bitmap.Save(Path.Combine(_dir, file), ImageFormat.Png);
            _report.Shots.Add(new Report.Shot(_s.Id, $"{_s.Id}/{file}", $"{_s.Id}: {caption}"));
        }
        catch (Exception ex)
        {
            _report.Fail(Where(name), $"screenshot failed: {ex.Message}");
        }
    }

    /// <summary>The UI Automation tree as text, next to the screenshots (to see what a check saw).</summary>
    private void Dump(Node tree, string name) => File.WriteAllText(Path.Combine(_dir, $"tree-{name}.txt"), Uia.Dump(tree));
}
