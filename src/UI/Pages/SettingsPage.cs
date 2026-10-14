using System.Diagnostics;
using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.UI.Pages;

/// <summary>设置: language and theme, startup, letting the speaker go at night, volume cap, volume keys, hotkeys, local players in the 影视 scene
/// (how to switch on their interfaces, and a VLC password for when VLC's own cannot be read or is refused), the
/// experimental multi-speaker dialog, wireless cinema and synchronized viewing window, the A/V sync test, updates (check,
/// install, channel, automatic checks), and about.</summary>
internal sealed class SettingsPage : ScrollPage
{
    public const string RepositoryUrl = "https://github.com/qiyinxi/HomePodCast";

    private static readonly string[] ThemeModes = [Theme.Auto, Theme.Light, Theme.Dark];

    /// <summary>The choices of 「键盘音量键」, in the order shown.</summary>
    private static readonly VolumeKeyMode[] VolumeKeyModes =
        [VolumeKeyMode.WhileStreaming, VolumeKeyMode.FollowWindows, VolumeKeyMode.WhenWindowsMuted, VolumeKeyMode.Off];

    private readonly TrayApp _app;
    private readonly FluentComboBox _language = new();
    private readonly FluentComboBox _theme = new();
    private readonly ToggleSwitch _autostart = new();
    private readonly SettingRow _autostartRow;
    private readonly ToggleSwitch _autoconnect = new();
    private static readonly HomeTheaterMode[] HomeTheaterModes = [HomeTheaterMode.TakeOver, HomeTheaterMode.Ask];
    private readonly Segmented _homeTheater = new(L.T("临时接管"), L.T("先询问"));
    private readonly SettingRow _homeTheaterRow;
    private readonly ToggleSwitch _night = new();
    private readonly FluentComboBox _nightFrom = new(), _nightUntil = new(), _nightIdle = new();
    private readonly SettingRow _nightWindowRow, _nightIdleRow;
    private readonly FluentSlider _cap = Ui.Slider(VolumeLimit.MinCap, 100, 5, 10);
    private readonly TextBlock _capValue = new("", TextStyle.Body) { Align = HorizontalAlignment.Right };
    private readonly TimerDebounce _capWait = new(250);
    private readonly FluentComboBox _volumeKeys = new();
    private readonly SettingRow _volumeKeysRow;
    private readonly ToggleSwitch _playerSync = new();
    private readonly FluentButton _hotkeys = new(L.T("快捷键…"));
    private readonly SettingRow _hotkeysRow;
    private readonly PasswordBox _vlcPassword = new(L.T("VLC 网页接口密码（自动读取失败时填写）"));
    private readonly FluentButton _vlcSave = new(L.T("保存"));
    private readonly FluentButton _vlcClear = new(L.T("清除"), ButtonKind.Subtle);
    private readonly SettingRow _vlcPasswordRow;

    // 更新: one row for the version, the check's status and its actions (UpdateStatusRow), the channel, automatic checks
    private static readonly UpdateChannel[] Channels = [UpdateChannel.Stable, UpdateChannel.Beta];
    private readonly UpdateStatusRow _updateRow;
    private readonly Segmented _channel = new(L.T("正式版"), L.T("测试版"));
    private readonly SettingRow _channelRow;
    private readonly ToggleSwitch _autoUpdate = new();
    private readonly SettingRow _autoUpdateRow;

    public void RefreshReceiverInfo() => _receiverConnection?.RefreshSelection();

