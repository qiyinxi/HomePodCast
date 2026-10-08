using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// Global hotkeys. While open, our own hotkeys are released so they can be typed into the boxes; each row
/// shows whether its combination is free. Changes are saved as they are made; closing the dialog registers
/// the hotkeys again. (Volume-key forwarding is a switch on the 设置 page.)
/// </summary>
internal sealed class HotkeysForm : FluentDialog
{
    private readonly TrayApp _app;
    private readonly Dictionary<HotkeyAction, (HotkeyBox Box, TextBlock Status)> _rows = new();

    public HotkeysForm(TrayApp app) : base(L.T("全局快捷键"))
    {
        _app = app;
        ContentWidth = 560;
        var rows = new List<FieldRow>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var box = new HotkeyBox { Value = Hotkey.FromConfig(_app.Config, action) };
            var status = new TextBlock("", TextStyle.Caption, TextRole.Secondary);
            box.ValueChanged += (_, _) => OnChanged(action, box.Value);
            var row = new FieldRow(ActionName(action), Ui.Row(12, status, box, status));
            ((RowPanel)row.Field).FixedWidths[box] = 190;
            rows.Add(row);
            Body.Controls.Add(row);
            _rows[action] = (box, status);
        }
        FieldRow.AlignCaptions([.. rows]);
        Body.Controls.Add(Ui.Note(L.T("点输入框，再按下新的组合键（要包含 Ctrl 或 Alt）；Backspace 清除。")));

        var defaults = new FluentButton(L.T("恢复默认"));
        var close = new FluentButton(L.T("关闭"), ButtonKind.Primary) { DialogResult = DialogResult.OK, MinWidth = 96 };
        defaults.Click += (_, _) => RestoreDefaults();
        var buttons = ButtonRow(defaults, close);
        Body.Controls.Add(buttons);
        Body.GapBefore[buttons] = 20;
        AcceptButton = close;
        CancelButton = close;
        ActiveControl = close; // no box is recording until the user clicks one
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
            (status.Text, status.Role) =
                key is null ? (L.T("未设置"), TextRole.Secondary)
                : twin is { } t ? (L.F("和「{0}」重复", ActionName(t)), TextRole.Critical)
                : _app.ProbeHotkey(key.Value) ? (L.T("可用"), TextRole.Success)
                : (L.T("不可用：已被其他程序占用"), TextRole.Critical);
        }
    }
}

/// <summary>
/// Records a key combination: click it (or tab to it), press the keys. Backspace/Delete clears it.
/// Looks like a Fluent text box; the accent underline shows it is recording.
/// </summary>
internal sealed class HotkeyBox : FluentControl
{
    private Hotkey? _value;

    public HotkeyBox()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Text;
        Text = L.T("（无）");
    }

    public event EventHandler? ValueChanged;

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
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(190), Dp(32));

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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        float radius = Dp(4);
        bool recording = Focused;
        Shapes.FillRound(g, recording ? P.Card : IsHover ? P.ControlHover : P.Control, r, radius);
        Shapes.BorderRound(g, P.ControlBorder, r, radius, Hairline);
        if (recording)
        {
            // Fluent text box focus: a 2 px accent line along the bottom edge.
            using var clip = Shapes.Round(r, radius);
            g.SetClip(clip);
            using var b = new SolidBrush(P.Accent);
            g.FillRectangle(b, 0, Height - Dp(2), Width, Dp(2));
            g.ResetClip();
        }
        TextRenderer.DrawText(g, Text, StyleFont(TextStyle.Body), r, _value == null ? P.TextSecondary : P.Text,
            TextFlags.Line | TextFormatFlags.HorizontalCenter);
    }
}
