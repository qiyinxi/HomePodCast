using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// A .NET update that replaces the runtime under the running app (2026-10-13): the watch that notices it (with a fake
/// File.Exists), its never running for a self-contained copy, and the restart command, with what the new copy gets.
/// </summary>
public class RuntimeWatchTests
{
    private const string Shared = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\";
    private const string DepsFile = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\Microsoft.NETCore.App.deps.json";
    private const string Http = "System.Net.Http, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";

    /// <summary>File.Exists for the deps file, switched by the test; counts the checks.</summary>
    private sealed class FakeRuntime
    {
        private int _checks;
        public volatile bool There = true;
        public int Checks => Volatile.Read(ref _checks);
        public readonly ConcurrentQueue<string> Asked = new();

        public bool Exists(string path)
        {
            Asked.Enqueue(path);
            Interlocked.Increment(ref _checks);
            return There;
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\qiyinxi.HomePodCast_1.1.0.0_x64__21rkx5qfj0b6t\")] // the Store package
    [InlineData(@"D:\Apps\HomePodCast\")] // a self-contained build
    [InlineData("")]
    public void Nothing_is_watched_for_a_self_contained_copy(string runtimeFolder)
    {
        var fake = new FakeRuntime { There = false };
        int removed = 0;
        Assert.False(AppEnvironment.IsSharedRuntime(runtimeFolder));
        Assert.Null(RuntimeWatch.For(runtimeFolder, fake.Exists, () => removed++));
        Assert.Equal(0, fake.Checks);
        Assert.Equal(0, removed);
    }

    [Theory]
    [InlineData(Shared)]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12")]
    [InlineData(@"D:\dotnet\shared\microsoft.netcore.app\10.0.13\")]
    public void A_shared_runtime_is_watched(string runtimeFolder) => Assert.True(AppEnvironment.IsSharedRuntime(runtimeFolder));

    [Fact]
    public void A_check_finds_the_runtime_gone_once_and_then_stops()
    {
        var fake = new FakeRuntime();
        int removed = 0;
        using var watch = RuntimeWatch.For(Shared, fake.Exists, () => removed++)!;
        Assert.False(watch.Check("test"));
        Assert.Equal(1, fake.Checks);
        Assert.Equal(DepsFile, Assert.Single(fake.Asked));
        Assert.False(watch.Removed);

        fake.There = false; // the update has removed it
        Assert.True(watch.Check("test"));
        Assert.True(watch.Removed);
        Assert.Equal(1, removed);

        // Stopped: no more file system checks, never a second notice.
        Assert.True(watch.Check("test"));
        watch.CheckSoon("test");
        watch.Saw(new FileNotFoundException("", Http));
        Thread.Sleep(200);
        Assert.Equal(2, fake.Checks);
        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task The_timer_checks_by_itself_and_stops_once_it_found_it()
    {
        var fake = new FakeRuntime();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var watch = RuntimeWatch.For(Shared, fake.Exists, () => { Interlocked.Increment(ref calls); removed.TrySetResult(); },
            TimeSpan.FromMilliseconds(30))!;
        watch.Start(watchExceptions: false);
        await WaitUntil(() => fake.Checks >= 2, "the start check and a timer check");
        Assert.False(removed.Task.IsCompleted);

        fake.There = false;
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int checks = fake.Checks;
        await Task.Delay(300); // ten periods
        Assert.Equal(checks, fake.Checks);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_wake_from_sleep_checks_at_once()
    {
        var fake = new FakeRuntime { There = false };
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = RuntimeWatch.For(Shared, fake.Exists, () => removed.TrySetResult())!; // the 5-minute timer is not started
        watch.CheckSoon("after a wake from sleep");
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(watch.Removed);
    }

    [Fact]
    public async Task An_assembly_that_could_not_be_loaded_is_logged_and_starts_a_check()
    {
        var fake = new FakeRuntime();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new ConcurrentQueue<string>();
        void Collect(string line) { if (line.Contains("runtime: ")) lines.Enqueue(line); }
        Log.Written += Collect;
        try
        {
            using var watch = RuntimeWatch.For(Shared, fake.Exists, () => removed.TrySetResult())!;
            // The runtime is still there: the assembly is named in the log, once.
            watch.Saw(new FileNotFoundException("", Http));
            await WaitUntil(() => lines.Any(l => l.Contains(Http)), "the failed load in the log");
            watch.Saw(new FileNotFoundException("", Http));
            await WaitUntil(() => fake.Checks >= 2, "a second check");
            Assert.Single(lines, l => l.Contains(Http) && l.Contains("still there"));
            Assert.False(removed.Task.IsCompleted);

            // A missing file that is no assembly: no check at all.
            int checks = fake.Checks;
            watch.Saw(new FileNotFoundException("Could not find file.", @"C:\Users\x\AppData\Roaming\HomePodCast\config.json"));
            watch.Saw(new InvalidOperationException("x"));
            await Task.Delay(200);
            Assert.Equal(checks, fake.Checks);

            // Gone: the warning says which failure found it.
            fake.There = false;
            watch.Saw(new FileLoadException("", "System.Text.Encoding.CodePages, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => lines.Any(l => l.Contains("WARN") && l.Contains("has been removed")), "the warning");
            var warning = lines.Single(l => l.Contains("has been removed"));
            Assert.Contains(@"(C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12)", warning);
            Assert.Contains("System.Text.Encoding.CodePages", warning);
        }
        finally
        {
            Log.Written -= Collect;
        }
    }

    [Theory]
    [InlineData("FileNotFound", Http, Http)]
    [InlineData("FileLoad", Http, Http)]
    [InlineData("FileNotFound", @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\System.Net.Http.dll",
        @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\System.Net.Http.dll")]
    [InlineData("FileNotFound", @"C:\Users\x\AppData\Roaming\HomePodCast\config.json", null)]
    [InlineData("FileNotFound", null, null)]
    [InlineData("Other", Http, null)]
    public void Only_a_missing_assembly_counts(string kind, string? fileName, string? expected)
    {
        Exception ex = kind switch
        {
            "FileNotFound" => new FileNotFoundException("", fileName),
            "FileLoad" => new FileLoadException("", fileName),
            _ => new InvalidOperationException(fileName),
        };
        Assert.Equal(expected, RuntimeWatch.MissingAssembly(ex));
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var sw = System.Diagnostics.Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(10); await Task.Delay(10))
            if (condition()) return;
        Assert.Fail($"timed out waiting for {what}");
    }
}

/// <summary>What 「立即重启」 (and a new UI language) starts: the command, the arguments, and that the start itself works.</summary>
public class AppRestartTests
{
    private const string Exe = @"C:\Program Files\HomePodCast\HomePodCast.exe";
    private const string FamilyName = "qiyinxi.HomePodCast_21rkx5qfj0b6t";

    [Theory]
    [InlineData(false, null, "")] // a new UI language: as before, the settings decide
    [InlineData(false, true, "--resume=connected")]
    [InlineData(false, false, "--resume=idle")]
    [InlineData(true, true, "--tray --resume=connected")]
    [InlineData(true, false, "--tray --resume=idle")]
    public void The_new_copy_comes_back_into_the_tray_and_connected_as_before(bool tray, bool? connected, string arguments)
    {
        var request = new AppRestart.Request("test", tray, connected);
        Assert.Equal((Exe, arguments), AppRestart.Command(request, null, Exe, @"C:\Windows"));
        // The arguments the new copy reads back.
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(tray, args.Contains("--tray"));
        Assert.Equal(connected, AppRestart.Resume(args));
    }

    [Fact]
    public void The_Store_version_starts_again_through_its_package()
    {
        // Explorer passes nothing on; the package's runtime is its own, so it never restarts for a replaced one.
        Assert.Equal((@"C:\Windows\explorer.exe", $@"shell:AppsFolder\{FamilyName}!HomePodCast"),
            AppRestart.Command(new AppRestart.Request("test", true, true), FamilyName, @"C:\Program Files\WindowsApps\x\HomePodCast.exe", @"C:\Windows"));
    }

    [Fact]
    public void An_ordinary_start_resumes_nothing()
    {
        Assert.Null(AppRestart.Resume([]));
        Assert.Null(AppRestart.Resume(["--tray"]));
        Assert.Null(AppRestart.Resume(["--resume=later"]));
        Assert.True(AppRestart.Resume([@"C:\x\HomePodCast.exe", "--tray", "--resume=connected"]));
        Assert.Equal($"\"{Exe}\" --tray", AppRestart.CommandLine(Exe, "--tray"));
        Assert.Equal($"\"{Exe}\"", AppRestart.CommandLine(Exe, ""));
    }

    /// <summary>
    /// The start itself, with cmd.exe in place of the app: it gets the arguments and this process's environment (the
    /// tests run as the test profile "unit-tests", which the new copy has to keep), started directly and with the shell
    /// as its parent (what an elevated copy does).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_new_process_gets_the_arguments_and_this_environment(bool asShellUser)
    {
        string folder = Path.Combine(Path.GetTempPath(), "HomePodCast-restart-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "env out.txt");
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Assert.Null(AppRestart.Start(cmd, $"/d /c set HOMEPODCAST_PROFILE> \"{output}\"", asShellUser));
            string text = "";
            for (var sw = System.Diagnostics.Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(20); await Task.Delay(50))
            {
                try { text = File.Exists(output) ? await File.ReadAllTextAsync(output) : ""; }
                catch (IOException) { continue; } // cmd is still writing it
                if (text.Contains('\n')) break;
            }
            Assert.Equal("HOMEPODCAST_PROFILE=unit-tests", text.Trim());
        }
        finally
        {
            for (int i = 0; i < 20; i++)
            {
                try { Directory.Delete(folder, recursive: true); break; }
                catch (IOException) { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public void A_start_that_fails_says_why()
    {
        var error = AppRestart.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe"), "", asShellUser: false);
        Assert.Equal("CreateProcess failed with error 2", error); // ERROR_FILE_NOT_FOUND
    }
}

// The language table is process-global: these tests switch it, so they never run next to another test.
[CollectionDefinition("RuntimeRestartNotice UI", DisableParallelization = true)]
public sealed class RuntimeRestartNoticeUiCollection { }

/// <summary>
/// 首页's restart notice, built off-screen in a page as wide as 首页 at the window's minimum and default sizes: hidden
/// until the runtime is gone, its text (with the interruption while streaming), 「立即重启」 calling the restart and
/// nothing else, and the button whole beside the text in Chinese and German.
/// </summary>
[Collection("RuntimeRestartNotice UI")]
public class RuntimeRestartNoticeTests
{
    [Fact]
    public void The_notice_appears_once_the_runtime_is_gone_and_its_button_restarts() => OnSta(L.Source, () =>
    {
        int restarts = 0;
        using var host = new Host(784, out var notice, () => restarts++);
        Show(host, notice, removed: false, streaming: true);
        Assert.True(notice.Card.Collapsed);
        Assert.False(notice.Card.Visible);
        int top = host.Below.Top; // nothing above 首页's cards

        Show(host, notice, removed: true, streaming: false);
        Assert.False(notice.Card.Collapsed);
        Assert.True(notice.Card.Visible);
        Assert.Equal(".NET 已更新，HomePodCast 需要重新启动才能继续正常工作。", notice.Text.Text);
        Assert.Equal(TextRole.Caution, notice.Text.Role);
        Assert.Equal("立即重启", notice.Restart.Text);
        Assert.Equal(ButtonKind.Primary, notice.Restart.Kind);
        Assert.Equal(top, notice.Card.Top); // at the top of the page
        Assert.True(notice.Card.Bottom < host.Below.Top, "the notice is above the cards");
        Assert.Equal(0, restarts); // never without the click

        Show(host, notice, removed: true, streaming: true);
        Assert.Equal(".NET 已更新，HomePodCast 需要重新启动才能继续正常工作。重启时声音会中断几秒，然后自动重新连接。", notice.Text.Text);

        notice.Restart.PerformClick();
        Assert.Equal(1, restarts);
    });

    [Theory]
    [InlineData("zh-CN", 784)] // 首页 at the window's default width (1000)
    [InlineData("zh-CN", 456)] // and at its minimum, beside the navigation
    [InlineData("de", 784)]
    [InlineData("de", 456)]
    [InlineData("en", 456)]
    public void The_button_stays_whole_beside_the_text(string language, int pageWidth) => OnSta(language, () =>
    {
        using var host = new Host(pageWidth, out var notice, () => { });
        foreach (bool streaming in new[] { false, true })
        {
            Show(host, notice, removed: true, streaming);
            var card = notice.Card;
            Rectangle InCard(Control c) => card.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
            var button = InCard(notice.Restart);
            var text = InCard(notice.Text);
            string where = $"{language} {pageWidth}{(streaming ? ", streaming" : "")}";
            Assert.True(card.ClientRectangle.Contains(button), $"{where}: 「{notice.Restart.Text}」 {button} sticks out of {card.ClientRectangle}");
            Assert.True(card.ClientRectangle.Contains(text), $"{where}: the text sticks out");
            Assert.True(notice.Restart.Width >= notice.Restart.GetPreferredSize(Size.Empty).Width, $"{where}: 「{notice.Restart.Text}」 is squeezed");
            Assert.False(button.IntersectsWith(text), $"{where}: the button covers the text");
            Assert.True(notice.Text.Height >= notice.Text.GetPreferredSize(new Size(notice.Text.Width, 0)).Height, $"{where}: the text is cut off");
            Assert.True(card.Bottom < host.Below.Top, $"{where}: the notice overlaps the cards");
            Snapshot(host, $"runtime-notice-{language}-{pageWidth}{(streaming ? "-streaming" : "")}");
        }
    });

    /// <summary>The window drawn into a PNG when HPC_UI_SNAPSHOT_DIR is set (to look at the notice without the app).</summary>
    private static void Snapshot(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("HPC_UI_SNAPSHOT_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, $"{name}-dpi{form.DeviceDpi}.png"));
    }

    private static void Show(Host host, RuntimeRestartNotice notice, bool removed, bool streaming)
    {
        notice.Show(removed, streaming);
        host.Page.PerformLayout();
        Application.DoEvents();
    }

    /// <summary>An invisible window with a page like 首页: the notice, then a card standing for the speaker and scene cards.</summary>
    private sealed class Host : Form
    {
        public Host(int pageWidth, out RuntimeRestartNotice notice, Action restart)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Opacity = 0;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Page = new ScrollPage();
            notice = new RuntimeRestartNotice(restart);
            Below = Ui.Card(Ui.Header(L.T("音量")));
            Page.Content.Controls.AddRange([notice.Card, Below]);
            Controls.Add(Page);
            Show();
            int width = Theme.Dp(pageWidth, DeviceDpi), height = Theme.Dp(420, DeviceDpi);
            ClientSize = new Size(width, height);
            Page.SetBounds(0, 0, width, height);
            Page.PerformLayout();
            Application.DoEvents();
        }

        public ScrollPage Page { get; }

        /// <summary>The first card under the notice.</summary>
        public Card Below { get; }
    }

    private static void OnSta(string language, Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string previous = L.Language;
            try { L.Use(language); body(); }
            catch (Exception ex) { error = ex; }
            finally { L.Use(previous); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The notice test did not finish.");
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
