using System.Globalization;
using System.Text.RegularExpressions;
using HomePodCast.Update;

namespace HomePodCast.UI.Controls;

/// <summary>Inert Markdown runs painted with the app's fonts. Links have keyboard and accessibility actions.</summary>
internal sealed class NotesParagraph : FluentControl
{
    private readonly NotesBlock _block;
    private readonly Action<string> _open;
    private readonly List<(string Text, Font Font, Rectangle Bounds, string? Url)> _pieces = [];
    private readonly string[] _links;
    private int _focusedLink;
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public NotesParagraph(NotesBlock block, Action<string> open)
    {
        _block = block;
        _open = open;
        _links = block.Runs.Where(r => r.Url != null).Select(r => r.Url!).Distinct().ToArray();
        Text = (block.ListDepth >= 0 ? "• " : "") + string.Concat(block.Runs.Select(r => r.Text));
        AccessibleRole = AccessibleRole.StaticText;
        TabStop = _links.Length > 0;
        SetStyle(ControlStyles.Selectable, TabStop);
    }

    private int Arrange(int width)
    {
        _pieces.Clear();
        int indent = _block.ListDepth >= 0 ? Dp(16 + _block.ListDepth * 14) : 0;
        int x = indent, y = 0;
        int height = TextRenderer.MeasureText("Ag国", Theme.Font(TextStyle.Body, DeviceDpi), Size.Empty, Flags).Height + Dp(3);
        if (_block.ListDepth >= 0)
            _pieces.Add(("•", Theme.Font(TextStyle.Body, DeviceDpi), new Rectangle(indent - Dp(14), 0, Dp(14), height), null));
        foreach (var run in _block.Runs)
        {
            var style = _block.Heading is 1 or 2 ? TextStyle.Subtitle : _block.Heading == 3 || run.Bold ? TextStyle.BodyStrong : TextStyle.Body;
            var font = Theme.FontFor(style, DeviceDpi, run.Text);
            height = Math.Max(height, TextRenderer.MeasureText("Ag国", font, Size.Empty, Flags).Height + Dp(3));
            void Place(string token)
            {
                int w = TextRenderer.MeasureText(token, font, Size.Empty, Flags).Width;
                if (x > indent && x + w > width) { x = indent; y += height; }
                if (x == indent && string.IsNullOrWhiteSpace(token)) return;
                _pieces.Add((token, font, new Rectangle(x, y, w, height), run.Url));
                x += w;
            }
            foreach (Match token in Regex.Matches(run.Text, @"[!-~]+|\s+|[^!-~\s]"))
            {
                if (TextRenderer.MeasureText(token.Value, font, Size.Empty, Flags).Width <= width - indent) Place(token.Value);
                else
                {
                    var elements = StringInfo.GetTextElementEnumerator(token.Value);
                    while (elements.MoveNext()) Place(elements.GetTextElement());
                }
            }
        }
        return y + height;
    }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Arrange(Math.Max(Dp(80), proposedSize.Width)));

    protected override void OnPaint(PaintEventArgs e)
    {
        Arrange(Width);
        foreach (var piece in _pieces)
        {
            TextRenderer.DrawText(e.Graphics, piece.Text, piece.Font, piece.Bounds, piece.Url != null ? P.AccentText : P.Text, Flags);
            if (piece.Url != null)
            {
                using var pen = new Pen(P.AccentText);
                e.Graphics.DrawLine(pen, piece.Bounds.Left, piece.Bounds.Bottom - Dp(3), piece.Bounds.Right, piece.Bounds.Bottom - Dp(3));
                if (Focused && piece.Url == _links[_focusedLink]) ControlPaint.DrawFocusRectangle(e.Graphics, piece.Bounds);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _pieces.Any(p => p.Url != null && p.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var url = _pieces.FirstOrDefault(p => p.Url != null && p.Bounds.Contains(e.Location)).Url;
        if (url != null && ReleaseNotes.CanOpen(url)) _open(url);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_links.Length == 0) return;
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            _focusedLink = (_focusedLink + (e.KeyCode == Keys.Right ? 1 : _links.Length - 1)) % _links.Length;
            Invalidate(); e.Handled = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space) { _open(_links[_focusedLink]); e.Handled = true; }
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new ParagraphAccessible(this);
    private sealed class ParagraphAccessible(NotesParagraph owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner._links.Length;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner._links.Length ? new LinkAccessible(owner, index) : null;
    }
    private sealed class LinkAccessible(NotesParagraph owner, int index) : AccessibleObject
    {
        private string Url => owner._links[index];
        public override string? Name { get => string.Concat(owner._block.Runs.Where(r => r.Url == Url).Select(r => r.Text)); set { } }
        public override string? Value => Url;
        public override AccessibleRole Role => AccessibleRole.Link;
        public override AccessibleObject? Parent => owner.AccessibilityObject;
        public override Rectangle Bounds => owner.RectangleToScreen(owner._pieces.Where(p => p.Url == Url).Select(p => p.Bounds).Aggregate(Rectangle.Empty,
            (a, b) => a.IsEmpty ? b : Rectangle.Union(a, b)));
        public override void DoDefaultAction() { if (ReleaseNotes.CanOpen(Url)) owner._open(Url); }
        public override string? DefaultAction => L.T("打开");
    }
}

internal sealed class DownloadProgress : FluentControl
{
    private double _value;
    public DownloadProgress() { TabStop = false; AccessibleRole = AccessibleRole.ProgressBar; }
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); AccessibleName = $"{(int)(_value * 100)}%"; Invalidate(); } }
    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(8));
    protected override void OnPaint(PaintEventArgs e)
    {
        Shapes.FillRound(e.Graphics, P.Track, ClientRectangle, Dp(3));
        if (_value > 0) Shapes.FillRound(e.Graphics, P.Accent, new Rectangle(0, 0, (int)(Width * _value), Height), Dp(3));
    }
}
