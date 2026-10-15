namespace HomePodCast.UI.Controls;

/// <summary>
/// A bordered settings row in the Windows 11 Settings style: icon, title, optional description (wrapped),
/// and the control on the right. When the window is too narrow the control moves under the text.
/// </summary>
internal sealed class SettingRow : Card
{
    /// <summary>
    /// The row's icon, painted by the row: a window of its own for each row's icon was 29 of the windows a page creates
    /// (about 1.4 ms each on the maintainer's PC, and each one moved again on every layout).
    /// </summary>
    private readonly string? _icon;
    private Rectangle _iconBounds;

    public SettingRow(string? glyph, string title, string? description, Control? content)
    {
        Padding = new Padding(16, 12, 16, 12);
        _icon = glyph;
        TitleText = new TextBlock(title, TextStyle.Body, wrap: true);
        DescriptionText = new TextBlock(description ?? "", TextStyle.Caption, TextRole.Secondary, wrap: true);
        Controls.Add(TitleText);
        Controls.Add(DescriptionText);
        Content = content;
        if (content != null)
        {
            Controls.Add(content);
            content.AccessibleName ??= title;
        }
    }

    public TextBlock TitleText { get; }
    public TextBlock DescriptionText { get; }
    public Control? Content { get; }

    /// <summary>Content width in 96-DPI units; 0 = its preferred width.</summary>
    public int ContentWidth { get; set; }

    protected override int Arrange(int width, bool apply)
    {
        int left = Dp(Padding.Left), top = Dp(Padding.Top), right = width - Dp(Padding.Right);
        int x = left;
        int iconSize = Dp(20);
        if (_icon != null) x += iconSize + Dp(16);

        int contentW = 0, contentH = 0;
        if (Content != null && !IsCollapsed(Content))
        {
            contentW = ContentWidth > 0 ? Dp(ContentWidth) : WidthFor(Content);
        }
        int textW = right - x - (contentW > 0 ? contentW + Dp(16) : 0);
        // Keep descriptions readable in compact windows instead of squeezing them beside a wide picker.
        bool stacked = contentW > 0 && textW < Dp(240);
        if (stacked)
        {
            textW = right - x;
            contentW = Math.Max(0, Math.Min(contentW, textW));
        }
        // Under the text the content gets at most the text's width, and its height there (a row of buttons that wraps).
        if (Content != null && !IsCollapsed(Content)) contentH = HeightFor(Content, contentW);

        int titleH = HeightFor(TitleText, textW);
        int descH = HeightFor(DescriptionText, textW);
        int textH = titleH + (descH > 0 ? Dp(2) + descH : 0);
        int minH = Dp(44);
        int height;
        if (stacked)
        {
            int y = top + textH + Dp(10);
            if (apply)
            {
                Place(TitleText, x, top, textW, titleH);
                Place(DescriptionText, x, top + titleH + Dp(2), textW, descH);
                Place(Content!, x, y, contentW, contentH);
                SetIcon(new Rectangle(left, top, iconSize, Math.Max(iconSize, titleH)));
            }
            height = y + contentH + Dp(Padding.Bottom);
        }
        else
        {
            int inner = Math.Max(minH, Math.Max(textH, contentH));
            if (apply)
            {
                int ty = top + (inner - textH) / 2;
                Place(TitleText, x, ty, textW, titleH);
                Place(DescriptionText, x, ty + titleH + Dp(2), textW, descH);
                if (Content != null && contentW > 0) Place(Content, right - contentW, top + (inner - contentH) / 2, contentW, contentH);
                SetIcon(new Rectangle(left, top + (inner - iconSize) / 2, iconSize, iconSize));
            }
            height = top + inner + Dp(Padding.Bottom);
        }
        return height;
    }

    private void SetIcon(Rectangle bounds)
    {
        if (_icon == null || bounds == _iconBounds) return;
        Invalidate(_iconBounds);
        _iconBounds = bounds;
        Invalidate(_iconBounds);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_icon != null && e.ClipRectangle.IntersectsWith(_iconBounds))
            Shapes.Glyph(e.Graphics, _icon, Theme.IconFont(18, DeviceDpi), _iconBounds, P.Text);
    }
}

/// <summary>
/// One line of a form inside a card: caption (fixed column), the control (fills), and an optional value
/// (fixed column, right-aligned). Captions of a card share their width through <see cref="CaptionWidth"/>.
/// </summary>
internal sealed class FieldRow : LayoutPanel
{
    public FieldRow(string caption, Control field, TextBlock? value = null)
    {
        Caption = new TextBlock(caption);
        Field = field;
        Value = value;
        Controls.Add(Caption);
        Controls.Add(field);
        field.AccessibleName ??= caption;
        if (value != null)
        {
            value.Align = HorizontalAlignment.Right;
            Controls.Add(value);
        }
    }

    public TextBlock Caption { get; }
    public Control Field { get; }
    public TextBlock? Value { get; }

    /// <summary>Rows whose captions share one column width (the widest caption); null = own caption only.</summary>
    public IReadOnlyList<FieldRow>? Group { get; set; }

    /// <summary>Value column in 96-DPI units.</summary>
    public int ValueWidth { get; set; } = 64;

    /// <summary>Field width in 96-DPI units; 0 = fill.</summary>
    public int FieldWidth { get; set; }

    public int MeasureCaption() => WidthFor(Caption);

    protected override int Arrange(int width, bool apply)
    {
        int capW = Group is { Count: > 0 } g ? g.Max(r => r.MeasureCaption()) : MeasureCaption();
        int gap = Dp(12);
        int valW = Value != null ? Dp(ValueWidth) : 0;
        int fieldW = width - capW - gap - (valW > 0 ? valW + gap : 0);
        if (FieldWidth > 0) fieldW = Math.Min(fieldW, Dp(FieldWidth));
        int fieldH = HeightFor(Field, fieldW);
        int capH = HeightFor(Caption, capW);
        int h = Math.Max(fieldH, Math.Max(capH, Dp(32)));
        if (apply)
        {
            Place(Caption, 0, (h - capH) / 2, capW, capH);
            Place(Field, capW + gap, (h - fieldH) / 2, fieldW, fieldH);
            if (Value != null)
            {
                int vh = HeightFor(Value, valW);
                Place(Value, width - valW, (h - vh) / 2, valW, vh);
            }
        }
        return h;
    }

    /// <summary>Line the captions of these rows up (one column as wide as the widest).</summary>
    public static void AlignCaptions(params FieldRow[] rows)
    {
        foreach (var r in rows) r.Group = rows;
    }
}