    public SettingsPage(TrayApp app) : base(L.T("设置"))
    {
        _app = app;

        foreach (var (_, name) in LanguageMenu.Choices.Prepend((L.Auto, LanguageMenu.AutoName))) _language.Items.Add(name);
        _theme.Items.AddRange([L.T("跟随 Windows"), L.T("浅色"), L.T("深色")]);
        _volumeKeys.Items.AddRange(VolumeKeyModes.Select(VolumeKeyModeName));

        var general = Ui.Section(L.T("常规"));
        var language = new SettingRow(Glyph.Globe, L.Language == "en" ? L.T("语言") : L.T("语言") + " / Language",
            L.T("切换后重新启动 HomePodCast 生效。"), _language);
        var theme = new SettingRow(Glyph.Appearance, L.T("外观"), L.T("浅色或深色；默认跟随 Windows。"), _theme);
        var autostart = _autostartRow = new SettingRow(Glyph.Power, L.T("开机自动启动"), L.T(AutostartText), _autostart);
        var autoconnect = new SettingRow(Glyph.Sync, L.T("启动后自动连接"), L.T("启动时连接上次用的音箱。"), _autoconnect);
        // Experimental until it has been tried on a real Apple TV (AGENTS.md: a hardware-untested label, not a version
        // one): whether the Apple TV takes its HomePod back after we disconnect is still unverified.
        _homeTheater.AccessibleName = L.T("HomePod 当 Apple TV 家庭影院音箱时（实验性）");
        _homeTheaterRow = new SettingRow(Glyph.Video, L.T("HomePod 当 Apple TV 家庭影院音箱时（实验性）"), "", _homeTheater);
        // 夜间自动断开 (NightDisconnectPolicy); the window and the idle time show while it is on.
        var night = new SettingRow(Glyph.Moon, L.T("夜间自动断开（实验性）"), // experimental since 1.1
            L.T("关屏不关机时用：在下面的时段里，键盘鼠标和声音都闲置一段时间后断开 HomePod，让其他设备可以用；" +
                "时段结束时自动重新连接，和启动时自动连接一样，不会抢正在播放的设备。"), _night);
        foreach (var time in NightDisconnectPolicy.TimeChoices)
        {
            _nightFrom.Items.Add(NightDisconnectPolicy.Format(time));
            _nightUntil.Items.Add(NightDisconnectPolicy.Format(time));
        }
        _nightFrom.AccessibleName = L.T("开始时间");
        _nightUntil.AccessibleName = L.T("结束时间");
        _nightWindowRow = new SettingRow(Glyph.None, L.T("时段"), "",
            Ui.Row(8, null, _nightFrom, new TextBlock(L.T("到")), _nightUntil));
        _nightIdle.Items.AddRange(NightDisconnectPolicy.IdleChoices.Select(m => L.F("{0} 分钟", m)));
        _nightIdleRow = new SettingRow(Glyph.None, L.T("闲置多久后断开"),
            L.T("这段时间里键盘鼠标没有操作，HomePod 也一直没有声音。"), _nightIdle);

        var sound = Ui.Section(L.T("音量与快捷键"));
        _cap.AccessibleName = L.T("音量上限"); // the row is named after its title; the slider in it too
        var capRow = Ui.Row(10, _cap, _cap, _capValue);
        capRow.FixedWidths[_cap] = 180;
        capRow.FixedWidths[_capValue] = 44;
        var cap = new SettingRow(Glyph.Volume, L.T("音量上限"),
            L.T("HomePod 音量不会超过这个值（连接、重连、快捷键都一样）。100 = 不限制"), capRow);
        _volumeKeysRow = new SettingRow(Glyph.Keyboard, L.T("键盘音量键"), "", _volumeKeys);
        _hotkeysRow = new SettingRow(Glyph.Keyboard, L.T("全局快捷键"), "", _hotkeys);

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

        var tools = Ui.Section(L.T("工具"));
        var profiles = new FluentButton(L.T("管理配置"));
        var profilesRow = new SettingRow(Glyph.Settings, L.T("使用配置"),
            L.T("保存音箱、采集设备、程序分流、音效、音量上限和夜间模式，之后一键切换。切换后麦克风关闭。"), profiles);
        var diagnostic = new FluentButton(L.T("导出诊断"));
        var diagnosticRow = new SettingRow(Glyph.Info, L.T("诊断报告"),
            L.T("导出脱敏日志、设备和网络统计；不包含配置文件、密码或录音，也不会上传。"), diagnostic);
        var group = new FluentButton(L.T("打开"));
        var cinema = new FluentButton(L.T("打开"));
        // experimental since 1.1; never tested on real hardware (one HomePod here), so the label stays until it has been
        var cinemaRow = new SettingRow(Glyph.Devices, L.T("无线影院（实验性）"),
            L.T("本地 5.1 或 7.1 影音文件的每个声道送到一台 HomePod，可以用麦克风测量各音箱的延迟。"), cinema);
        var groupRow = new SettingRow(Glyph.Devices, L.T("多音箱（实验性）"),
            L.T("立体声对、多房间同步、分声道和每台音箱的音量偏移。"), group);
        var sync = new FluentButton(L.T("开始"));
        var syncRow = new SettingRow(Glyph.Gauge, L.T("音画同步测试"),
            L.T("听「咔」声、看闪光，测出声音比画面晚多少。需要先连接音箱。"), sync);
        // The folder Explorer has to open: for the Store version, the package's own storage (AppEnvironment.LogFolder).
        var logs = new FluentButton(L.T("打开日志文件夹"), ButtonKind.Secondary, Glyph.Folder);
        var logsRow = new SettingRow(Glyph.Folder, L.T("日志"),
            L.F("报告问题时请附上这个文件夹里的 {0}：\n{1}", AppConfig.LogFileName, AppEnvironment.LogFolder), logs);

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

        var about = Ui.Section(L.T("关于"));
        var version = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        var link = new FluentButton("GitHub", ButtonKind.Link, Glyph.OpenInNew) { AccessibleName = RepositoryUrl };
        var aboutRow = new SettingRow(Glyph.Info, $"HomePodCast {version}", RepositoryUrl, link);

        Content.Controls.AddRange([general, language, theme, autostart, autoconnect, _homeTheaterRow, night, _nightWindowRow, _nightIdleRow,
            sound, cap, _volumeKeysRow, _hotkeysRow, players, playerSync, mpvRow, vlcRow, _vlcPasswordRow, manualRow, tools, profilesRow, diagnosticRow, groupRow, cinemaRow, syncRow, logsRow,
            updates, _updateRow.Row, _channelRow, _autoUpdateRow, about, aboutRow]);
        foreach (var header in new Control[] { general, sound, players, tools, updates, about }) Content.GapBefore[header] = 20;
        foreach (var row in new Control[] { language, theme, autostart, autoconnect, _homeTheaterRow, night, _nightWindowRow, _nightIdleRow,
                     cap, _volumeKeysRow, _hotkeysRow,
                     playerSync, mpvRow, vlcRow, _vlcPasswordRow, manualRow, profilesRow, diagnosticRow, groupRow, cinemaRow, syncRow, logsRow,
                     _updateRow.Row, _channelRow, _autoUpdateRow, aboutRow })
            Content.GapBefore[row] = 4;
        Content.GapBefore[general] = 12;
        AddVideoWindow(before: syncRow);
        var manualAirPlay = new FluentButton(L.T("添加"));
        var manualAirPlayRow = new SettingRow(Glyph.Devices, L.T("手动添加 AirPlay 设备（实验性）"), // experimental since 1.1
            L.T("发现不到设备时，填写 IP 地址和端口。"), manualAirPlay);
        Content.Controls.Add(manualAirPlayRow);
        Content.Controls.SetChildIndex(manualAirPlayRow, Content.Controls.GetChildIndex(updates));
        Content.GapBefore[manualAirPlayRow] = 4;
        manualAirPlay.Click += (_, _) => _app.AddManualAirPlayDevice(FindForm());
        _receiverConnection = new ReceiverConnectionPanel(_app);
        Content.Controls.Add(_receiverConnection);
        Content.Controls.SetChildIndex(_receiverConnection, Content.Controls.GetChildIndex(updates));
        _receiverConnection.RefreshSelection();
        AddSpeakerInfo(before: updates);
        AddPairings(before: updates);

        _language.SelectionChangeCommitted += (_, _) =>
        {
            int i = _language.SelectedIndex;
            string value = i <= 0 ? L.Auto : LanguageMenu.Choices[i - 1].Value;
            LanguageMenu.Choose(_app.Config, value, _app.Quit);
        };
        _theme.SelectionChangeCommitted += (_, _) =>
        {
            var mode = ThemeModes[Math.Max(0, _theme.SelectedIndex)];
            _app.Config.Theme = mode;
            _app.Config.Save();
            Theme.Mode = mode;
        };
        _autostart.Toggled += (_, _) =>
        {
            if (Autostart.Method == AutostartMethod.StartupTask)
            {
                SetStartupTask(_autostart.Checked);
                return;
            }
            try { Autostart.Enabled = _autostart.Checked; }
            catch (Exception ex) { MessageBox.Show(FindForm(), L.F("设置开机启动失败：{0}", ex.Message), "HomePodCast"); }
            _autostart.Checked = SafeAutostart();
        };
        logs.Click += (_, _) => AppEnvironment.OpenLogFolder();
        _autoconnect.Toggled += (_, _) =>
        {
            _app.Config.AutoConnect = _autoconnect.Checked;
            _app.Config.Save();
        };
        _homeTheater.SelectionChangeCommitted += (_, _) =>
        {
            if (_homeTheater.SelectedIndex < 0) return;
            _app.SetHomeTheaterMode(HomeTheaterModes[_homeTheater.SelectedIndex]);
            ShowHomeTheater();
        };
        _night.Toggled += (_, _) => SetNight();
        _nightFrom.SelectionChangeCommitted += (_, _) => SetNight();
        _nightUntil.SelectionChangeCommitted += (_, _) => SetNight();
        _nightIdle.SelectionChangeCommitted += (_, _) => SetNight();
        _cap.Scroll += (_, _) =>
        {
            _capValue.Text = $"{_cap.Value}%";
            _capWait.Restart();
        };
        _capWait.Tick += () =>
        {
            _capWait.Stop();
            _app.SetVolumeCap(_cap.Value);
            CapChanged?.Invoke();
        };
        _volumeKeys.SelectionChangeCommitted += (_, _) =>
        {
            if (_volumeKeys.SelectedIndex < 0) return;
            _app.SetVolumeKeyMode(VolumeKeyModes[_volumeKeys.SelectedIndex]);
            ShowVolumeKeys();
        };
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
        _hotkeys.Click += (_, _) => _app.ShowHotkeys(FindForm()!);
        group.Click += (_, _) => GroupForm.ShowFor(_app, FindForm()!);
        cinema.Click += (_, _) => CinemaForm.ShowFor(_app, FindForm());
        profiles.Click += (_, _) => _app.ShowProfiles(FindForm()!);
        diagnostic.Click += (_, _) => _app.ExportDiagnostics(FindForm());
        sync.Click += (_, _) => _app.RunSyncTest(FindForm()!);
        link.Click += (_, _) => OpenRepository();
        _channel.AccessibleName = L.T("更新渠道");
        _channel.SelectionChangeCommitted += (_, _) =>
        {
            if (_channel.SelectedIndex >= 0) _app.SetUpdateChannel(Channels[_channel.SelectedIndex]);
        };
        _autoUpdate.Toggled += (_, _) => _app.SetAutoUpdateCheck(_autoUpdate.Checked);

        Load();
    }

