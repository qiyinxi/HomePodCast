using System.Diagnostics;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// GUI smoke test of a built HomePodCast.exe (CI job gui-smoke, .github/workflows/build.yml):
/// <c>HomePodCast.GuiSmoke --app &lt;HomePodCast.exe&gt; --out &lt;folder&gt; [--video-window &lt;module folder&gt;] [--only id,id…] [--shard index/count] [--allow-local]</c>.
/// Runs the scenarios below one after the other, at 100 % and 150 % display scale; writes
/// the screenshots, the UI Automation trees and each run's log into the folder, with index.html and report.md; exits 1
/// when any check failed. The zero-based shard partitions the filtered scenario list by index modulo count.
/// It starts the real app, so it refuses to run outside GitHub Actions unless given --allow-local: on a PC with a
/// HomePod, any copy that connects takes the speaker away from the HomePodCast already running there.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Every page and dialog in Chinese, English and German (the long one) at 100 % and also at the window's minimum
    /// width; at 150 % in Chinese and German; one dark run; the retry wait (「找不到音箱…」) and a connection to the fake
    /// HomePod, which also walk everything while connected; a .NET update under the connected app, and 立即重启; once, the scale goes
    /// 150 % → 100 % → 150 % while the app runs. The group-output editor has a focused 150 % run in every language.
    /// </summary>
    private static readonly Scenario[] Matrix =
    [
        new("idle", "zh-CN", 100, Narrow: true),
        new("idle", "en", 100, Narrow: true),
        new("idle", "de", 100, Narrow: true),
        new("idle", "zh-CN", 100, Theme: "dark"),
        new("retrying", "zh-CN", 100),
        new("retrying", "en", 100),
        new("streaming", "zh-CN", 100),
        new("runtime", "zh-CN", 100),
        new("update-notes", "zh-CN", 100, RequiredScale: true),
        new("update-notes", "en", 150, RequiredScale: true),
        new("idle", "zh-CN", 150, DpiChange: true),
        new("idle", "de", 150),
        new("streaming", "de", 150),
        new("compatibility", "zh-CN", 100, Narrow: true),
        new("compatibility", "en", 100),
        new("compatibility", "de", 150),
        .. L.Languages.Select(language => new Scenario("group-outputs", language, 150, RequiredScale: true)),
    ];

    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromMinutes(6);

    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--uia-probe") return UiaProbe.Run(args[1..]);
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (args.Contains("--selftest")) return SelfTest();
        if (Opt("--fit-dumps") is { } dumps) return FitDumps(dumps);

        (int Index, int Count)? shard = null;
        if (args.Contains("--shard"))
        {
            if (!TryParseShard(Opt("--shard"), out var parsed))
            {
                Console.Error.WriteLine("--shard requires index/count, with count > 0 and 0 <= index < count (for example 0/5).");
                return 2;
            }
            shard = parsed;
        }
        var exe = Opt("--app") is { } app ? Path.GetFullPath(app) : null;
        if (exe == null || !File.Exists(exe))
        {
            Console.Error.WriteLine("usage: HomePodCast.GuiSmoke --app <HomePodCast.exe> --out <folder> [--only id,id] [--shard index/count] [--allow-local]");
            return 2;
        }
        bool ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (!ci && !args.Contains("--allow-local"))
        {
            Console.Error.WriteLine("This starts HomePodCast's window, and a copy that connects takes the HomePod away from the one already " +
                                    "running on this PC. It is meant for the CI runner; pass --allow-local only on a machine without a speaker.");
            return 2;
        }
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2); // UI Automation and screenshots in real pixels
        Walker.Profile = ci ? null : "gui-smoke";
        var outDir = Path.GetFullPath(Opt("--out") ?? "gui-screenshots");
        Directory.CreateDirectory(outDir);
        var only = Opt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scenarios = SelectScenarios(only, shard);

        var report = new Report();
        if (shard is { } part) report.Note("runner", $"scenario shard {part.Index}/{part.Count} after --only filtering");
        // The synchronized viewing window (experimental since 1.1), from the build's module zip, extracted.
        if (Opt("--video-window") is { } module)
        {
            module = Path.GetFullPath(module);
            if (File.Exists(Path.Combine(module, "HomePodCast.VideoWindow.exe"))) Walker.VideoWindowDir = module;
            else report.Fail("runner", $"--video-window {module}: no HomePodCast.VideoWindow.exe there");
        }
        else report.Warn("runner", "no --video-window: the synchronized viewing window is not checked");
        var total = Stopwatch.StartNew();
        var originalSize = Display.Resolution();
        var originalScale = Display.Scale();
        report.Note("runner", $"display {originalSize.Width}x{originalSize.Height} at {originalScale?.Current.ToString() ?? "?"}% " +
                              $"(recommended {originalScale?.Recommended.ToString() ?? "?"}%, at most {originalScale?.Maximum.ToString() ?? "?"}%); " +
                              $"modes: {string.Join(", ", Display.Modes().Select(m => $"{m.Width}x{m.Height}"))}");
        // A bigger screen than the runner's 1024x768, so that 150 % is offered (Windows keeps the scaled screen at least
        // about 600 px high) and the window gets its normal size.
        if (originalSize.Height < 1080 && Display.Modes().Contains(new Size(1920, 1080)))
        {
            var (ok, detail) = Display.SetResolution(new Size(1920, 1080));
            if (ok) report.Note("runner", detail);
            else report.Warn("runner", detail);
        }

        foreach (var group in scenarios.GroupBy(s => s.Scale))
        {
            var (scaled, why) = Display.SetScale(group.Key);
            if (!scaled)
            {
                foreach (var s in group)
                {
                    string detail = $"{group.Key}% scale not available on this runner: {why}";
                    if (s.RequiredScale) report.Fail(s.Id, detail);
                    else report.Warn(s.Id, detail);
                    report.Scenarios.Add((s.Id, s.RequiredScale ? $"**failed: required display scale unavailable** ({why})" : $"skipped: {detail}"));
                }
                continue;
            }
            report.Note("runner", why);
            foreach (var scenario in group)
            {
                Console.WriteLine($"=== {scenario.Id}");
                int failuresBefore = report.Failures.Count;
                var watch = Stopwatch.StartNew();
                var walker = new Walker(scenario, exe, outDir, report);
                var thread = new Thread(walker.Run) { IsBackground = true, Name = scenario.Id };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
                if (!thread.Join(ScenarioTimeout))
                {
                    report.Fail(scenario.Id, $"did not finish within {ScenarioTimeout.TotalMinutes} minutes");
                    walker.Kill();
                    thread.Join(TimeSpan.FromSeconds(60));
                }
                if (scenario.Kind == "group-outputs" && !report.Shots.Any(shot => shot.Scenario == scenario.Id))
                    report.Fail(scenario.Id, "the required group-output scenario produced no screenshot");
                if (scenario.Kind == "update-notes" && report.Shots.Count(shot => shot.Scenario == scenario.Id) < 2)
                    report.Fail(scenario.Id, "the required update-notes scenario did not capture both the top and bottom");
                int failed = report.Failures.Count - failuresBefore;
                report.Scenarios.Add((scenario.Id, (failed == 0 ? "passed" : $"**{failed} failure(s)**") + $" ({watch.Elapsed.TotalSeconds:F0} s)"));
            }
        }

        // Leave the runner's screen as it was (it is thrown away afterwards anyway).
        if (originalScale != null) Display.SetScale(originalScale.Current);
        if (Display.Resolution() != originalSize && originalSize != Size.Empty) Display.SetResolution(originalSize);

        report.Note("runner", $"{scenarios.Count} scenario(s) in {total.Elapsed.TotalMinutes:F1} min, {report.Shots.Count} screenshots");
        File.WriteAllText(Path.Combine(outDir, "index.html"), report.Html());
        var markdown = report.Markdown();
        File.WriteAllText(Path.Combine(outDir, "report.md"), markdown);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllText(summary, markdown);
        Console.WriteLine();
        Console.WriteLine(markdown);
        return report.Failures.Count == 0 ? 0 : 1;
    }

    private static bool TryParseShard(string? value, out (int Index, int Count) shard)
    {
        shard = default;
        var parts = value?.Split('/');
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) ||
            count <= 0 || index < 0 || index >= count) return false;
        shard = (index, count);
        return true;
    }

    private static List<Scenario> SelectScenarios(string[]? only, (int Index, int Count)? shard = null)
    {
        var filtered = Matrix.Where(s => only == null || only.Any(o => o == "baseline"
            ? s.Kind != "group-outputs" : s.Id.StartsWith(o, StringComparison.Ordinal)));
        return filtered.Where((_, index) => shard == null || index % shard.Value.Count == shard.Value.Index).ToList();
    }

    /// <summary>
    /// <c>--fit-dumps &lt;folder&gt;</c>: the cut-off check on the UI trees of a downloaded gui-screenshots artifact
    /// (tree-*.txt; the scale and language from the scenario folder's name), for working on that check without the app.
    /// </summary>
    private static int FitDumps(string folder)
    {
        var line = new System.Text.RegularExpressions.Regex(@"^\s*Text ""(?<name>.*)"" \[(?<x>-?\d+),(?<y>-?\d+) (?<w>\d+)x(?<h>\d+)\] hwnd=");
        foreach (var file in Directory.GetFiles(folder, "tree-*.txt", SearchOption.AllDirectories).Order())
        {
            var scenario = Path.GetFileName(Path.GetDirectoryName(file))!;
            int dpi = scenario.Contains("-150") ? 144 : 96;
            bool latin = !scenario.Contains("-zh-");
            foreach (var text in File.ReadLines(file))
            {
                var m = line.Match(text);
                if (!m.Success) continue;
                var r = new Rectangle(int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value));
                if (r.Width > 0 && r.Height > 0 && Checks.TextFit.Needed(m.Groups["name"].Value, r, dpi, latin) is { } needed)
                    Console.WriteLine($"{scenario}/{Path.GetFileName(file)}: \"{m.Groups["name"].Value}\" {r.Width}x{r.Height}, needs {needed}");
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>--selftest</c>: what the runner itself can check without starting the app or changing the screen: the texts and
    /// status patterns it looks for in each language, and what it reads about the display.
    /// </summary>
    private static int SelfTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }
        var groupScenarios = Matrix.Where(s => s.Kind == "group-outputs").ToArray();
        var baseline = SelectScenarios(["baseline"]);
        Check(baseline.Select(s => s.Id).SequenceEqual(Matrix.Where(s => s.Kind != "group-outputs").Select(s => s.Id)),
            "baseline automatically includes every non-group scenario");
        Check(baseline.Where(s => s.Kind == "update-notes").Select(s => (s.Language, s.Scale, s.RequiredScale))
            .SequenceEqual(new[] { ("zh-CN", 100, true), ("en", 150, true) }),
            "baseline requires Chinese and English release-notes screenshots at their specified scales");
        Check(groupScenarios.Select(s => s.Language).Order().SequenceEqual(L.Languages.Order()),
            "group-output scenarios cover every supported language exactly once");
        Check(groupScenarios.All(s => s.Scale == 150 && s.RequiredScale), "every group-output scenario requires 150 % scale");
        var groupPartitions = Enumerable.Range(0, 5).Select(index => SelectScenarios(["group-outputs"], (index, 5))).ToArray();
        Check(groupPartitions.All(part => part.Count > 0 && part.All(s => s.Kind == "group-outputs")),
            "every group-output shard contains only group-output scenarios");
        Check(groupPartitions.SelectMany(part => part).Select(s => s.Id).Order().SequenceEqual(groupScenarios.Select(s => s.Id).Order()),
            "five group-output shards cover every required scenario exactly once");
        Check(groupPartitions.All(part => part.Count <= (groupScenarios.Length + 4) / 5), "group-output shards are evenly distributed");
        Check(TryParseShard("0/5", out var firstShard) && firstShard == (0, 5) &&
              TryParseShard("4/5", out var lastShard) && lastShard == (4, 5), "zero-based shard bounds are accepted");
        foreach (var invalid in new string?[] { null, "", "0", "0/", "/5", "0/0", "-1/5", "5/5", "0/-1", "one/5", "0/5/1", "0/2147483648" })
            Check(!TryParseShard(invalid, out _), $"invalid shard '{invalid ?? "<missing>"}' is refused");
        foreach (var lang in L.Languages)
        {
            var t = new Texts(lang);
            string notFound = t.T("找不到音箱：请确认它已通电、和电脑在同一网络");
            string status = t.F("{0}，{1:F0} 秒后重试", notFound, 16.0);
            Check(t.Pattern("{0}，{1:F0} 秒后重试").IsMatch(status) && status.StartsWith(notFound, StringComparison.Ordinal), $"{lang}: retry status 「{status}」");
            Check(!t.Pattern("{0}，{1:F0} 秒后重试").IsMatch(t.T("正在连接…")), $"{lang}: 「{t.T("正在连接…")}」 is no retry status");
            Check(t.Pattern("正在重连（第 {0} 次）…").IsMatch(t.F("正在重连（第 {0} 次）…", 3)), $"{lang}: 「{t.F("正在重连（第 {0} 次）…", 3)}」");
            Check(t.Pattern("已连接 · {0}").IsMatch(t.F("已连接 · {0}", "Smoke Test HomePod")), $"{lang}: 「{t.F("已连接 · {0}", "Smoke Test HomePod")}」");
            foreach (var key in new[] { "首页", "混音器", "麦克风", "均衡", "设置", "连接", "断开", "全局快捷键", "标记刚才的卡顿", "未设置", "使用配置",
                         "多音箱（实验性）", "导出诊断", "音箱信息", "HomePod 音响", "打开主界面", "退出", "需要防火墙放行", "当前是公用网络", "自动检查更新",
                         "更新渠道", "测试版", "正式版", "快捷键…", "管理配置", "打开", "关闭", "取消", "实际延迟", "缓冲", "音箱处理", "重传", "播放统计", "缓冲欠载", "网络波动",
                         "无线影院（实验性）", "选择影音文件", "播放", "暂停", "停止", "测量音箱延迟", "左", "低音", "侧右", "音源", "电脑实时声音", "本地影音文件",
                         "音画同步", "本地播放器", "影视场景自动调整播放器", "VLC 网页接口密码（自动读取失败时填写）", "选择模块…",
                         "同步观影窗口（实验性）", "选择窗口…", "停止采集", "全屏 / F11", "已跟随本机音频状态", "自动扣除显示延迟",
                         "画面延迟 {0} ms · {1} · 缓存 {2} 帧 / {3} MiB", "选择要观看的窗口；这里只延迟画面，音频仍由 HomePodCast 发送。",
                         "关于", "许可证", "切换到浅色", "切换到深色", "诊断", "音画同步测试", "日志", "诊断报告", "打开日志文件夹", "当前版本 {0}", "检查更新", "更新", "查看说明", "跳过此版本", "立即重启",
                         "HomePod 静音", "取消静音", "仅 HomePod 输出", "夜间模式" })
                Check(t.Cjk || !Texts.HasCjk(t.T(key)), $"{lang}: {key} → {t.T(key)}");
        }
        var scale = Display.Scale();
        Console.WriteLine($"display {Display.Resolution().Width}x{Display.Resolution().Height}, scale {scale?.Current}% " +
                          $"(recommended {scale?.Recommended}%, max {scale?.Maximum}%), monitor {Native.PrimaryMonitorDpi()} dpi; " +
                          $"modes {string.Join(", ", Display.Modes().Select(m => $"{m.Width}x{m.Height}"))}");
        return failures == 0 ? 0 : 1;
    }
}
