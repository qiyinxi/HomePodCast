using System.Diagnostics;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>设置: language and theme, startup, volume cap, volume keys, hotkeys, the experimental multi-speaker
/// dialog, the A/V sync test, and about.</summary>
internal sealed class SettingsPage : ScrollPage
{
    public const string RepositoryUrl = "https://github.com/qiyinxi/HomePodCast";

    private static readonly string[] ThemeModes = [Theme.Auto, Theme.Light, Theme.Dark];

    private readonly TrayApp _app;
    private readonly FluentComboBox _language = new();
    private readonly FluentComboBox _theme = new();
    private readonly ToggleSwitch _autostart = new();
    private readonly ToggleSwitch _autoconnect = new();
    private readonly FluentSlider _cap = Ui.Slider(VolumeLimit.MinCap, 100, 5, 10);
    private readonly TextBlock _capValue = new("", TextStyle.Body) { Align = HorizontalAlignment.Right };
    private readonly TimerDebounce _capWait = new(250);
    private readonly ToggleSwitch _forward = new();
    private readonly FluentButton _hotkeys = new(L.T("快捷键…"));
    private readonly SettingRow _hotkeysRow;

    public SettingsPage(TrayApp app) : base(L.T("设置"))
    {
        _app = app;

        foreach (var (_, name) in LanguageMenu.Choices.Prepend((L.Auto, L.T("自动（跟随系统）")))) _language.Items.Add(name);
        _theme.Items.AddRange([L.T("跟随 Windows"), L.T("浅色"), L.T("深色")]);

        var general = Ui.Section(L.T("常规"));
        var language = new SettingRow(Glyph.Globe, L.Language == "en" ? L.T("语言") : L.T("语言") + " / Language",
            L.T("切换后重新启动 HomePodCast 生效。"), _language);
        var theme = new SettingRow(Glyph.Appearance, L.T("外观"), L.T("浅色或深色；默认跟随 Windows。"), _theme);
        var autostart = new SettingRow(Glyph.Power, L.T("开机自动启动"), L.T("登录 Windows 后在托盘里启动。"), _autostart);
        var autoconnect = new SettingRow(Glyph.Sync, L.T("启动后自动连接"), L.T("启动时连接上次用的音箱。"), _autoconnect);

        var sound = Ui.Section(L.T("音量与快捷键"));
        var capRow = Ui.Row(10, _cap, _cap, _capValue);
        capRow.FixedWidths[_cap] = 180;
        capRow.FixedWidths[_capValue] = 44;
        var cap = new SettingRow(Glyph.Volume, L.T("音量上限"),
            L.T("HomePod 音量不会超过这个值（连接、重连、快捷键都一样）。100 = 不限制"), capRow);
        var forward = new SettingRow(Glyph.Keyboard, L.T("Windows 静音或音量为 0 时，键盘音量键调节 HomePod 音量"),
            L.T("建议把 Windows 设为静音（而不是 0%）：这样音量 +、− 和静音键都会转给 HomePod，Windows 保持静音。"), _forward);
        _hotkeysRow = new SettingRow(Glyph.Keyboard, L.T("全局快捷键"), "", _hotkeys);

        var tools = Ui.Section(L.T("工具"));
        var group = new FluentButton(L.T("打开"));
        var groupRow = new SettingRow(Glyph.Devices, L.T("多音箱（实验性）"),
            L.T("立体声对、多房间同步、分声道和每台音箱的音量偏移。"), group);
        var sync = new FluentButton(L.T("开始"));
        var syncRow = new SettingRow(Glyph.Gauge, L.T("音画同步测试"),
            L.T("听「咔」声、看闪光，测出声音比画面晚多少。需要先连接音箱。"), sync);

        var about = Ui.Section(L.T("关于"));
        var version = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        var link = new FluentButton("GitHub", ButtonKind.Link, Glyph.OpenInNew) { AccessibleName = RepositoryUrl };
        var aboutRow = new SettingRow(Glyph.Info, $"HomePodCast {version}", RepositoryUrl, link);

        Content.Controls.AddRange([general, language, theme, autostart, autoconnect, sound, cap, forward, _hotkeysRow,
            tools, groupRow, syncRow, about, aboutRow]);
        foreach (var header in new Control[] { general, sound, tools, about }) Content.GapBefore[header] = 20;
        foreach (var row in new Control[] { language, theme, autostart, autoconnect, cap, forward, _hotkeysRow, groupRow, syncRow, aboutRow })
            Content.GapBefore[row] = 4;
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
            try { Autostart.Enabled = _autostart.Checked; }
            catch (Exception ex) { MessageBox.Show(FindForm(), L.F("设置开机启动失败：{0}", ex.Message), "HomePodCast"); }
            _autostart.Checked = SafeAutostart();
        };
        _autoconnect.Toggled += (_, _) =>
        {
            _app.Config.AutoConnect = _autoconnect.Checked;
            _app.Config.Save();
        };
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
        _forward.Toggled += (_, _) => _app.SetForwardVolumeKeys(_forward.Checked);
        _hotkeys.Click += (_, _) => _app.ShowHotkeys(FindForm()!);
        group.Click += (_, _) => GroupForm.ShowFor(_app, FindForm()!);
        sync.Click += (_, _) => _app.RunSyncTest(FindForm()!);
        link.Click += (_, _) => OpenRepository();

        Load();
    }

    /// <summary>The cap was changed here (the volume sliders follow).</summary>
    public event Action? CapChanged;

    private static bool SafeAutostart()
    {
        try { return Autostart.Enabled; } catch { return false; }
    }

    private void Load()
    {
        var cfg = _app.Config;
        string current = LanguageMenu.Normalize(cfg.Language);
        _language.SelectedIndex = current == L.Auto ? 0 : Array.FindIndex(LanguageMenu.Choices, c => c.Value == current) + 1;
        _theme.SelectedIndex = Array.IndexOf(ThemeModes, Theme.Normalize(cfg.Theme));
        _autostart.Checked = SafeAutostart();
        _autoconnect.Checked = cfg.AutoConnect;
        _forward.Checked = cfg.ForwardVolumeKeys;
        ShowSoundOptions();
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
    }

    public void ShowSoundOptions()
    {
        if (_cap.IsDragging || _capWait.Enabled) return;
        _cap.Value = _app.Config.VolumeCapPercent;
        _capValue.Text = $"{_cap.Value}%";
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
        _autostart.Checked = SafeAutostart();
        ShowSoundOptions();
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _capWait.Dispose();
        base.Dispose(disposing);
    }
}
