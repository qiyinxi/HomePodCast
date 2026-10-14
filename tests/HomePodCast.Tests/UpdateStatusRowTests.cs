using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;
using HomePodCast.Update;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

// The language table is process-global: these tests switch it, so they never run next to another test.
[CollectionDefinition("UpdateStatusRow UI", DisableParallelization = true)]
public sealed class UpdateStatusRowUiCollection { }

/// <summary>
/// 设置 → 软件更新's one row (UpdateStatusRow) in every state, built off-screen in a page as wide as at the window's
/// minimum and default sizes, with made-up update states: what it shows and holds, that nothing appears or goes away
/// below it, and that its buttons stay whole and apart in Chinese and German. The GUI smoke test only ever sees the
/// 「检查更新」 state (a release build never offers an update without GitHub), so the offered one is checked here.
/// </summary>
[Collection("UpdateStatusRow UI")]
public class UpdateStatusRowTests(ITestOutputHelper output)
{
    private static readonly Release Next = new(ReleaseVersion.Parse("1.1.0-beta.1")!, "v1.1.0-beta.1",
        "https://github.com/qiyinxi/HomePodCast/releases/tag/v1.1.0-beta.1", true,
        new ReleaseAsset("HomePodCast-1.1.0-beta.1-x64.msi",
            "https://github.com/qiyinxi/HomePodCast/releases/download/v1.1.0-beta.1/HomePodCast-1.1.0-beta.1-x64.msi", 100,
            "475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e"));

    private const string MsiHint = "「更新」会下载安装包、用 GitHub 提供的 SHA-256 校验，然后退出 HomePodCast 安装（Windows 会请求管理员权限），装好后自动重新启动。";
    private const string PortableHint = "便携版：「更新」会打开发布页，请下载新的 zip 解压后替换。";

    private static UpdateView View(string status, TextRole role = TextRole.Secondary, Release? available = null, bool checking = false,
        bool downloading = false, bool? msi = true, bool store = false) =>
        new(status, role, available, checking, downloading, Banner: false, msi, store);

    /// <summary>The states in the order a user meets them: checked, found, downloading, cancelled, skipped.</summary>
    private static (string Name, UpdateView View)[] States() =>
    [
        ("last check", View(L.F("上次检查：{0}", "2026-10-14 09:30"))),
        ("checking", View(L.T("正在检查更新…"), checking: true)),
        ("failed", View(L.T("检查失败，请稍后再试（详情见日志）"), TextRole.Critical)),
        ("found", View(L.F("发现新版本 {0}", Next.Version), TextRole.Accent, Next)),
        ("found, checking again", View(L.F("发现新版本 {0}", Next.Version), TextRole.Accent, Next, checking: true)),
        ("downloading", View(L.F("正在下载 {0}… {1}%", Next.Version, 45), available: Next, downloading: true)),
        ("cancelled", View(L.T("已取消下载"), available: Next)),
        ("skipped", View(L.F("已跳过 {0}，有更新的版本时会再提示", Next.Version))),
    ];

