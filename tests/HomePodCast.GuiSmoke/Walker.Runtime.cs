using System.Diagnostics;
using System.Net.Sockets;
using HomePodCast.Tests;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// The scenario <c>runtime</c>: a .NET update replaces the runtime under the running app (2026-10-13), faked on a private
/// copy of the runner's .NET, which the app is started on (DOTNET_ROOT), connected to the fake HomePod. A newer patch
/// version then appears next to it, and the version the app runs on loses its deps.json and the network assemblies it
/// has not loaded, as an update's uninstall leaves it. 「检查更新」 fails to load System.Net.Http, which makes the app look
/// and find its runtime gone: 首页 shows the notice with 「立即重启」. The click quits the app, and it comes back by itself
/// on the newer version, with its window and connected to the fake HomePod again, without the notice. The runner's own
/// .NET is never touched.
/// </summary>
internal sealed partial class Walker
{
    /// <summary>The private .NET the app runs on (DOTNET_ROOT for it and every copy it starts); null: the runner's own.</summary>
    private string? _dotnetRoot;

    private const string NetCoreApp = "Microsoft.NETCore.App";

    private void RunRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "hpc-smoke-dotnet-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (version, newer) = CopyDotnet(root);
            _dotnetRoot = root;
            try
            {
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

            // The log names the folder as the host found it (%TEMP% may be an 8.3 path on the runner): matched from the copy's name.
            string RuntimeLine(string v) => $"{Path.GetFileName(root)}\\shared\\{NetCoreApp}\\{v} (shared";
            string runtime = Path.Combine(root, "shared", NetCoreApp, version);
            WaitUntil(() => ReadLog().Any(l => l.Contains(RuntimeLine(version), StringComparison.OrdinalIgnoreCase)),
                $"the app to run on the private .NET ({runtime})", 10_000);
            var connected = _t.Pattern("已连接 · {0}");
            WaitForStatus(MainWindow, "首页", s => connected.IsMatch(s), 60_000);
            Expect(!Has(Uia.Snapshot(MainWindow()), T("立即重启")), "首页", $"「{T("立即重启")}」 before anything happened to the runtime");

            // The update: a newer patch version next to it, and what an uninstall can remove under a running app.
            CopyDirectory(runtime, Path.Combine(root, "shared", NetCoreApp, newer));
            File.Delete(Path.Combine(runtime, NetCoreApp + ".deps.json"));
            var removed = new List<string>();
            foreach (var dll in new[] { "System.Net.Http.dll", "System.Net.Security.dll", "System.Net.Http.Json.dll" })
            {
                try { File.Delete(Path.Combine(runtime, dll)); removed.Add(dll); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _report.Note(Where("update"), $"{dll} is in use: kept"); }
            }
            _report.Pass(Where("update"), $"{newer} added; {version} lost its deps.json and {string.Join(", ", removed)}");
            if (!removed.Contains("System.Net.Http.dll"))
                throw new StepFailed("System.Net.Http.dll is loaded already: 「检查更新」 can't show a failed load");

            // 「检查更新」: System.Net.Http can't be loaded; the update row says why, and the watch finds the runtime gone.
            Navigate("关于");
            Uia.Invoke(Button("检查更新"));
            string status = T(".NET 已更新，请重启 HomePodCast 后再试");
            WaitUntil(() => Has(Uia.Snapshot(MainWindow()), status), $"「{status}」 in 设置 → 软件更新", 20_000, fail: false);
            Expect(Has(Uia.Snapshot(MainWindow()), status), "设置", $"the update row doesn't say 「{status}」 after the failed check");
            WaitUntil(() => ReadLog().Any(l => l.Contains("runtime: the .NET runtime this copy started on", StringComparison.Ordinal)),
                "the watch to find the runtime gone", 20_000);
            var found = ReadLog().First(l => l.Contains("runtime: the .NET runtime this copy started on", StringComparison.Ordinal));
            Expect(found.Contains("System.Net.Http", StringComparison.Ordinal), "log", $"the failed load isn't named: {found}");

            // 首页: the notice, with the interruption (it is streaming), and 「立即重启」.
            Navigate("首页");
            string notice = T(".NET 已更新，HomePodCast 需要重新启动才能继续正常工作。重启时声音会中断几秒，然后自动重新连接。");
            WaitUntil(() => Has(Uia.Snapshot(MainWindow()), notice), "the restart notice on 首页", 10_000);
            var tree = Uia.Snapshot(MainWindow());
            Dump(tree, "home-runtime-removed");
            Shoot(MainWindow(), "home-runtime-removed", "首页 after .NET was replaced: the restart notice");
            Layout(tree, "首页 (runtime removed)");
            var restart = Button("立即重启");
            _report.Pass(Where("首页"), $"「{notice}」 with 「{T("立即重启")}」");

            // 立即重启: this copy quits, a new one starts by itself.
            var old = App;
            Uia.Invoke(restart);
            if (!old.WaitForExit(30_000)) throw new StepFailed("the app did not quit for the restart within 30 s");
            Expect(old.ExitCode == 0, "restart", $"the old copy exited with {old.ExitCode}");
            _app = WaitForRestartedApp(old.Id, 30_000);
            _restarted = true;
            var main = WaitWindow(MainWindow, "the restarted copy's main window (it had its window open)", 30_000);
            Thread.Sleep(1500);
            HandlePrompts(); // the startup firewall check again
            var (again, button) = WaitForStatus(MainWindow, "首页 (restarted)", s => connected.IsMatch(s), 60_000);
            Expect(button == T("断开"), "首页 (restarted)", $"the button reads 「{button}」 while 「{again}」 (expected 「{T("断开")}」)");
            _report.Pass(Where("restart"), $"back by itself, {again}");
            var log = ReadLog();
            Expect(log.Any(l => l.Contains("restarting because a .NET update removed the runtime", StringComparison.Ordinal)
                                && l.Contains("--resume=connected", StringComparison.Ordinal) && !l.Contains("--tray", StringComparison.Ordinal)),
                "log", "no 「restarting … --resume=connected」 line (without --tray: the window was open)");
            Expect(log.Any(l => l.Contains("restarted by 「立即重启」: connecting again", StringComparison.Ordinal)), "log",
                "the new copy doesn't say it was restarted to connect again");
            Expect(log.Any(l => l.Contains(RuntimeLine(newer), StringComparison.OrdinalIgnoreCase)), "log",
                $"the new copy doesn't run on the newer version ({Path.Combine(root, "shared", NetCoreApp, newer)})");
            tree = Uia.Snapshot(main);
            Expect(!Has(tree, T("立即重启")), "首页 (restarted)", "the restart notice is still there");
            Shoot(main, "home-restarted", "首页 after 立即重启: connected again, no notice");

            QuitRestarted();
            CheckLog(idle: false);
            var sessions = _fake.Requests.Count(r => r == "SETUP stream [encrypted]");
            Expect(sessions >= 2, "fake HomePod", $"{sessions} stream session(s): the restarted copy did not connect again");
            Expect(_fake.Requests.Contains("TEARDOWN [encrypted]"), "fake HomePod", "the restart sent no TEARDOWN");
            foreach (var error in _fake.Errors) _report.Fail(Where("fake HomePod"), error);
        }
        finally
        {
            // The restarted copy was not started by the walker: not through its Process object, by its exe path.
            if (_app != null && _restarted) _app = null;
            else StopApp();
            KillLeftovers();
            for (int i = 0; i < 20 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception) { Thread.Sleep(500); } // the app's files may stay locked a moment after it exited
            }
        }
    }

    /// <summary>
    /// A private .NET in <paramref name="root"/>: the hosts, and the runtime and Windows Desktop runtime this runner runs
    /// (the newest patches, the ones the app would pick too). Returns that runtime's version and a newer patch's name.
    /// </summary>
    private (string Version, string Newer) CopyDotnet(string root)
    {
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\', '/');
        string version = Path.GetFileName(runtime);
        string dotnet = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)!)!)!;
        string desktop = Path.GetDirectoryName(typeof(Form).Assembly.Location)!;
        if (!Version.TryParse(version.Split('-')[0], out var v)) throw new StepFailed($"the runtime folder {runtime} has no version");
        string newer = $"{v.Major}.{v.Minor}.{v.Build + 100}";
        var watch = Stopwatch.StartNew();
        CopyDirectory(Path.Combine(dotnet, "host"), Path.Combine(root, "host"));
        CopyDirectory(runtime, Path.Combine(root, "shared", NetCoreApp, version));
        CopyDirectory(desktop, Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App", Path.GetFileName(desktop.TrimEnd('\\'))));
        _report.Note(Where(), $"private .NET {version} (Windows Desktop {Path.GetFileName(desktop.TrimEnd('\\'))}) from {dotnet} in {root}, " +
                              $"{watch.ElapsedMilliseconds} ms");
        return (version, newer);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>
    /// <see cref="_app"/> is the copy 立即重启 started, found by name: its Process object can't tell its exit code
    /// (InvalidOperationException, the first CI run), so it is quit and waited for by its id and window.
    /// </summary>
    private bool _restarted;

    /// <summary>Quits the restarted copy from the tray flyout's 退出 and waits until its process is gone.</summary>
    private void QuitRestarted()
    {
        int pid = App.Id;
        var main = MainWindow();
        try
        {
            var flyout = OpenFlyout();
            var quit = Find(Uia.Snapshot(flyout), n => n.Type == "Button" && n.Says(T("退出"))) ?? throw new StepFailed($"no 「{T("退出")}」 in the flyout");
            Uia.Invoke(quit);
        }
        catch (Exception ex) when (ex is StepFailed or InvalidOperationException or System.Windows.Automation.ElementNotAvailableException)
        {
            _report.Fail(Where("quit"), $"quitting the restarted copy from the flyout failed: {ex.Message}");
            if (main != IntPtr.Zero) Native.PostMessage(main, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Running(pid) && DateTime.UtcNow < deadline) Thread.Sleep(200);
        Expect(!Running(pid), "quit", "the restarted copy did not exit within 30 s");
        _app = null; // gone (or killed with the leftovers): nothing here asks its Process object any more
    }

    /// <summary>A process with this id still runs (by the process list, not by a Process object's exit state).</summary>
    private static bool Running(int pid)
    {
        bool found = false;
        foreach (var p in Process.GetProcesses())
        {
            found |= p.Id == pid;
            p.Dispose();
        }
        return found;
    }

    /// <summary>The copy 立即重启 started: the same exe, another process.</summary>
    private Process WaitForRestartedApp(int oldId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_exe)))
            {
                bool ours = false;
                try { ours = p.Id != oldId && string.Equals(p.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { } // exited meanwhile, or not ours to read
                if (ours) return p;
                p.Dispose();
            }
            Thread.Sleep(200);
        }
        throw new StepFailed($"no new copy of {_exe} within {timeoutMs / 1000} s after 立即重启");
    }
}
