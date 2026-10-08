namespace HomePodCast.UI.Controls;

/// <summary>
/// Fluent toggle switch with an optional label to its right (the label is clickable too). Space toggles.
/// <see cref="CheckedChanged"/> fires for every change; <see cref="Toggled"/> only when the user flipped it.
/// </summary>
internal sealed class ToggleSwitch : FluentControl
{
    private bool _checked;

    public ToggleSwitch(string text = "")
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false);
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        Text = text;
    }

    public event EventHandler? CheckedChanged;
    public event EventHandler? Toggled;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    /// <summary>Flip as the user would (click, Space, accessibility action).</summary>
    internal void Flip()
    {
        if (!Enabled) return;
        Checked = !Checked;
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Flip();
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && e.Modifiers == Keys.None)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Flip();
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        RequestLayout();
    }

    private Size TrackSize => new(Dp(40), Dp(20));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var track = TrackSize;
        int h = Math.Max(track.Height + Dp(8), LineHeight(TextStyle.Body) + Dp(4));
        if (string.IsNullOrEmpty(Text)) return new Size(track.Width + Dp(4), h);
        int text = TextRenderer.MeasureText(Text, StyleFont(TextStyle.Body), Size.Empty, TextFlags.Measure).Width;
        return new Size(track.Width + Dp(12) + text + Dp(4), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var track = TrackSize;
        var r = new Rectangle(Dp(2), (Height - track.Height) / 2, track.Width, track.Height);
        float radius = track.Height / 2f;
        bool enabled = Enabled;
        if (_checked)
        {
            var fill = !enabled ? P.TextDisabled : IsPressed ? P.AccentPressed : IsHover ? P.AccentHover : P.Accent;
            Shapes.FillRound(g, fill, r, radius);
        }
        else
        {
            if (IsHover && enabled) Shapes.FillRound(g, P.Subtle, r, radius);
            Shapes.BorderRound(g, enabled ? P.ToggleBorder : P.TextDisabled, r, radius, Math.Max(1, Dp(1)));
        }

        // Knob: 12 px, 14 on hover; stretches while pressed.
        float knob = !enabled ? Dp(12) : IsHover || IsPressed ? Dp(14) : Dp(12);
        float knobWidth = IsPressed && enabled ? knob + Dp(3) : knob;
        float cy = r.Top + r.Height / 2f;
        float margin = (r.Height - knob) / 2f;
        float x = _checked ? r.Right - margin - knobWidth : r.Left + margin;
        var knobColor = _checked ? (enabled ? P.TextOnAccent : P.Card) : enabled ? P.ToggleKnob : P.TextDisabled;
        Shapes.FillRound(g, knobColor, new RectangleF(x, cy - knob / 2f, knobWidth, knob), knob / 2f);

        if (!string.IsNullOrEmpty(Text))
        {
            var textRect = new Rectangle(r.Right + Dp(12), 0, Width - r.Right - Dp(12), Height);
            TextRenderer.DrawText(g, Text, StyleFont(TextStyle.Body), textRect, enabled ? P.Text : P.TextDisabled, TextFlags.Line);
        }
        if (FocusVisible) DrawFocusRing(g, Rectangle.Inflate(r, Dp(2), Dp(3)), radius + Dp(3));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessible(this);

    private sealed class ToggleAccessible(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override string DefaultAction => L.T("切换");

        public override void DoDefaultAction() => owner.Flip();
    }
}