    /// <summary>The cap was changed here (the volume sliders follow).</summary>
    public event Action? CapChanged;
    /// <summary>A profile was applied: everything here, and 音箱信息 for the speaker it may have switched to.</summary>
    public void ReloadConfiguration()
    {
        _capWait.Stop();
        Load();
        _receiverConnection?.RefreshSelection();
        _speakerInfo?.SelectionChanged();
    }
    public void FlushPendingSettings()
    {
        if (!_capWait.Enabled) return;
        _capWait.Stop();
        _app.SetVolumeCap(_cap.Value);
    }

    private const string AutostartText = "登录 Windows 后在托盘里启动。";

    private static bool SafeAutostart()
    {
        try { return Autostart.Enabled; } catch { return false; }
    }

    /// <summary>The switch shows the autostart state: the Run value, or (the Store version) the startup task, read off the UI thread.</summary>
    private void ShowAutostart()
    {
        if (Autostart.Method == AutostartMethod.StartupTask) ShowStartupTask();
        else _autostart.Checked = SafeAutostart();
    }

    /// <summary>Counts the reads and switches of the startup task: an answer that a later one overtook is not shown.</summary>
    private int _startupTaskRequest;

    /// <summary>A switch of the startup task is under way (the switch is disabled meanwhile, and nothing is read).</summary>
    private bool _settingStartupTask;