    [Fact]
    public void Each_state_shows_its_status_and_only_its_own_buttons_in_the_one_row() => OnSta(L.Source, () =>
    {
        int checks = 0, installs = 0, notes = 0, skips = 0;
        using var host = new Host(784, out var row, () => checks++, () => installs++, () => notes++, () => skips++);

        Show(host, row, View("上次检查：2026-10-14 09:30"));
        Assert.Equal("当前版本 " + ReleaseVersion.CurrentText, row.Row.TitleText.Text);
        Assert.Equal("上次检查：2026-10-14 09:30", row.Row.DescriptionText.Text);
        Assert.Equal(["检查更新"], Shown(row));
        Assert.True(row.Check.Enabled);
        row.Check.PerformClick();
        Assert.Equal(1, checks);

        Show(host, row, View("正在检查更新…", checking: true));
        Assert.Equal(["检查更新"], Shown(row));
        Assert.False(row.Check.Enabled);

        Show(host, row, View("检查失败，请稍后再试（详情见日志）", TextRole.Critical));
        Assert.Equal(TextRole.Critical, row.Row.DescriptionText.Role);
        Assert.True(row.Check.Enabled);

        // Found: 更新 / 查看说明 / 跳过此版本 in place of 检查更新, in the same row; what 更新 does is its tooltip.
        Show(host, row, View("发现新版本 1.1.0-beta.1", TextRole.Accent, Next));
        Assert.Equal("发现新版本 1.1.0-beta.1", row.Row.DescriptionText.Text);
        Assert.Equal(TextRole.Accent, row.Row.DescriptionText.Role);
        Assert.Equal(["更新", "查看说明", "跳过此版本"], Shown(row));
        Assert.Equal(ButtonKind.Primary, row.Install.Kind);
        Assert.True(row.Install.Enabled && row.Notes.Enabled && row.Skip.Enabled);
        Assert.Equal(MsiHint, row.InstallTip);
        Assert.Equal(MsiHint, row.Install.AccessibleDescription);
        Assert.Same(row.Row, row.Install.Parent!.Parent); // the row's own buttons, not a row of their own
        row.Install.PerformClick();
        row.Notes.PerformClick();
        Assert.Equal((1, 1, 0), (installs, notes, skips));

        // A portable copy: 更新 opens the release page.
        Show(host, row, View("发现新版本 1.1.0-beta.1", TextRole.Accent, Next, msi: false));
        Assert.Equal(PortableHint, row.InstallTip);

        // An automatic check under way: 更新 and 跳过此版本 wait for it.
        Show(host, row, View("发现新版本 1.1.0-beta.1", TextRole.Accent, Next, checking: true));
        Assert.False(row.Install.Enabled);
        Assert.False(row.Skip.Enabled);
        Assert.True(row.Notes.Enabled);

        // Downloading: 更新 turns into 取消 (the same button, so the caller cancels), 跳过此版本 is off.
        Show(host, row, View("正在下载 1.1.0-beta.1… 45%", available: Next, downloading: true));
        Assert.Equal(["取消", "查看说明", "跳过此版本"], Shown(row));
        Assert.Equal(ButtonKind.Secondary, row.Install.Kind);
        Assert.True(row.Install.Enabled);
        Assert.False(row.Skip.Enabled);
        Assert.True(row.Notes.Enabled);
        Assert.Null(row.InstallTip);
        row.Install.PerformClick();
        Assert.Equal(2, installs);

        Show(host, row, View("已取消下载", available: Next));
        Assert.Equal(["更新", "查看说明", "跳过此版本"], Shown(row));
        Assert.Equal(ButtonKind.Primary, row.Install.Kind);
        row.Skip.PerformClick();
        Assert.Equal(1, skips);

        // Skipped (or the channel changed): back to 检查更新.
        Show(host, row, View("已跳过 1.1.0-beta.1，有更新的版本时会再提示"));
        Assert.Equal(["检查更新"], Shown(row));
        Assert.True(row.Check.Enabled);

        // The Microsoft Store version: its status line and no buttons at all.
        Show(host, row, View("Microsoft Store 版由 Microsoft Store 负责更新。", store: true));
        Assert.Equal("Microsoft Store 版由 Microsoft Store 负责更新。", row.Row.DescriptionText.Text);
        Assert.Empty(Shown(row));
        Assert.True(row.Buttons.Collapsed);
        AssertLaidOut(host, row, "store");
    });

    [Theory]
    [InlineData("zh-CN", 784)] // the page at the window's default width (1000)
    [InlineData("zh-CN", 456)] // and at its minimum (700), with the navigation at a third of it
    [InlineData("de", 784)]
    [InlineData("de", 456)]
    [InlineData("en", 456)]
    public void The_buttons_stay_whole_inside_the_row_and_the_rows_below_only_move_with_its_height(string language, int pageWidth) =>
        OnSta(language, () =>
        {
            using var host = new Host(pageWidth, out var row, () => { }, () => { }, () => { }, () => { });
            var heights = new List<(string Name, UpdateView View, int Height)>();
            foreach (var (name, view) in States())
            {
                Show(host, row, view);
                AssertLaidOut(host, row, $"{language} {pageWidth}, {name}");
                heights.Add((name, view, row.Row.Height));
                Snapshot(host, $"update-row-{language}-{pageWidth}-{name.Replace(", ", "-").Replace(' ', '-')}");
            }
            output.WriteLine($"{language}, page {pageWidth} (dpi {host.DeviceDpi}): row height " +
                             string.Join(", ", heights.Select(h => $"{h.Name} {h.Height}")));
            // 更新 → 取消 → 更新 (download, cancel) moves nothing: 取消 takes the room of 更新 even where it is shorter.
            var offered = heights.Where(h => h.View.Available != null).Select(h => h.Height).Distinct().ToList();
            Assert.True(offered.Count == 1, $"{language} {pageWidth}: the row changes height while an update is offered ({string.Join(", ", offered)})");
        });

