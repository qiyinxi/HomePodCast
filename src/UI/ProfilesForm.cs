using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed class ProfilesPanel : StackPanel
{
    private readonly AppConfig _config;
    private readonly FluentComboBox _profiles = new() { PreferredWidth = 440, Placeholder = L.T("选择配置…"), AccessibleName = L.T("使用配置") };
    private readonly TextBox _name = new() { MaxLength = UsageProfiles.MaxNameLength, AccessibleName = L.T("配置名称") };
    private readonly FluentButton _apply = new(L.T("应用"), ButtonKind.Primary);
    private readonly FluentButton _delete = new(L.T("删除"), ButtonKind.Subtle);
    private string? _selected;

    public ProfilesPanel(TrayApp app) : this(app.Config, app.SaveUsageProfile, app.ApplyUsageProfile, app.DeleteUsageProfile) { }

    public event Action? Applied;

    internal ProfilesPanel(AppConfig config, Action<string?, string> saveProfile,
        Func<string, IWin32Window?, bool> applyProfile, Action<string> deleteProfile)
    {
        _config = config;
        Gap = 12;
        _name.BorderStyle = BorderStyle.FixedSingle;
        StyleName();
        var help = new TextBlock(L.T("保存音箱、采集设备、程序分流、音效、音量上限和夜间模式，之后一键切换。切换后麦克风关闭。"), wrap: true);
        var fresh = new FluentButton(L.T("新建"));
        var save = new FluentButton(L.T("保存当前设置"));
        Controls.AddRange([help, _profiles, new TextBlock(L.T("配置名称")), _name,
            Ui.Row(8, null, fresh, save), Ui.Row(8, null, _delete, _apply)]);
        _profiles.SelectionChangeCommitted += (_, _) =>
        {
            _selected = Selected()?.Id;
            _name.Text = Selected()?.Name ?? "";
            UpdateButtons();
        };
        fresh.Click += (_, _) => { _selected = null; _profiles.SelectedIndex = -1; _name.Clear(); UpdateButtons(); _name.Focus(); };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { _name.Focus(); return; }
            // Two profiles of the same name can't be told apart in 首页's picker.
            if (UsageProfiles.NameTaken(config.UsageProfiles, _name.Text, _selected))
            {
                MessageBox.Show(this, L.F("已经有名为「{0}」的配置，请换一个名称。", _name.Text.Trim()), L.T("使用配置"));
                _name.Focus();
                return;
            }
            if (_selected != null && MessageBox.Show(this, L.T("用当前设置覆盖选中的配置吗？"), L.T("使用配置"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (_selected == null && config.UsageProfiles.Count >= UsageProfiles.MaxProfiles)
            {
                MessageBox.Show(this, L.F("最多保存 {0} 个配置，请先删除一个。", UsageProfiles.MaxProfiles), L.T("使用配置"));
                return;
            }
            try
            {
                saveProfile(_selected, _name.Text);
                _selected ??= config.UsageProfiles.Last().Id;
                Reload();
            }
            catch (Exception ex) { Log.Warn($"profile save failed: {ex.Message}"); MessageBox.Show(this, L.T("保存失败，请稍后重试。"), L.T("使用配置")); }
        };
        _apply.Click += (_, _) => { if (_selected != null && applyProfile(_selected, FindForm())) Applied?.Invoke(); };
        _delete.Click += (_, _) =>
        {
            if (_selected == null || MessageBox.Show(this, L.T("删除选中的配置吗？"), L.T("使用配置"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            deleteProfile(_selected);
            _selected = null; _name.Clear(); Reload();
        };
        Reload();
    }

    private void StyleName()
    {
        _name.BackColor = Theme.P.Control;
        _name.ForeColor = Theme.P.Text;
    }
    protected override void OnThemeChanged()
    {
        StyleName();
        base.OnThemeChanged();
    }

    private UsageProfile? Selected() => _profiles.SelectedIndex >= 0 && _profiles.SelectedIndex < _config.UsageProfiles.Count
        ? _config.UsageProfiles[_profiles.SelectedIndex] : null;

    public void Reload()
    {
        using var layout = LayoutPanel.BatchRelayout(this);
        _profiles.Items.Clear();
        _profiles.Items.AddRange(_config.UsageProfiles.Select(p => p.Name));
        _profiles.SelectedIndex = _config.UsageProfiles.FindIndex(p => p.Id == _selected);
        _profiles.Enabled = _profiles.Items.Count > 0;
        UpdateButtons();
    }

    private void UpdateButtons() => _apply.Enabled = _delete.Enabled = _selected != null;
}

internal sealed class ProfilesForm : FluentDialog
{
    public ProfilesForm(TrayApp app) : this(app.Config, app.SaveUsageProfile, app.ApplyUsageProfile, app.DeleteUsageProfile) { }
    internal ProfilesForm(AppConfig config, Action<string?, string> save,
        Func<string, IWin32Window?, bool> apply, Action<string> delete) : base(L.T("使用配置"))
    {
        ContentWidth = 540;
        var panel = new ProfilesPanel(config, save, apply, delete);
        var close = new FluentButton(L.T("关闭")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([panel, ButtonRow(close)]);
        panel.Applied += () => DialogResult = DialogResult.OK;
        CancelButton = close;
    }
}
