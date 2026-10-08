using System.Drawing.Drawing2D;

namespace HomePodCast.UI.Controls;

/// <summary>Horizontal peak meter: fast attack, slow release, perceptual (square-root) scale; red near clipping.</summary>
internal sealed class LevelMeter : FluentControl
{
    private float _shown;

    public LevelMeter()
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    /// <summary>Bar thickness in 96-DPI units (the control may be taller; the bar is centred).</summary>
    public int Thickness { get; set; } = 4;

    public void SetLevel(float peak)
    {
        float level = MathF.Sqrt(Math.Clamp(peak, 0f, 1f));
        float shown = level > _shown ? level : _shown * 0.85f + level * 0.15f;
        if (shown < 0.002f) shown = 0;
        if (Math.Abs(shown - _shown) < 0.001f) return;
        _shown = shown;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(120), Dp(Thickness + 4));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int t = Dp(Thickness);
        var r = new Rectangle(0, (Height - t) / 2, Width, t);
        Shapes.FillRound(g, P.MeterTrack, r, t / 2f);
        int w = (int)(Width * _shown);
        if (w <= 0) return;
        Shapes.FillRound(g, _shown > 0.95f ? P.Critical : P.Success, new Rectangle(r.X, r.Y, Math.Max(w, t), t), t / 2f);
    }
}

/// <summary>A small labelled number (stream statistics). Shows "—" when there is no value.</summary>
internal sealed class MetricTile : FluentControl
{
    private string _caption = "", _value = "—", _unit = "";

    public MetricTile(string caption, string unit = "")
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        _caption = caption;
        _unit = unit;
        AccessibleName = caption;
    }

    public string Caption => _caption;

    public void SetValue(string? value)
    {
        value ??= "—";
        if (value == _value) return;
        _value = value;
        AccessibleDescription = value == "—" ? value : $"{value} {_unit}".Trim();
        Invalidate();
    }

    public string ValueText => _value;

    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = Dp(12) + LineHeight(TextStyle.Caption) + Dp(4) + LineHeight(TextStyle.Metric) + Dp(12);
        int w = Math.Max(TextRenderer.MeasureText(_caption, StyleFont(TextStyle.Caption), Size.Empty, TextFlags.Measure).Width, Dp(60)) + Dp(28);
        return new Size(w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Shapes.Card(g, new Rectangle(0, 0, Width, Height), Dp(8), P.Card, P.CardBorder, Hairline);
        int x = Dp(14), y = Dp(12);
        int capH = LineHeight(TextStyle.Caption);
        TextRenderer.DrawText(g, _caption, StyleFont(TextStyle.Caption), new Rectangle(x, y, Width - x * 2, capH), P.TextSecondary, TextFlags.Line);
        y += capH + Dp(4);
        var valueFont = StyleFont(TextStyle.Metric);
        int valH = LineHeight(TextStyle.Metric);
        int valW = TextRenderer.MeasureText(_value, valueFont, Size.Empty, TextFlags.Measure).Width;
        TextRenderer.DrawText(g, _value, valueFont, new Rectangle(x, y, Width - x * 2, valH), P.Text, TextFlags.Line);
        if (_unit.Length > 0 && _value != "—")
        {
            var unitFont = StyleFont(TextStyle.Caption);
            int unitH = LineHeight(TextStyle.Caption);
            // Baseline-ish: bottom of the unit near the bottom of the number.
            TextRenderer.DrawText(g, _unit, unitFont, new Rectangle(x + valW + Dp(4), y + valH - unitH - Dp(3), Width, unitH),
                P.TextSecondary, TextFlags.Line);
        }
    }
}

/// <summary>A coloured dot for the stream state (green streaming, amber connecting, red retrying, grey idle).</summary>
internal sealed class StatusDot : FluentControl
{
    private Color _color = Icons.Idle;

    public StatusDot()
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    public Color DotColor
    {
        get => _color;
        set { if (_color != value) { _color = value; Invalidate(); } }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(10), Dp(10));

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_color);
        int d = Math.Min(Width, Height) - 1;
        e.Graphics.FillEllipse(b, (Width - d) / 2f, (Height - d) / 2f, d, d);
    }
}

/// <summary>A round badge with an icon-font glyph (the speaker on 首页).</summary>
internal sealed class GlyphBadge : FluentControl
{
    public GlyphBadge(string glyph, int size = 48)
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Glyph = glyph;
        BadgeSize = size;
    }

    public string Glyph { get; }
    public int BadgeSize { get; }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(BadgeSize), Dp(BadgeSize));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Math.Min(Width, Height);
        var r = new Rectangle((Width - d) / 2, (Height - d) / 2, d - 1, d - 1);
        using (var b = new SolidBrush(P.Subtle)) g.FillEllipse(b, r);
        using (var pen = new Pen(P.CardBorder, Hairline)) g.DrawEllipse(pen, r);
        Shapes.Glyph(g, Glyph, Theme.IconFont(BadgeSize / 2, DeviceDpi), r, P.Text);
    }
}

/// <summary>An app icon (from the executable), drawn smoothly at the control's size.</summary>
internal sealed class AppIconBox : FluentControl
{
    private Bitmap? _image;

    public AppIconBox(Icon? icon)
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        if (icon != null)
        {
            try { _image = icon.ToBitmap(); } catch { }
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(24), Dp(24));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int s = Math.Min(Width, Height);
        var r = new Rectangle((Width - s) / 2, (Height - s) / 2, s, s);
        if (_image != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_image, r);
        }
        else
        {
            Shapes.Glyph(g, HomePodCast.UI.Glyph.Volume, Theme.IconFont(16, DeviceDpi), r, P.TextSecondary);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A hairline across the width.</summary>
internal sealed class Divider : FluentControl
{
    public Divider()
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Hairline);

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(P.Divider);
}
