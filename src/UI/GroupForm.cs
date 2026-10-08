using HomePodCast.Net;

namespace HomePodCast.UI;

/// <summary>
/// Experimental settings for playing to more than one speaker: a stereo pair (chosen in the main device
/// list), or a second speaker that plays in sync with the selected one (multi-room); channel split and
/// swap; per-speaker volume offsets on top of the linked volume.
/// </summary>
internal sealed class GroupForm : Form
{
    private const int OffsetRange = 30;

    private readonly AppConfig _config;
    private readonly string? _tsid;
    private readonly ComboBox _extra = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _split = new() { Text = L.T("电脑端分声道"), AutoSize = true };
    private readonly CheckBox _swap = new() { Text = L.T("交换左右"), AutoSize = true };
    private readonly TableLayoutPanel _memberRows = new() { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _state = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0) };
    private readonly Dictionary<string, int> _offsets;
    private List<AirPlayDevice> _found = [];
    private List<AirPlayDevice?> _choices = [null];
    private bool _scanned;

    public static void ShowFor(TrayApp app, IWin32Window owner)
    {
        string before = PlayLayout(app.Config);
        using var form = new GroupForm(app.Config);
        if (form.ShowDialog(owner) != DialogResult.OK) return;
        app.Config.Save();
        // Reconnect only if what plays actually changed; a single speaker is never interrupted for nothing.
        if (PlayLayout(app.Config) != before && app.Controller.State != StreamState.Idle) app.Connect();
    }

    /// <summary>Edits <paramref name="config"/> in place when OK is pressed; the caller saves it.</summary>
    internal GroupForm(AppConfig config)
    {
        _config = config;
        var cfg = config;
        _tsid = StereoPairs.TryGetTsid(cfg.DeviceId, out var t) ? t : null;
        _offsets = new Dictionary<string, int>(cfg.GroupVolumeOffsets ?? new());

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = L.T("多音箱（实验性）");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = Icons.Speaker(Icons.Streaming);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var outer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(14, 12, 14, 10),
        };

        outer.Controls.Add(Note(L.T("实验性功能：立体声对和多房间同步还没有在真实设备上验证过，可能不同步、没有声音或连不上。"),
            Icons.Connecting, bold: true));

        string current = cfg.DeviceId == null ? L.T("还没有选择音箱")
            : _tsid != null ? L.F("立体声对「{0}」", cfg.DeviceName ?? _tsid)
            : cfg.DeviceName ?? cfg.DeviceId;
        outer.Controls.Add(Row(L.T("当前音箱"), new Label { Text = current, AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));

        _extra.Items.Add(L.T("（不使用）"));
        _extra.SelectedIndex = 0;
        _extra.Enabled = false;
        outer.Controls.Add(Row(L.T("同时推送到"), _extra));
        outer.Controls.Add(Note(_tsid != null
            ? L.T("立体声对的两只音箱会自动一起连接，不需要再选第二台。")
            : L.T("多房间：选择另一台 AirPlay 音箱，和当前音箱按同一时间线同步播放。"), Color.DimGray));

        _split.Checked = cfg.GroupSplitChannels;
        _swap.Checked = cfg.GroupSwapChannels;
        _split.Margin = new Padding(3, 10, 3, 0);
        outer.Controls.Add(_split);
        outer.Controls.Add(Note(L.T("勾选后，左边的音箱只放左声道、右边的只放右声道；不勾选时每台都收到完整立体声。"), Color.DimGray));
        outer.Controls.Add(_swap);

        outer.Controls.Add(_memberRows);
        _state.Margin = new Padding(3, 8, 3, 0);
        _state.Text = L.T("正在搜索音箱…");
        outer.Controls.Add(_state);

        var ok = new Button { Text = L.T("确定"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = L.T("取消"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([ok, cancel]);
        outer.Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(outer);
        ResumeLayout(false);
        PerformLayout();

        _extra.SelectedIndexChanged += (_, _) => ShowMembers();
        _split.CheckedChanged += (_, _) => ShowMembers();
        _swap.CheckedChanged += (_, _) => ShowMembers();
        ok.Click += (_, _) => Apply();
        ShowMembers();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        List<AirPlayDevice> found = [];
        try
        {
            found = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warn($"group settings scan: {ex.Message}");
        }
        if (!IsDisposed) SetFound(found);
    }

    /// <summary>Scan result (raw mDNS list, pairs not merged).</summary>
    internal void SetFound(List<AirPlayDevice> found)
    {
        _found = found;
        _scanned = true;
        FillChoices();
        ShowMembers();
    }

    private void FillChoices()
    {
        var cfg = _config;
        if (_tsid != null || cfg.DeviceId == null) return;
        string self = GroupPlan.Key(cfg.DeviceId);
        _choices = [null, .. StereoPairs.Merge(_found).Where(d =>
            !StereoPairs.IsPairId(d.DeviceId) && d.DeviceId.Length > 0 && GroupPlan.Key(d.DeviceId) != self)];
        if (cfg.MultiRoomDeviceId is { } extra && !_choices.Any(d => d != null && GroupPlan.Key(d.DeviceId) == GroupPlan.Key(extra)))
            _choices.Add(new AirPlayDevice(cfg.MultiRoomDeviceName ?? extra, extra, System.Net.IPAddress.None, 7000, L.T("未发现"),
                new Dictionary<string, string>()));

        _extra.BeginUpdate();
        _extra.Items.Clear();
        foreach (var d in _choices)
            _extra.Items.Add(d == null ? L.T("（不使用）")
                : d.Address.Equals(System.Net.IPAddress.None) ? L.F("{0}（未发现）", d.Name)
                : $"{d.Name}  {d.Address}");
        _extra.SelectedIndex = Math.Max(0, _choices.FindIndex(d =>
            d != null && cfg.MultiRoomDeviceId != null && GroupPlan.Key(d.DeviceId) == GroupPlan.Key(cfg.MultiRoomDeviceId)));
        _extra.EndUpdate();
        _extra.Enabled = true;
    }

    /// <summary>The speakers that would play, in channel order (the first one is left unless swapped).</summary>
    private List<(string DeviceId, string Name)> Members()
    {
        var cfg = _config;
        if (_tsid != null)
            return StereoPairs.Members(_found, _tsid).Select(d => (d.DeviceId, $"{d.Name}  {d.Address}")).ToList();
        if (cfg.DeviceId == null || _extra.SelectedIndex <= 0 || _extra.SelectedIndex >= _choices.Count) return [];
        var extra = _choices[_extra.SelectedIndex]!;
        return [(cfg.DeviceId, cfg.DeviceName ?? cfg.DeviceId), (extra.DeviceId, extra.Name)];
    }

    private void ShowMembers()
    {
        foreach (var (key, box) in CurrentOffsetBoxes()) _offsets[key] = (int)box.Value;

        var members = Members();
        bool group = members.Count == 2;
        _split.Enabled = group;
        _swap.Enabled = group && _split.Checked;

        _memberRows.SuspendLayout();
        foreach (var old in _memberRows.Controls.Cast<Control>().ToList()) old.Dispose();
        _memberRows.RowStyles.Clear();
        if (group)
        {
            _memberRows.Controls.Add(new Label { Text = L.T("音量偏移"), AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left }, 2, 0);
            var plan = new GroupPlan(GroupKind.StereoPair, "", null, [], _split.Checked, _swap.Checked, new Dictionary<string, int>());
            for (int i = 0; i < members.Count; i++)
            {
                string role = plan.ChannelsFor(i) switch
                {
                    ChannelMode.LeftOnly => L.T("左声道"),
                    ChannelMode.RightOnly => L.T("右声道"),
                    _ => L.T("完整立体声"),
                };
                string key = GroupPlan.Key(members[i].DeviceId);
                var offset = new NumericUpDown
                {
                    Minimum = -OffsetRange,
                    Maximum = OffsetRange,
                    Width = 64,
                    Value = Math.Clamp(_offsets.GetValueOrDefault(key), -OffsetRange, OffsetRange),
                    Tag = key,
                    Anchor = AnchorStyles.Left,
                };
                _memberRows.Controls.Add(new Label { Text = role, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, i + 1);
                _memberRows.Controls.Add(new Label { Text = members[i].Name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 3, 10, 3) }, 1, i + 1);
                _memberRows.Controls.Add(offset, 2, i + 1);
            }
        }
        _memberRows.ResumeLayout();

        _state.Text = _config.DeviceId == null ? L.T("请先在主界面选择音箱。")
            : !_scanned ? L.T("正在搜索音箱…")
            : _tsid != null && members.Count < 2 ? L.F("没有同时找到这对音箱的两只（找到 {0} 只）。两只都在线才能连接。", members.Count)
            : group ? L.T("两台音箱共用一个音量滑块，偏移量加在各自的音量上。")
            : L.T("现在只推送到一台音箱。");
    }

    private IEnumerable<(string Key, NumericUpDown Box)> CurrentOffsetBoxes() =>
        _memberRows.Controls.OfType<NumericUpDown>().Select(b => ((string)b.Tag!, b));

    internal void Apply()
    {
        foreach (var (key, box) in CurrentOffsetBoxes()) _offsets[key] = (int)box.Value;
        var cfg = _config;
        cfg.GroupSplitChannels = _split.Checked;
        cfg.GroupSwapChannels = _swap.Checked;
        cfg.GroupVolumeOffsets = _offsets.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (_tsid == null && _extra.Enabled)
        {
            var extra = _extra.SelectedIndex > 0 && _extra.SelectedIndex < _choices.Count ? _choices[_extra.SelectedIndex] : null;
            cfg.MultiRoomDeviceId = extra?.DeviceId;
            cfg.MultiRoomDeviceName = extra?.Name;
            if (extra == null) cfg.MultiRoomHost = null;
            else if (!extra.Address.Equals(System.Net.IPAddress.None)) cfg.MultiRoomHost = extra.Address.ToString();
        }
    }

    private static string PlayLayout(AppConfig cfg) => GroupPlan.FromConfig(cfg) is { } p
        ? $"{p.Kind}|{p.PairTsid}|{string.Join(",", p.Members.Select(m => m.DeviceId))}|{p.SplitChannels}|{p.SwapChannels}|" +
          string.Join(",", p.VolumeOffsets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
        : "single";

    private static FlowLayoutPanel Row(string caption, Control field)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.Add(new Label { Text = caption, AutoSize = true, MinimumSize = new Size(72, 0), Margin = new Padding(3, 6, 6, 3) });
        row.Controls.Add(field);
        return row;
    }

    private Label Note(string text, Color color, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        ForeColor = color,
        Font = bold ? new Font(Font, FontStyle.Bold) : Font,
        Margin = new Padding(3, 2, 3, 2),
    };
}