    [Fact]
    public void At_the_default_width_in_Chinese_the_row_keeps_its_height_when_an_update_is_found() => OnSta(L.Source, () =>
    {
        using var host = new Host(784, out var row, () => { }, () => { }, () => { }, () => { });
        Show(host, row, View(L.T("已是最新版本"), TextRole.Success));
        int upToDate = row.Row.Height, below = host.Below.Top;
        foreach (var view in new[]
                 {
                     View(L.F("发现新版本 {0}", Next.Version), TextRole.Accent, Next),
                     View(L.F("正在下载 {0}… {1}%", Next.Version, 45), available: Next, downloading: true),
                 })
        {
            Show(host, row, view);
            Assert.Equal(upToDate, row.Row.Height);
            Assert.Equal(below, host.Below.Top);
            Assert.True(row.Buttons.Height < 2 * row.Install.Height, "the buttons are on one line beside the text");
        }
    });

    // ---------------------------------------------------------------- helpers

    private static List<string> Shown(UpdateStatusRow row) =>
        row.Buttons.Collapsed ? [] : row.Buttons.Controls.Cast<Control>().Where(c => !LayoutPanel.IsCollapsed(c)).Select(c => c.Text).ToList();

    private static void Show(Host host, UpdateStatusRow row, UpdateView view)
    {
        row.Show(view);
        host.Page.PerformLayout();
        Application.DoEvents();
    }

    /// <summary>
    /// Every shown button at its full width, inside the row and apart from the others and from the texts; the texts
    /// whole; the channel row right under it (nothing in between).
    /// </summary>
    private static void AssertLaidOut(Host host, UpdateStatusRow row, string where)
    {
        var r = row.Row;
        Rectangle InRow(Control c) => r.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
        var inner = r.ClientRectangle;
        foreach (var text in new[] { r.TitleText, r.DescriptionText })
        {
            Assert.True(inner.Contains(InRow(text)), $"{where}: 「{text.Text}」 sticks out of the row");
            Assert.True(text.Height >= text.GetPreferredSize(new Size(text.Width, 0)).Height, $"{where}: 「{text.Text}」 is cut off");
        }
        var buttons = row.Buttons.Collapsed ? [] : row.Buttons.Controls.Cast<FluentButton>().Where(b => !LayoutPanel.IsCollapsed(b)).ToList();
        foreach (var b in buttons)
        {
            var box = InRow(b);
            Assert.True(inner.Contains(box), $"{where}: 「{b.Text}」 {box} sticks out of the row {inner}");
            Assert.True(b.Width >= b.GetPreferredSize(Size.Empty).Width, $"{where}: 「{b.Text}」 is squeezed to {b.Width}px");
            foreach (var text in new[] { r.TitleText, r.DescriptionText })
                Assert.False(box.IntersectsWith(InRow(text)), $"{where}: 「{b.Text}」 covers 「{text.Text}」");
            foreach (var other in buttons.Where(o => o != b))
                Assert.False(box.IntersectsWith(InRow(other)), $"{where}: 「{b.Text}」 and 「{other.Text}」 overlap");
        }
        var page = host.Page.Content;
        int gap = Theme.Dp(4, page.DeviceDpi);
        Assert.Equal(r.Bottom + gap, host.Below.Top);
        Assert.Equal(page.Controls.GetChildIndex(r) + 1, page.Controls.GetChildIndex(host.Below));
    }

    /// <summary>An invisible window with a page like 设置's update section: header, the row, 更新渠道 and 自动检查更新.</summary>
    private sealed class Host : Form
    {
        public Host(int pageWidth, out UpdateStatusRow row, Action check, Action install, Action notes, Action skip)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Opacity = 0;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Page = new ScrollPage();
            row = new UpdateStatusRow(check, install, notes, skip);
            var header = Ui.Section(L.T("软件更新"));
            Below = new SettingRow(Glyph.Flag, L.T("更新渠道"), "", new Segmented(L.T("正式版"), L.T("测试版")));
            var auto = new SettingRow(Glyph.Recent, L.T("自动检查更新"), "", new ToggleSwitch());
            Page.Content.Controls.AddRange([header, row.Row, Below, auto]);
            foreach (var r in new Control[] { row.Row, Below, auto }) Page.Content.GapBefore[r] = 4;
            Controls.Add(Page);
            Show();
            int width = Theme.Dp(pageWidth, DeviceDpi), height = Theme.Dp(420, DeviceDpi);
            ClientSize = new Size(width, height);
            Page.SetBounds(0, 0, width, height);
            Page.PerformLayout();
            Application.DoEvents();
        }

        public ScrollPage Page { get; }

        /// <summary>The row under the update row (更新渠道).</summary>
        public SettingRow Below { get; }
    }

    /// <summary>The window drawn into a PNG when HPC_UI_SNAPSHOT_DIR is set (to look at the states without the app).</summary>
    private static void Snapshot(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("HPC_UI_SNAPSHOT_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, $"{name}-dpi{form.DeviceDpi}.png"));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The update row test did not finish.");
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
