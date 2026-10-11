using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed class ProfilesForm : FluentDialog
{
    private readonly AppConfig _config;
    private readonly FluentComboBox _profiles = new() { PreferredWidth = 440 };
    private readonly TextBox _name = new() { MaxLength = UsageProfiles.MaxNameLength, AccessibleName = L.T("配置名称") };
    private readonly FluentButton _apply = new(L.T("应用"), ButtonKind.Primary);
    private readonly FluentButton _delete = new(L.T("删除"), ButtonKind.Subtle);
    private string? _selected;

    public ProfilesForm(TrayApp app) : this(app.Config, app.SaveUsageProfile, app.ApplyUsageProfile, app.DeleteUsageProfile) { }

    internal ProfilesForm(AppConfig config, Action<string?, string> saveProfile,
        Func<string, IWin32Window?, bool> applyProfile, Action<string> deleteProfile) : base(L.T("使用配置"))
    {
        _config = config;
        ContentWidth = 540;
        var help = new TextBlock(L.T("保存音箱、采集设备、程序分流、音效、音量上限和夜间模式，之后一键切换。切换后麦克风关闭。"), wrap: true);
        var fresh = new FluentButton(L.T("新建"));
        var save = new FluentButton(L.T("保存当前设置"));
        var close = new FluentButton(L.T("关闭")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([help, _profiles, new TextBlock(L.T("配置名称")), _name,
            Ui.Row(8, null, fresh, save), ButtonRow(_delete, _apply, close)]);
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
        _apply.Click += (_, _) => { if (_selected != null && applyProfile(_selected, this)) DialogResult = DialogResult.OK; };
        _delete.Click += (_, _) =>
        {
            if (_selected == null || MessageBox.Show(this, L.T("删除选中的配置吗？"), L.T("使用配置"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            deleteProfile(_selected);
            _selected = null; _name.Clear(); Reload();
        };
        CancelButton = close;
        Reload();
    }

    private UsageProfile? Selected() => _profiles.SelectedIndex >= 0 && _profiles.SelectedIndex < _config.UsageProfiles.Count
        ? _config.UsageProfiles[_profiles.SelectedIndex] : null;

    private void Reload()
    {
        _profiles.Items.Clear();
        _profiles.Items.AddRange(_config.UsageProfiles.Select(p => p.Name));
        _profiles.SelectedIndex = _config.UsageProfiles.FindIndex(p => p.Id == _selected);
        _profiles.Enabled = _profiles.Items.Count > 0;
        UpdateButtons();
    }

    private void UpdateButtons() => _apply.Enabled = _delete.Enabled = _selected != null;
}