    private async void ShowStartupTask()
    {
        if (_settingStartupTask) return;
        int request = ++_startupTaskRequest;
        try
        {
            var state = await Task.Run(() => StartupTaskApi.GetState(AppEnvironment.StartupTaskId));
            if (!IsDisposed && request == _startupTaskRequest) ShowStartupTask(Autostart.ViewOf(state));
        }
        catch (Exception ex)
        {
            Log.Warn($"autostart: the startup task can't be read: {ex.Message}");
            if (IsDisposed || request != _startupTaskRequest) return;
            _autostart.Checked = false;
            ShowAutostartNote(L.T("读不到开机启动的状态。也可以在 Windows 设置 → 应用 → 启动 里打开或关闭 HomePodCast。"), TextRole.Caution);
        }
    }

    private void ShowStartupTask(AutostartView view)
    {
        _autostart.Checked = view.On;
        _autostart.Enabled = view.CanChange;
        switch (view.Note)
        {
            case AutostartNote.OffInWindows:
                ShowAutostartNote(L.T("已在 Windows 设置的“启动”页面里关闭，只能在那里重新打开。"), TextRole.Caution);
                break;
            case AutostartNote.Policy:
                ShowAutostartNote(L.T("开机启动由组织的策略管理。"), TextRole.Caution);
                break;
            default:
                ShowAutostartNote(L.T(AutostartText), TextRole.Secondary);
                break;
        }
    }

