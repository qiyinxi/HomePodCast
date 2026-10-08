namespace HomePodCast.UI.Controls;

/// <summary>What a text is for; the colour comes from the theme.</summary>
internal enum TextRole { Primary, Secondary, Disabled, Accent, Success, Caution, Critical }

/// <summary>
/// Static text in a type-ramp style and a theme colour; single line with an ellipsis, or wrapped to the
/// width it is given (its preferred height follows). Empty text takes no space in a stack.
/// </summary>
internal sealed class TextBlock : FluentControl
{
    private TextStyle _style = TextStyle.Body;
    private TextRole _role = TextRole.Primary;
    private bool _wrap;
    private Color? _color;

    public TextBlock(string text = "", TextStyle style = TextStyle.Body, TextRole role = TextRole.Primary, bool wrap = false)
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        _style = style;
        _role = role;
        _wrap = wrap;
        Text = text;
    }

    public TextStyle Style
    {
        get => _style;
        set { if (_style != value) { _style = value; Changed(); } }
    }

    public TextRole Role
    {
        get => _role;
        set { if (_role != value) { _role = value; Invalidate(); } }
    }

    /// <summary>An explicit colour (e.g. a status colour that is not a theme role); null = <see cref="Role"/>.</summary>
    public Color? CustomColor
    {
        get => _color;
        set { if (_color != value) { _color = value; Invalidate(); } }
    }

    public bool Wrap
    {
        get => _wrap;
        set { if (_wrap != value) { _wrap = value; Changed(); } }
    }

    public HorizontalAlignment Align { get; set; } = HorizontalAlignment.Left;

    public Font TextFont => StyleFont(_style);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Changed();
    }

    private void Changed()
    {
        Invalidate();
        if (Parent == null || Width == 0) return;
        if (GetPreferredSize(new Size(Width, 0)).Height != Height || !_wrap) RequestLayout();
    }

    public static Color ColorOf(TextRole role) => role switch
    {
        TextRole.Secondary => P.TextSecondary,
        TextRole.Disabled => P.TextDisabled,
        TextRole.Accent => P.AccentText,
        TextRole.Success => P.Success,
        TextRole.Caution => P.Caution,
        TextRole.Critical => P.Critical,
        _ => P.Text,
    };

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (string.IsNullOrEmpty(Text)) return Size.Empty;
        var font = TextFont;
        if (_wrap && proposedSize.Width > 0)
        {
            var s = TextRenderer.MeasureText(Text, font, new Size(proposedSize.Width, int.MaxValue), TextFlags.Wrap);
            return new Size(proposedSize.Width, s.Height + 1);
        }
        var line = TextRenderer.MeasureText(Text, font, Size.Empty, TextFlags.Measure);
        return new Size(line.Width + 1, Math.Max(line.Height, LineHeight(_style)) + 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var color = !Enabled ? P.TextDisabled : _color ?? ColorOf(_role);
        var align = Align switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        TextRenderer.DrawText(e.Graphics, Text, TextFont, ClientRectangle, color, (_wrap ? TextFlags.Wrap : TextFlags.Line) | align);
    }
}
