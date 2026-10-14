using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomePodCast.Tests;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// One run of the app: <c>idle</c> (a fresh config, no speaker), <c>retrying</c> (a speaker address nobody answers, with
/// automatic connect: 「找不到音箱…，N 秒后重试」) or <c>streaming</c> (connected to the tests' fake HomePod on 127.0.0.1:7000),
/// in a UI language, at a display scale, in a theme. <see cref="Narrow"/> walks the pages again at the window's minimum width.
/// </summary>
internal sealed record Scenario(string Kind, string Language, int Scale, string Theme = "auto", bool Narrow = false)
{
    public string Id => $"{Kind}-{Language}-{Scale}" + (Theme == "auto" ? "" : "-" + Theme);
}

/// <summary>A step that can't go on (a window that never appeared, the app gone): the scenario stops there.</summary>
internal sealed class StepFailed(string message) : Exception(message);

/// <summary>Drives one scenario: starts the app, walks it through UI Automation, checks, takes screenshots, quits it.</summary>
internal sealed class Walker
{
    /// <summary>
    /// HOMEPODCAST_PROFILE for the app: null on a CI runner (the default config folder, as a fresh install: the
    /// screenshots show what a user sees), "gui-smoke" anywhere else (its own config folder and single-instance name).
    /// </summary>
    public static string? Profile { get; set; } = "gui-smoke";

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Profile == null ? "HomePodCast" : "HomePodCast-" + Profile);

    private static string LogPath => Path.Combine(ConfigDir, "homepodcast.log");

    private static readonly string[] Pages = ["首页", "混音器", "麦克风与音效", "设置"];

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
        try
        {
            switch (_s.Kind)
            {
                case "idle": RunIdle(); break;
                case "retrying": RunRetrying(); break;
                case "streaming": RunStreaming(); break;
                default: throw new ArgumentException($"unknown scenario kind {_s.Kind}");
            }
        }
        catch (StepFailed ex)
        {
            _report.Fail(Where(), "stopped: " + ex.Message);
        }
        catch (Exception ex)
        {
            _report.Fail(Where(), $"stopped by {ex.GetType().Name}: {ex.Message}");
            File.WriteAllText(Path.Combine(_dir, "exception.txt"), ex.ToString());
        }
        finally
        {
            StopApp();
            SaveLog();
            _fake?.Dispose();
        }
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
    };

    private void RunIdle()
    {
        Start(BaseConfig());
        WalkEverything(connected: false);
        Quit();
        CheckLog(idle: true);
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
            if (page == "设置") tree = CheckSettings(connected);
            CheckNavigation(tree);
            Layout(tree, page);
            ShootPage(page, "");
            if (page == "首页") Flyout(connected);
        }

        Dialog("hotkeys", Button("快捷键…"), "全局快捷键", "关闭", CheckHotkeys);
        Dialog("profiles", Button("管理配置"), "使用配置", "关闭");
        Dialog("multi-speaker", ButtonInRow("多音箱（实验性）", "打开"), "多音箱（实验性）", "取消", ready: tree =>
            !tree.Descendants().Any(n => n.Name == T("正在搜索音箱…")));
        // Opening it starts nothing (no file, no microphone); 关闭 must leave the connection to the fake HomePod as it was.
        Dialog("cinema", ButtonInRow("无线影院（实验性）", "打开"), "无线影院（实验性）", "关闭", CheckCinema);

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
                Layout(tree, page + " (narrow)");
                ShootPage(page, "narrow");
            }
        }
    }

    // ---------------------------------------------------------------- page checks

    private void CheckNavigation(Node tree, bool compact = false)
    {
        var settings = Find(tree, n => n.Type == "TabItem" && n.Name == T("设置"));
        Expect(settings?.Parent != null, "navigation", "no settings navigation item");
        if (settings?.Parent is not { } rail) return;
        int dpi = (int)Native.GetDpiForWindow(MainWindow());
        int gap = 8 * dpi / 96;
        Expect(Math.Abs(rail.Rect.Bottom - settings.Rect.Bottom - gap) <= 2, "navigation",
            "settings is not pinned to the bottom of the rail");
        if (compact)
            Expect(rail.Rect.Width <= 64 * dpi / 96 + 1, "navigation", "narrow window did not compact the navigation rail");
    }

    private void CheckHome(Node tree, bool connected)
    {
        var page = PagePane(tree, "首页");
        var buttons = page.Descendants().Where(n => n.Type == "Button" && n.Shown).ToList();
        string expected = connected ? T("断开") : T("连接");
        Expect(buttons.Any(b => b.Says(expected)), "首页", $"no 「{expected}」 button (buttons: {string.Join(", ", buttons.Select(b => b.Name))})");
        foreach (var gone in new[] { "刚才卡了", "导出诊断" }) // moved: 设置 → 工具, and a hotkey
            Expect(buttons.All(b => !b.Says(T(gone))), "首页", $"「{T(gone)}」 is on 首页 again");
        if (!connected)
        {
            Expect(Has(page, T("未连接")), "首页", $"the status doesn't say 「{T("未连接")}」");
            return;
        }

        // The tiles show this connection's values (MetricTile: the caption is the name, the value the description).
        var deadline = DateTime.UtcNow.AddSeconds(8);
        foreach (var caption in new[] { "实际延迟", "缓冲", "音箱处理", "断音", "重传" })
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

    private Node CheckSettings(bool connected)
    {
        // 音箱信息 reads the speaker when the page is shown: wait for 读取中… to go.
        var tree = Uia.Snapshot(MainWindow());
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Has(tree, T("读取中…")) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            tree = Uia.Snapshot(MainWindow());
        }
        var page = PagePane(tree, "设置");
        Expect(page.Descendants().Any(n => n.Type == "Button" && n.Says(T("导出诊断"))), "设置", $"no 「{T("导出诊断")}」 button (设置 → 工具)");
        Expect(Has(page, T("工具")), "设置", $"no 「{T("工具")}」 section");
        Expect(Has(page, T("手动添加 AirPlay 设备（实验性）")), "设置", "manual AirPlay receiver row missing"); // experimental since 1.1
        Expect(Has(page, T("音箱信息")), "设置", $"no 「{T("音箱信息")}」 section");
        if (connected)
        {
            Expect(Has(page, _fake!.Name), "设置", $"音箱信息 doesn't name 「{_fake.Name}」");
            Expect(!Has(page, T("无法读取（音箱不在线？）")), "设置", "音箱信息 could not read the speaker");
        }
        else
        {
            Expect(Has(page, T("还没有选择音箱")), "设置", $"音箱信息 doesn't say 「{T("还没有选择音箱")}」");
        }

        // 夜间自动断开 is off on a fresh config. Not switched on here: inside its window on an idle runner it would end the
        // connection to the fake HomePod that the next steps check.
        var night = Find(page, n => n.Name == T("夜间自动断开（实验性）") && n.Type != "Text" && n.Hwnd != IntPtr.Zero);
        var nightState = night == null ? null : Native.MsaaState(night.Hwnd);
        Expect(nightState is { } ns && (ns & Native.STATE_SYSTEM_CHECKED) == 0, "设置",
            $"「{T("夜间自动断开（实验性）")}」 is not off on a fresh config (state {nightState?.ToString("X") ?? "not found"})");

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

    /// <summary>无线影院: every speaker position, nothing playable before a file is chosen, no measurement without playback.</summary>
    private void CheckCinema(Node tree, string where)
    {
        foreach (var role in new[] { "前左", "前右", "中置", "低音声道", "后左", "后右", "侧左", "侧右" })
            Expect(tree.Descendants().Any(n => n.Type == "Text" && n.Name == T(role)), where, $"no speaker row 「{T(role)}」");
        foreach (var disabled in new[] { "播放", "暂停", "停止", "测量音箱延迟" })
        {
            var button = Find(tree, n => n.Type == "Button" && n.Says(T(disabled)));
            Expect(button is { Enabled: false }, where,
                button == null ? $"no 「{T(disabled)}」 button" : $"「{T(disabled)}」 is enabled before a file plays");
        }
        Expect(Find(tree, n => n.Type == "Button" && n.Says(T("选择影音文件"))) is { Enabled: true }, where, $"「{T("选择影音文件")}」 is not enabled");
    }

    // ---------------------------------------------------------------- flyout

    private void Flyout(bool connected)
    {
        var flyout = OpenFlyout();
        Thread.Sleep(500);
        var tree = Uia.Snapshot(flyout);
        Dump(tree, "flyout");
        string expected = connected ? T("断开") : T("连接");
        Expect(tree.Descendants().Any(n => n.Type == "Button" && n.Says(expected)), "flyout", $"no 「{expected}」 button");
        string name = connected ? _fake!.Name : T("还没有选择音箱");
        Expect(Has(tree, name), "flyout", $"doesn't show 「{name}」");
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
    private void ShootPage(string page, string label)
    {
        var main = MainWindow();
        var pane = PagePane(Uia.Snapshot(main), page).Hwnd;
        string name = PageFileName(page) + (label.Length > 0 ? "-" + label : "");
        Scroll(pane, Native.SB_TOP);
        Dump(Uia.Snapshot(main), name);
        for (int i = 1; i <= 12; i++)
        {
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
        "麦克风与音效" => "effects",
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
            if (Native.ClassName(h) != "#32770") continue;
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

    private IntPtr WaitWindow(Func<IntPtr> find, string what, int timeoutMs)
    {
        IntPtr found = IntPtr.Zero;
        WaitUntil(() => (found = find()) != IntPtr.Zero, what, timeoutMs);
        return found;
    }

    private void WaitUntil(Func<bool> condition, string what, int timeoutMs, bool fail = true)
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
            HandlePrompts();
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
