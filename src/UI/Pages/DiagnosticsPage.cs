using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Explicit troubleshooting actions, separate from playback controls and application information.</summary>
internal sealed class DiagnosticsPage : ScrollPage
{
    public DiagnosticsPage(TrayApp app) : this(app.RunSyncTest, AppEnvironment.OpenLogFolder, app.ExportDiagnostics) { }

    internal DiagnosticsPage(Action<IWin32Window> runSyncTest, Action openLogFolder,
        Action<IWin32Window?> exportDiagnostics) : base(L.T("诊断"))
    {
        TitleText!.Wrap = true;
        var sync = Ui.Section(L.T("音画同步测试"));
        var start = new FluentButton(L.T("开始"), ButtonKind.Primary);
        var logs = Ui.Section(L.T("日志"));
        var openLogs = new FluentButton(L.T("打开日志文件夹"), ButtonKind.Secondary, Glyph.Folder);
        var report = Ui.Section(L.T("诊断报告"));
        var export = new FluentButton(L.T("导出诊断"));
        // The raw log names speakers and contains addresses and paths; public reports use the existing redacted export.
        Content.Controls.AddRange([sync,
            Ui.Note(L.T("听「咔」声、看闪光，测出声音比画面晚多少。需要先连接音箱。")), Ui.Row(8, null, start),
            logs, Ui.Note(L.F("完整的原始日志，自己排查问题时看。里面有音箱名称和 IP 地址，公开报告问题请改用下面的诊断报告。{0} 在这个文件夹里：\n{1}",
                AppConfig.LogFileName, AppEnvironment.LogFolder)), Ui.Row(8, null, openLogs),
            report, Ui.Note(L.T("公开报告问题时附上这个：脱敏后的日志，加上设备和网络统计。不包含配置文件、密码或录音，也不会上传。")),
            Ui.Row(8, null, export)]);
        Content.GapBefore[sync] = 12;
        Content.GapBefore[logs] = Content.GapBefore[report] = 20;
        // Browsing this page must never start a tone, open Explorer or ask for an export destination.
        start.Click += (_, _) => runSyncTest(FindForm()!);
        openLogs.Click += (_, _) => openLogFolder();
        export.Click += (_, _) => exportDiagnostics(FindForm());
    }
}
