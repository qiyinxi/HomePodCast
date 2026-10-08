namespace HomePodCast.UI;

/// <summary>
/// Global hotkeys and volume-key forwarding. While open, our own hotkeys are released so they can be
/// typed into the boxes; each row shows whether its combination is free. Changes are saved as they are
/// made; closing the dialog registers the hotkeys again.
/// </summary>
internal sealed class HotkeysForm : Form
{
    private readonly TrayApp _app;
    private readonly Dictionary<HotkeyAction, (HotkeyBox Box, Label Status)> _rows = new();
    private readonly CheckBox _forward = new() { AutoSize = true };

    public HotkeysForm(TrayApp app)
    {
        _app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = L.T("快捷键与音量键");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = Icons.Speaker(Icons.Streaming);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 12, 14, 10),
            ColumnCount = 3,
        };
        for (int i = 0; i < 3; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var box = new HotkeyBox { Width = 170, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 10, 4) };
            var status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(150, 0) };
            box.Value = Hotkey.FromConfig(_app.Config, action);
            box.ValueChanged += (_, _) => OnChanged(action, box.Value);
            layout.Controls.Add(new Label { Text = ActionName(action), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, row);
            layout.Controls.Add(box, 1, row);
            layout.Controls.Add(status, 2, row);
            _rows[action] = (box, status);
            row++;
        }

        var hint = new Label
        {
            Text = L.T("点输入框，再按下新的组合键（要包含 Ctrl 或 Alt）；Backspace 清除。"),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 3, 10),
        };
        layout.Controls.Add(hint, 0, row);
        layout.SetColumnSpan(hint, 3);
        row++;

        _forward.Text = L.T("Windows 静音或音量为 0 时，键盘音量键调节 HomePod 音量");
        _forward.Checked = _app.Config.ForwardVolumeKeys;
        _forward.Margin = new Padding(0, 0, 3, 2);
        _forward.CheckedChanged += (_, _) => _app.SetForwardVolumeKeys(_forward.Checked);
        layout.Controls.Add(_forward, 0, row);
        layout.SetColumnSpan(_forward, 3);
        row++;

        var forwardHint = new Label
        {
            Text = L.T("建议把 Windows 设为静音（而不是 0%）：这样音量 +、− 和静音键都会转给 HomePod，Windows 保持静音。"),
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(18, 0, 3, 10),
        };
        layout.Controls.Add(forwardHint, 0, row);
        layout.SetColumnSpan(forwardHint, 3);
        row++;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 0) };
        var defaults = new Button { Text = L.T("恢复默认"), AutoSize = true };
        var close = new Button { Text = L.T("关闭"), AutoSize = true, DialogResult = DialogResult.OK, Margin = new Padding(10, 3, 0, 3) };
        defaults.Click += (_, _) => RestoreDefaults();
        buttons.Controls.AddRange([defaults, close]);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 3);
        row++;

        for (int i = 0; i < row; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;
        ActiveControl = close; // no box is recording until the user clicks one
        ResumeLayout(false);
        PerformLayout();
    }

    public static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.VolumeUp => L.T("HomePod 音量 +"),
        HotkeyAction.VolumeDown => L.T("HomePod 音量 −"),
        HotkeyAction.Mute => L.T("HomePod 静音 / 取消静音"),
        _ => L.T("切换场景"),
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _app.SuspendHotkeys(); // our own registrations would swallow the keys typed into the boxes
        RefreshStatus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _app.ApplyHotkeys();
        base.OnFormClosed(e);
    }

    private void OnChanged(HotkeyAction action, Hotkey? key)
    {
        _app.Config.Hotkeys ??= new();
        _app.Config.Hotkeys[action] = key?.ToString() ?? "";
        _app.Config.Save();
        RefreshStatus();
    }

    private void RestoreDefaults()
    {
        _app.Config.Hotkeys = new();
        _app.Config.Save();
        foreach (var (action, (box, _)) in _rows) box.SetValueSilently(Hotkey.FromConfig(_app.Config, action));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        foreach (var (action, (box, status)) in _rows)
        {
            var key = box.Value;
            HotkeyAction? twin = _rows.Where(r => r.Key < action && r.Value.Box.Value == key)
                .Select(r => (HotkeyAction?)r.Key).FirstOrDefault();
            (status.Text, status.ForeColor) =
                key is null ? (L.T("未设置"), Color.DimGray)
                : twin is { } t ? (L.F("和「{0}」重复", ActionName(t)), Icons.Error)
                : _app.ProbeHotkey(key.Value) ? (L.T("可用"), Icons.Streaming)
                : (L.T("不可用：已被其他程序占用"), Icons.Error);
        }
    }
}

/// <summary>Records a key combination: click it, press the keys. Backspace/Delete clears it.</summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey? _value;

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
        TextAlign = HorizontalAlignment.Center;
        Text = L.T("（无）");
    }

    public event EventHandler? ValueChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Hotkey? Value
    {
        get => _value;
        set
        {
            SetValueSilently(value);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetValueSilently(Hotkey? value)
    {
        _value = value;
        Text = value?.ToString() ?? L.T("（无）");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
        if (key == Keys.Tab && (mods & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);
        if (mods == Keys.None && key == Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        if (mods == Keys.None && key is Keys.Back or Keys.Delete)
        {
            if (_value != null) Value = null;
            return true;
        }
        if (Hotkey.IsModifierKey(key)) return true;
        var hotkey = new Hotkey(mods, key);
        if (hotkey.IsValid && hotkey != _value) Value = hotkey;
        return true;
    }
}