    private void ShowAutostartNote(string text, TextRole role)
    {
        _autostartRow.DescriptionText.Text = text;
        _autostartRow.DescriptionText.Role = role;
    }

    /// <summary>
    /// The Store version's switch: asks Windows to turn the startup task on or off (off the UI thread). Turned off in
    /// Windows' own settings, it can only be turned on there again: offer to open the page.
    /// </summary>
    private async void SetStartupTask(bool on)
    {
        _settingStartupTask = true;
        ++_startupTaskRequest; // a read still under way is out of date
        _autostart.Enabled = false;
        StartupTaskState state;
        try
        {
            state = await Task.Run(() => Autostart.SetStartupTask(on)); // logs the outcome, or the failure
        }
        catch (Exception ex)
        {
            _settingStartupTask = false;
            if (IsDisposed) return;
            _autostart.Enabled = true;
            _autostart.Checked = !on;
            MessageBox.Show(FindForm(), L.F("设置开机启动失败：{0}", ex.Message), "HomePodCast");
            return;
        }
        _settingStartupTask = false;
        if (IsDisposed) return;
        var view = Autostart.ViewOf(state);
        ShowStartupTask(view);
        if (on && !view.On && view.Note == AutostartNote.OffInWindows &&
            MessageBox.Show(FindForm(),
                L.T("HomePodCast 的开机启动在 Windows 设置里被关掉了，只能在那里重新打开。\n\n点「是」打开 Windows 设置的“启动”页面，然后打开 HomePodCast。"),
                L.T("开机自动启动"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            AppEnvironment.OpenUri("ms-settings:startupapps");
    }

    private void Load()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        var cfg = _app.Config;
        string current = LanguageMenu.Normalize(cfg.Language);
        _language.SelectedIndex = current == L.Auto ? 0 : Array.FindIndex(LanguageMenu.Choices, c => c.Value == current) + 1;
        _theme.SelectedIndex = Array.IndexOf(ThemeModes, Theme.Normalize(cfg.Theme));
        ShowAutostart();
        _autoconnect.Checked = cfg.AutoConnect;
        ShowHomeTheater();
        ShowNight();
        _playerSync.Checked = cfg.MoviePlayerSync;
        ShowVolumeKeys();
        ShowSoundOptions();
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
        ShowPlayers();
        ShowUpdate();
    }

    /// <summary>
    /// 更新: the version's row (its status, and 检查更新 or, while a new version is offered, 更新 / 查看说明 / 跳过此版本;
    /// UpdateStatusRow), the channel, and automatic checks (or why this build has none).
    /// </summary>
    public void ShowUpdate()
    {
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

    public void ShowSoundOptions()
    {
        if (_cap.IsDragging || _capWait.Enabled) return;
        _cap.Value = _app.Config.VolumeCapPercent;
        _capValue.Text = $"{_cap.Value}%";
    }

    /// <summary>「HomePod 当 Apple TV 家庭影院音箱时」: the selected choice and what it does.</summary>
    private void ShowHomeTheater()
    {
        var mode = _app.Config.HomeTheaterTakeover;
        _homeTheater.SelectedIndex = Array.IndexOf(HomeTheaterModes, mode);
        _homeTheaterRow.DescriptionText.Text = HomeTheaterModeDescription(mode);
    }

    /// <summary>
    /// What 临时接管 / 先询问 do. That the Apple TV connects again the next time it plays is how Apple's home theater is
    /// expected to behave, not yet seen on the user's own Apple TV (docs/device-test-checklist.zh-CN.md).
    /// </summary>
    internal static string HomeTheaterModeDescription(HomeTheaterMode mode) => mode switch
    {
        HomeTheaterMode.TakeOver => L.T("连接时直接从 Apple TV 接过 HomePod（启动时自动连接、点「连接」都一样），Apple TV 正在播放的声音会被打断。" +
                                        "HomePodCast 连着的时候，Apple TV 的声音不会从这台 HomePod 出来；断开后，Apple TV 下次播放时会重新连上它。"),
        _ => L.T("点「连接」时先问你；自动连接会一直等到 HomePod 空闲，而 Apple TV 通常一直连着它。"),
    };

    /// <summary>「夜间自动断开」: the switch, and while it is on the window and the idle time.</summary>
    private void ShowNight()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        var cfg = _app.Config;
        _night.Checked = cfg.NightDisconnect;
        _nightWindowRow.Collapsed = _nightIdleRow.Collapsed = !cfg.NightDisconnect;
        ShowNightTime(_nightFrom, cfg.NightDisconnectFrom);
        ShowNightTime(_nightUntil, cfg.NightDisconnectUntil);
        int idle = Array.IndexOf(NightDisconnectPolicy.IdleChoices, cfg.NightDisconnectIdleMinutes);
        _nightIdle.SelectedIndex = idle;
        _nightIdle.Placeholder = idle < 0 ? L.F("{0} 分钟", cfg.NightDisconnectIdleMinutes) : "";
        bool empty = cfg.NightDisconnectFrom == cfg.NightDisconnectUntil;
        _nightWindowRow.DescriptionText.Text = empty ? L.T("开始和结束时间相同，不会断开。") : "";
        _nightWindowRow.DescriptionText.Role = empty ? TextRole.Caution : TextRole.Secondary;
    }

    /// <summary>A time off the list (a hand-edited config) shows as the box's placeholder, and stays until another is picked.</summary>
    private static void ShowNightTime(FluentComboBox box, TimeOnly time)
    {
        int i = Array.IndexOf(NightDisconnectPolicy.TimeChoices, time);
        box.SelectedIndex = i;
        box.Placeholder = i < 0 ? NightDisconnectPolicy.Format(time) : "";
    }

    private void SetNight()
    {
        var cfg = _app.Config;
        TimeOnly Picked(FluentComboBox box, TimeOnly current) =>
            box.SelectedIndex >= 0 ? NightDisconnectPolicy.TimeChoices[box.SelectedIndex] : current;
        _app.SetNightDisconnect(_night.Checked, Picked(_nightFrom, cfg.NightDisconnectFrom), Picked(_nightUntil, cfg.NightDisconnectUntil),
            _nightIdle.SelectedIndex >= 0 ? NightDisconnectPolicy.IdleChoices[_nightIdle.SelectedIndex] : cfg.NightDisconnectIdleMinutes);
        ShowNight();
    }

    private static string VolumeKeyModeName(VolumeKeyMode mode) => mode switch
    {
        VolumeKeyMode.WhileStreaming => L.T("推流时控制 HomePod"),
        VolumeKeyMode.FollowWindows => L.T("HomePod 跟随 Windows 音量"),
        VolumeKeyMode.WhenWindowsMuted => L.T("仅在 Windows 静音时"),
        _ => L.T("关"),
    };

    private static string VolumeKeyModeDescription(VolumeKeyMode mode) => mode switch
    {
        VolumeKeyMode.WhileStreaming => L.T("推流时，音量 +、− 和静音键只调 HomePod，Windows 音量不变，屏幕下方会显示 HomePod 音量；不推流时照常调 Windows。"),
        VolumeKeyMode.FollowWindows => L.T("Windows 音量就是 HomePod 音量（Windows 100% 对应音量上限）：音量键照常调 Windows，在程序里调 HomePod 也会改 Windows 音量；" +
                                          "连接时两边对齐到较小的那个。Windows 静音时 HomePod 也静音；如果默认输出是电脑扬声器、又只想用 HomePod 听，请选「推流时控制 HomePod」。"),
        VolumeKeyMode.WhenWindowsMuted => L.T("只在 Windows 静音或音量为 0 时，音量键改调 HomePod，Windows 保持静音（建议用静音，而不是 0%）。"),
        _ => L.T("音量键只调 Windows，不影响 HomePod。"),
    };

    /// <summary>The selected mode and what it does; without a Windows output device, that the keys go to the HomePod.</summary>
    public void ShowVolumeKeys()
    {
        var mode = _app.Config.VolumeKeys;
        if (!_volumeKeys.DroppedDown) _volumeKeys.SelectedIndex = Array.IndexOf(VolumeKeyModes, mode);
        var desc = _volumeKeysRow.DescriptionText;
        bool fallback = mode is VolumeKeyMode.FollowWindows or VolumeKeyMode.WhenWindowsMuted && !_app.OutputDevicePresent;
        desc.Text = fallback ? L.T("没有可用的输出设备：推流时音量键直接控制 HomePod。") : VolumeKeyModeDescription(mode) +
            (mode == VolumeKeyMode.FollowWindows && _app.Routing.CaptureDeviceId != null ? "\n" + L.T(MixerPage.CaptureVolumeNote) : "");
        desc.Role = fallback ? TextRole.Caution : TextRole.Secondary;
    }

    /// <summary>Lists the hotkeys; turns red when one could not be registered (the dialog says which).</summary>
    public void ShowHotkeyStatus(int unavailable)
    {
        var desc = _hotkeysRow.DescriptionText;
        if (unavailable > 0)
        {
            desc.Text = L.F("{0} 个快捷键被其他程序占用，点开换一个", unavailable);
            desc.Role = TextRole.Critical;
            return;
        }
        var keys = Enum.GetValues<HotkeyAction>()
            .Select(a => (Action: a, Key: Hotkey.FromConfig(_app.Config, a)))
            .Where(x => x.Key != null)
            .Select(x => $"{HotkeysForm.ActionName(x.Action)}: {x.Key}")
            .ToList();
        desc.Text = keys.Count == 0 ? L.T("全局快捷键：HomePod 音量、静音、切换场景") : string.Join("\n", keys);
        desc.Role = TextRole.Secondary;
    }

    private void OpenRepository()
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"open {RepositoryUrl}: {ex.Message}"); }
    }

    public override void PageShown()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        ShowAutostart(); // the Store version's startup task may have been switched in Windows' settings meanwhile
        ShowSoundOptions();
        ShowVolumeKeys(); // the capture device may have been changed on 混音器
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
        ShowPlayers();
        _receiverConnection?.RefreshSelection();
        _speakerInfo?.PageShown();
        _pairings?.PageShown();
    }

    /// <summary>A password typed but not saved is not kept once the page is left.</summary>
    public override void PageHidden()
    {
        _vlcPassword.Clear();
        ShowVlcPasswordButtons();
        _speakerInfo?.PageHidden();
        _pairings?.PageHidden();
    }

    // ---------------------------------------------------------------- 音箱信息 (SpeakerInfoPanel.cs)

    private ReceiverConnectionPanel? _receiverConnection;
    private SpeakerInfoPanel? _speakerInfo;

    /// <summary>The 音箱信息 section, just above <paramref name="before"/>: the selected speakers' details, read when shown.</summary>
    private void AddSpeakerInfo(Control before)
    {
        var header = Ui.Section(L.T("音箱信息"));
        _speakerInfo = new SpeakerInfoPanel(_app);
        int at = Content.Controls.GetChildIndex(before);
        Content.Controls.Add(header);
        Content.Controls.Add(_speakerInfo);
        Content.Controls.SetChildIndex(header, at);
        Content.Controls.SetChildIndex(_speakerInfo, at + 1);
        Content.GapBefore[header] = 20;
        Content.GapBefore[_speakerInfo] = 4;
    }

    // ---------------------------------------------------------------- 已配对的电视 (PairingsPanel.cs)

    private PairingsPanel? _pairings;

    /// <summary>The paired TVs (Apple TV), just above <paramref name="before"/>; hidden while there are none.</summary>
    private void AddPairings(Control before)
    {
        var header = Ui.Section(L.T("已配对的电视"));
        _pairings = new PairingsPanel(_app, header);
        int at = Content.Controls.GetChildIndex(before);
        Content.Controls.Add(header);
        Content.Controls.Add(_pairings);
        Content.Controls.SetChildIndex(header, at);
        Content.Controls.SetChildIndex(_pairings, at + 1);
        Content.GapBefore[header] = 20;
        Content.GapBefore[_pairings] = 4;
    }

    // ---------------------------------------------------------------- 同步观影窗口 (TrayApp.VideoWindow.cs)

    /// <summary>
    /// The optional viewing window module, just above <paramref name="before"/>; not in the Microsoft Store version,
    /// where starting a separately downloaded exe is against Store policy 10.2.2 (VideoWindowModule.Offered).
    /// </summary>
    private void AddVideoWindow(Control before)
    {
        if (!Players.VideoWindowModule.Offered) return;
        var open = new FluentButton(L.T("打开"));
        var module = new FluentButton(L.T("选择模块…"));
        var row = new SettingRow(Glyph.Video, L.T("同步观影窗口（实验性）"), // experimental since 1.1
            L.T("延迟显示选定窗口，跟随 HomePod 音频；请关闭播放器和浏览器的其他同步补偿，避免重复调整。"),
            Ui.Row(8, null, module, open));
        Content.Controls.Add(row);
        Content.Controls.SetChildIndex(row, Content.Controls.GetChildIndex(before));
        Content.GapBefore[row] = 4;
        module.Click += (_, _) => _app.ChooseVideoWindowModule(FindForm());
        open.Click += (_, _) => _app.OpenVideoWindow(FindForm());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _capWait.Dispose();
        base.Dispose(disposing);
    }
}
