using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Preferences: appearance, startup, takeover, night disconnect, volume and hotkeys.</summary>
internal sealed class SettingsPage : ScrollPage
{
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
    private readonly FluentButton _hotkeys = new(L.T("快捷键…"));
    private readonly SettingRow _hotkeysRow;
    public void ShowTheme() => _theme.SelectedIndex = Array.IndexOf(ThemeModes, Theme.Normalize(_app.Config.Theme));

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

        Content.Controls.AddRange([general, language, theme, autostart, autoconnect, _homeTheaterRow, night, _nightWindowRow, _nightIdleRow,
            sound, cap, _volumeKeysRow, _hotkeysRow]);
        foreach (var header in new Control[] { general, sound }) Content.GapBefore[header] = 20;
        foreach (var row in new Control[] { language, theme, autostart, autoconnect, _homeTheaterRow, night, _nightWindowRow, _nightIdleRow,
            cap, _volumeKeysRow, _hotkeysRow }) Content.GapBefore[row] = 4;
        Content.GapBefore[general] = 12;

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
        _hotkeys.Click += (_, _) => _app.ShowHotkeys(FindForm()!);
        Load();
    }

    /// <summary>The cap was changed here (the volume sliders follow).</summary>
    public event Action? CapChanged;
    /// <summary>A profile was applied: everything here, and 音箱信息 for the speaker it may have switched to.</summary>
    public void ReloadConfiguration()
    {
        _capWait.Stop();
        Load();
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
        ShowVolumeKeys();
        ShowSoundOptions();
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
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
                                          "连接时两边对齐到较小的那个。Windows 静音时 HomePod 也静音；如果默认输出是电脑扬声器、又只想用 HomePod 听，请打开首页的「仅 HomePod 输出」。"),
        VolumeKeyMode.WhenWindowsMuted => L.T("只在 Windows 静音或音量为 0 时，音量键改调 HomePod，Windows 保持静音（建议用静音，而不是 0%）。"),
        _ => L.T("音量键只调 Windows，不影响 HomePod。"),
    };

    /// <summary>
    /// The selected mode and what it does; without a Windows output device, that the keys go to the HomePod; with
    /// 仅 HomePod 输出 on, that 「HomePod 跟随 Windows 音量」 is handled as 「推流时控制 HomePod」.
    /// </summary>
    public void ShowVolumeKeys()
    {
        var mode = _app.Config.VolumeKeys;
        var effective = _app.EffectiveVolumeKeys;
        if (!_volumeKeys.DroppedDown) _volumeKeys.SelectedIndex = Array.IndexOf(VolumeKeyModes, mode);
        var desc = _volumeKeysRow.DescriptionText;
        bool fallback = effective is VolumeKeyMode.FollowWindows or VolumeKeyMode.WhenWindowsMuted && !_app.OutputDevicePresent;
        desc.Text = fallback ? L.T("没有可用的输出设备：推流时音量键直接控制 HomePod。")
            : effective != mode ? L.T("已打开「仅 HomePod 输出」：推流时按「推流时控制 HomePod」处理，音量 +、− 和静音键只调 HomePod；不推流时照常调 Windows。")
            : VolumeKeyModeDescription(mode) +
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

    public override void PageShown()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        ShowTheme();
        ShowAutostart(); // the Store version's startup task may have been switched in Windows' settings meanwhile
        ShowSoundOptions();
        ShowVolumeKeys(); // the capture device may have been changed on 混音器
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _capWait.Dispose();
        base.Dispose(disposing);
    }
}
