namespace HomePodCast.UI.Controls;

internal enum ButtonKind
{
    /// <summary>Neutral fill with a hairline border.</summary>
    Secondary,
    /// <summary>Accent fill: the one main action of a card.</summary>
    Primary,
    /// <summary>No fill until hovered (icon buttons, quiet actions).</summary>
    Subtle,
    /// <summary>Accent text, like a hyperlink.</summary>
    Link,
}

/// <summary>Fluent button: text and/or an icon-font glyph; Space/Enter click it; works as a dialog's Accept/Cancel button.</summary>
internal sealed class FluentButton : FluentControl, IButtonControl
{
    private ButtonKind _kind;
    private string? _glyph;

    public FluentButton(string text = "", ButtonKind kind = ButtonKind.Secondary, string? glyph = null)
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false);
        TabStop = true;
        AccessibleRole = kind == ButtonKind.Link ? AccessibleRole.Link : AccessibleRole.PushButton;
        _kind = kind;
        _glyph = glyph;
        Text = text;
        Cursor = kind == ButtonKind.Link ? Cursors.Hand : Cursors.Default;
    }

    public ButtonKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; Invalidate(); } }
    }

    public string? Glyph
    {
        get => _glyph;
        set { if (_glyph != value) { _glyph = value; Invalidate(); RequestLayout(); } }
    }

    /// <summary>Glyph colour override (e.g. a muted speaker in the state colour).</summary>
    public Color? GlyphColor { get; set; }

    /// <summary>Minimum width in 96-DPI units.</summary>
    public int MinWidth { get; set; }

    /// <summary>Height in 96-DPI units.</summary>
    public int ButtonHeight { get; set; } = 32;

    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value) { }

    public void PerformClick()
    {
        if (Enabled && Visible) OnClick(EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (DialogResult != DialogResult.None && FindForm() is { } form) form.DialogResult = DialogResult;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        RequestLayout();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space && e.Modifiers == Keys.None)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            PerformClick();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = Dp(ButtonHeight);
        bool hasText = !string.IsNullOrEmpty(Text);
        if (!hasText) return new Size(h, h); // icon button: square
        int w = TextRenderer.MeasureText(Text, StyleFont(TextStyle.Body), Size.Empty, TextFlags.Measure).Width + Dp(_kind == ButtonKind.Link ? 4 : 24);
        if (_glyph != null) w += Dp(16 + 8);
        return new Size(Math.Max(w, Dp(MinWidth)), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        float radius = Dp(4);
        Color fill, border = Color.Empty, fore;
        bool enabled = Enabled;
        switch (_kind)
        {
            case ButtonKind.Primary:
                fill = !enabled ? P.TextDisabled : IsPressed ? P.AccentPressed : IsHover ? P.AccentHover : P.Accent;
                fore = enabled ? P.TextOnAccent : P.Card;
                break;
            case ButtonKind.Subtle:
                fill = !enabled ? Surface : IsPressed ? P.SubtlePressed : IsHover ? P.Subtle : Surface;
                fore = enabled ? P.Text : P.TextDisabled;
                break;
            case ButtonKind.Link:
                fill = Surface;
                fore = !enabled ? P.TextDisabled : IsPressed ? P.AccentPressed : P.AccentText;
                break;
            default:
                fill = !enabled ? P.Control : IsPressed ? P.ControlPressed : IsHover ? P.ControlHover : P.Control;
                border = P.ControlBorder;
                fore = !enabled ? P.TextDisabled : IsPressed ? P.TextSecondary : P.Text;
                break;
        }
        if (fill != Surface) Shapes.FillRound(g, fill, r, radius);
        if (!border.IsEmpty) Shapes.BorderRound(g, border, r, radius, Hairline);

        var font = StyleFont(TextStyle.Body);
        bool hasText = !string.IsNullOrEmpty(Text);
        if (_glyph != null)
        {
            var iconFont = Theme.IconFont(16, DeviceDpi);
            int icon = Dp(16);
            int textWidth = hasText ? TextRenderer.MeasureText(Text, font, Size.Empty, TextFlags.Measure).Width + Dp(8) : 0;
            int x = (Width - icon - textWidth) / 2;
            Shapes.Glyph(g, _glyph, iconFont, new Rectangle(x, 0, icon, Height), GlyphColor is { } gc && enabled ? gc : fore);
            if (hasText)
                TextRenderer.DrawText(g, Text, font, new Rectangle(x + icon + Dp(8), 0, Width - x - icon - Dp(8), Height), fore, TextFlags.Line);
        }
        else
        {
            var flags = TextFlags.Line | TextFormatFlags.HorizontalCenter;
            if (_kind == ButtonKind.Link && (IsHover || FocusVisible))
            {
                using var underline = new Font(font, FontStyle.Underline);
                TextRenderer.DrawText(g, Text, underline, r, fore, flags);
            }
            else
            {
                TextRenderer.DrawText(g, Text, font, r, fore, flags);
            }
        }
        if (FocusVisible) DrawFocusRing(g, r, radius + Dp(1));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessible(this);

    private sealed class ButtonAccessible(FluentButton owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => L.T("按下");

        // Posted: a click may open a modal dialog, which must not run inside the accessibility call.
        public override void DoDefaultAction()
        {
            if (owner.IsHandleCreated) owner.BeginInvoke(owner.PerformClick);
        }
    }
}