using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Picture alignment: local player delay controls and the optional synchronized viewing module.</summary>
internal sealed class ViewingPage : ScrollPage
{
    private readonly TrayApp _app;
    private readonly ToggleSwitch _playerSync = new();
    private readonly TextBlock _module = Ui.Note();
    private readonly PasswordBox _vlcPassword = new(L.T("VLC 网页接口密码（自动读取失败时填写）"));
    private readonly FluentButton _vlcSave = new(L.T("保存"));
    private readonly FluentButton _vlcClear = new(L.T("清除"), ButtonKind.Subtle);
    private readonly SettingRow _vlcPasswordRow;

    public ViewingPage(TrayApp app) : this(app, Players.VideoWindowModule.Offered) { }

    /// <summary>The module is omitted in packaged builds; player controls and navigation remain available.</summary>
    internal ViewingPage(TrayApp app, bool moduleOffered) : base(L.T("音画同步"))
    {
        _app = app;
        TitleText!.Wrap = true;

        var players = Ui.Section(L.T("本地播放器"));
        var playerSync = new SettingRow(Glyph.Video, L.T("影视场景自动调整播放器"),
            L.T("「影视」场景推流时，把 mpv、VLC 的声音提前，让画面对上 HomePod；换场景、断开或退出时改回原来的值。"), _playerSync);
        var copyMpv = new FluentButton(L.T("复制"), ButtonKind.Secondary, Glyph.Copy) { AccessibleName = L.T("复制 mpv.conf 这一行") };
        var mpvRow = new SettingRow(Glyph.None, "mpv",
            L.F("在 mpv.conf（通常在 %APPDATA%\\mpv）里加这一行，然后重启 mpv：\n{0}", Players.MpvIpc.ConfigLine), copyMpv);
        var vlcRow = new SettingRow(Glyph.None, "VLC",
            L.T("工具 → 偏好设置 → 显示设置选「全部」→ 界面 → 主界面：勾选「Web」；再到 主界面 → Lua 设置密码，然后重启 VLC。" +
                "HomePodCast 从 VLC 的设置里读取这个密码，只用来连接本机的 VLC。"), null);
        // Typed only when VLC's own password cannot be read or is refused; the line under it says which one is used.
        _vlcPasswordRow = new SettingRow(Glyph.None, L.T("VLC 网页接口密码（自动读取失败时填写）"), "",
            Ui.Row(8, null, _vlcPassword, _vlcSave, _vlcClear));
        var manualRow = new SettingRow(Glyph.None, "PotPlayer · MPC-HC · MPC-BE",
            L.T("没有可用的接口，请按首页显示的数值手动设置：PotPlayer 按 Shift+< / Shift+>；MPC-HC、MPC-BE 按小键盘 + / −，或在选项里设「音频时间偏移」。"), null);

        Content.Controls.AddRange([players, playerSync, mpvRow, vlcRow, _vlcPasswordRow, manualRow]);
        Content.GapBefore[players] = 12;
        foreach (var row in new Control[] { playerSync, mpvRow, vlcRow, _vlcPasswordRow, manualRow }) Content.GapBefore[row] = 4;

        _playerSync.Toggled += (_, _) => _app.SetMoviePlayerSync(_playerSync.Checked);
        copyMpv.Click += (_, _) =>
        {
            try { Clipboard.SetText(Players.MpvIpc.ConfigLine); }
            catch (Exception ex) { Log.Warn($"clipboard: {ex.Message}"); }
        };
        _vlcPassword.PasswordChanged += (_, _) => ShowVlcPasswordButtons();
        _vlcPassword.Submitted += (_, _) => SaveVlcPassword();
        _vlcSave.Click += (_, _) => SaveVlcPassword();
        _vlcClear.Click += (_, _) => ClearVlcPassword();
        if (moduleOffered)
        {
            var viewing = Ui.Section(L.T("同步观影窗口（实验性）")); // experimental since 1.1
            var open = new FluentButton(L.T("打开"), ButtonKind.Primary);
            var module = new FluentButton(L.T("选择模块…"));
            Content.Controls.AddRange([viewing,
                Ui.Note(L.T("延迟显示选定窗口，跟随 HomePod 音频；请关闭播放器和浏览器的其他同步补偿，避免重复调整。")),
                Ui.Row(8, null, module, open), _module]);
            Content.GapBefore[viewing] = 20;
            module.Click += (_, _) => { app.ChooseVideoWindowModule(FindForm()); PageShown(); };
            open.Click += (_, _) => { app.OpenVideoWindow(FindForm()); PageShown(); };
        }
        ReloadConfiguration();
    }

    public void ReloadConfiguration()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        _playerSync.Checked = _app.Config.MoviePlayerSync;
        _module.Text = _app.Config.VideoWindowModuleDirectory ?? L.T("未设置");
        ShowPlayers();
    }

    public override void PageShown() => ReloadConfiguration();

    /// <summary>Which VLC password is in use, or that VLC refused it (never the password); the buttons' state.</summary>
    public void ShowPlayers()
    {
        var (text, problem) = Players.PlayerText.VlcPasswordStatus(_app.PlayerStatuses, _app.HasVlcPassword);
        var desc = _vlcPasswordRow.DescriptionText;
        desc.Text = text;
        desc.Role = problem ? TextRole.Critical : TextRole.Secondary;
        ShowVlcPasswordButtons();
    }

    private void ShowVlcPasswordButtons()
    {
        bool typed = _vlcPassword.Password.Length > 0;
        _vlcSave.Enabled = typed;
        _vlcClear.Enabled = typed || _app.HasVlcPassword;
    }

    /// <summary>Encrypts and saves what was typed, then empties the box (the saved password is never shown again).</summary>
    private void SaveVlcPassword()
    {
        if (_vlcPassword.Password.Length == 0) return;
        try
        {
            _app.SetVlcPassword(_vlcPassword.Password);
        }
        catch (Exception ex)
        {
            Log.Warn($"players: saving the VLC password failed: {ex.Message}");
            MessageBox.Show(FindForm(), L.F("保存密码失败：{0}", ex.Message), "HomePodCast");
            return;
        }
        _vlcPassword.Clear();
        ShowPlayers();
    }

    private void ClearVlcPassword()
    {
        _vlcPassword.Clear();
        if (_app.HasVlcPassword)
        {
            try
            {
                _app.SetVlcPassword(null);
            }
            catch (Exception ex)
            {
                Log.Warn($"players: clearing the VLC password failed: {ex.Message}");
            }
        }
        ShowPlayers();
    }

    /// <summary>A password typed but not saved is not kept once the page is left.</summary>
    public override void PageHidden()
    {
        _vlcPassword.Clear();
        ShowVlcPasswordButtons();
    }

}

internal sealed class SyncPage : ScrollPage
{
    public SyncPage(TrayApp app) : base(L.T("音画同步测试"))
    {
        var start = new FluentButton(L.T("开始"), ButtonKind.Primary);
        Content.Controls.AddRange([Ui.Note(L.T("听「咔」声、看闪光，测出声音比画面晚多少。需要先连接音箱。")), Ui.Row(8, null, start)]);
        // Browsing a page must never start a tone; the existing measurement starts only on this explicit action.
        start.Click += (_, _) => app.RunSyncTest(FindForm()!);
    }
}
