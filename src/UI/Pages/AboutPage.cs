using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.UI.Pages;

/// <summary>Version, updates and local maintenance, separate from preferences.</summary>
internal sealed class AboutPage : ScrollPage
{
    public const string RepositoryUrl = "https://github.com/qiyinxi/HomePodCast";
    private readonly TrayApp _app;
    // 更新: one row for the version, the check's status and its actions (UpdateStatusRow), the channel, automatic checks
    private static readonly UpdateChannel[] Channels = [UpdateChannel.Stable, UpdateChannel.Beta];
    private readonly UpdateStatusRow _updateRow;
    private readonly Segmented _channel = new(L.T("正式版"), L.T("测试版"));
    private readonly SettingRow _channelRow;
    private readonly ToggleSwitch _autoUpdate = new();
    private readonly SettingRow _autoUpdateRow;

    public AboutPage(TrayApp app) : base(L.T("关于"))
    {
        _app = app;
        // Not "更新": that is the button's text, and other languages need two words (Updates / Update).
        var updates = Ui.Section(L.T("软件更新"));
        _updateRow = new UpdateStatusRow(() => _app.CheckForUpdates(), () =>
        {
            if (_app.UpdateState.Downloading) _app.CancelUpdateDownload();
            else _app.InstallUpdate(FindForm());
        }, _app.ShowReleaseNotes, _app.SkipUpdate);
        _channelRow = new SettingRow(Glyph.Flag, L.T("更新渠道"),
            L.T("正式版只提示正式版本；测试版也提示测试版本（比正式版先拿到新功能，可能不太稳定），哪个新提示哪个。"), _channel);
        _autoUpdateRow = new SettingRow(Glyph.Recent, L.T("自动检查更新"), "", _autoUpdate);

        var version = typeof(AboutPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        var link = new FluentButton("GitHub", ButtonKind.Link, Glyph.OpenInNew) { AccessibleName = RepositoryUrl };
        var aboutRow = new SettingRow(Glyph.Info, $"HomePodCast {version}", RepositoryUrl, link);

        var license = new FluentButton("GPL-3.0", ButtonKind.Link, Glyph.OpenInNew);
        var licenseRow = new SettingRow(Glyph.Info, L.T("许可证"), "GNU General Public License v3.0", license);
        var logs = new FluentButton(L.T("打开日志文件夹"), ButtonKind.Secondary, Glyph.Folder);
        var diagnostic = new FluentButton(L.T("导出诊断"));
        Content.Controls.AddRange([aboutRow, licenseRow, updates, _updateRow.Row, _channelRow, _autoUpdateRow,
            Ui.Section(L.T("日志")), new SettingRow(Glyph.Folder, L.T("日志"),
                L.F("报告问题时请附上这个文件夹里的 {0}：\n{1}", AppConfig.LogFileName, AppEnvironment.LogFolder), logs),
            new SettingRow(Glyph.Info, L.T("诊断报告"),
                L.T("导出脱敏日志、设备和网络统计；不包含配置文件、密码或录音，也不会上传。"), diagnostic)]);
        link.Click += (_, _) => AppEnvironment.OpenUri(RepositoryUrl);
        license.Click += (_, _) => AppEnvironment.OpenUri(RepositoryUrl + "/blob/dev/LICENSE");
        logs.Click += (_, _) => AppEnvironment.OpenLogFolder();
        diagnostic.Click += (_, _) => _app.ExportDiagnostics(FindForm());
        _channel.AccessibleName = L.T("更新渠道");
        _channel.SelectionChangeCommitted += (_, _) =>
        {
            if (_channel.SelectedIndex >= 0) _app.SetUpdateChannel(Channels[_channel.SelectedIndex]);
        };
        _autoUpdate.Toggled += (_, _) => _app.SetAutoUpdateCheck(_autoUpdate.Checked);
        ShowUpdate();
    }

    /// <summary>
    /// 更新: the version's row (its status, and 检查更新 or, while a new version is offered, 更新 / 查看说明 / 跳过此版本;
    /// UpdateStatusRow), the channel, and automatic checks (or why this build has none).
    /// </summary>
    public void ShowUpdate()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        var u = _app.UpdateState;
        _updateRow.Show(u);
        // The Microsoft Store version: the Store updates it; nothing here contacts GitHub.
        _channelRow.Collapsed = _autoUpdateRow.Collapsed = u.Store;
        if (u.Store) return;

        _channel.SelectedIndex = Array.IndexOf(Channels, _app.UpdateChannel);
        _autoUpdate.Checked = _app.Config.AutoUpdateCheck;
        var autoDesc = _autoUpdateRow.DescriptionText;
        if (_app.NoAutomaticUpdates != null)
        {
            autoDesc.Text = L.F("这是开发版本（{0}），不会自动检查更新；「检查更新」仍然可以用。", ReleaseVersion.CurrentText);
            autoDesc.Role = TextRole.Caution;
        }
        else
        {
            autoDesc.Text = L.T("有新版本时弹出通知提醒你，不会自动安装。启动约 30 秒后检查一次，之后每天一次；关闭时只在你点「检查更新」时联网。");
            autoDesc.Role = TextRole.Secondary;
        }
    }

    public override void PageShown() => ShowUpdate();
}
