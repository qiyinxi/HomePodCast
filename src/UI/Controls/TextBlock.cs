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
    private bool _parentPainted, _shown = true;
    private readonly bool _nativeWindow;

    /// <summary>
    /// A layout item drawn by its LayoutPanel, with an MSAA/UIA child supplied by that panel. Keeping the managed
    /// control preserves layout and binding, but its WinForms visibility stays false so CreateControl never makes a HWND.
    /// Native services such as ToolTip explicitly request a window at construction.
    /// </summary>
    internal bool ParentPainted => _parentPainted;

    protected override void OnParentChanged(EventArgs e)
    {
        // Also supports the few plain WinForms hosts. Choose before Controls.Add can create the child's window,
        // and leave detached/disposed items alone. A native text reparented after creation keeps its window.
        if (Parent != null)
        {
            _parentPainted = !_nativeWindow && Parent is LayoutPanel && !IsHandleCreated;
            base.SetVisibleCore(_parentPainted ? false : _shown);
        }
        base.OnParentChanged(e);
    }

    // Control.Visible describes the native child; this describes the text on its parent's surface.
    public new bool Visible
    {
        get => _parentPainted ? _shown && (Parent?.Visible ?? true) : base.Visible;
        set => SetVisibleCore(value);
    }

    // A control without a HWND never receives Windows' DPI messages. Its host is the authority for font measurement.
    public new int DeviceDpi => _parentPainted && Parent != null ? Parent.DeviceDpi : base.DeviceDpi;

    protected override void SetVisibleCore(bool value)
    {
        bool changed = _shown != value;
        _shown = value;
        if (!_parentPainted) base.SetVisibleCore(value);
        else if (changed)
        {
            OnVisibleChanged(EventArgs.Empty);
            Parent?.Invalidate(Bounds);
            (Parent as LayoutPanel)?.PaintedTextChanged(this, AccessibleEvents.Reorder);
        }
    }

    private void Repaint()
    {
        if (_parentPainted) Parent?.Invalidate(Bounds);
        else Invalidate();
    }

    protected override void OnThemeChanged() => Repaint();

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Repaint();
        if (_parentPainted) (Parent as LayoutPanel)?.PaintedTextChanged(this, AccessibleEvents.StateChange);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        var old = Bounds;
        base.SetBoundsCore(x, y, width, height, specified);
        if (_parentPainted && old != Bounds)
        {
            Parent?.Invalidate(Rectangle.Union(old, Bounds));
            (Parent as LayoutPanel)?.PaintedTextChanged(this, AccessibleEvents.LocationChange);
        }
    }

    public TextBlock(string text = "", TextStyle style = TextStyle.Body, TextRole role = TextRole.Primary, bool wrap = false,
        bool nativeWindow = false)
    {
        _nativeWindow = nativeWindow;
        _parentPainted = !nativeWindow;
        if (_parentPainted) base.SetVisibleCore(false);
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
        set { if (_role != value) { _role = value; Repaint(); } }
    }

    /// <summary>An explicit colour (e.g. a status colour that is not a theme role); null = <see cref="Role"/>.</summary>
    public Color? CustomColor
    {
        get => _color;
        set { if (_color != value) { _color = value; Repaint(); } }
    }

    public bool Wrap
    {
        get => _wrap;
        set { if (_wrap != value) { _wrap = value; Changed(); } }
    }

    public HorizontalAlignment Align { get; set; } = HorizontalAlignment.Left;

    public Font TextFont => Theme.FontFor(_style, DeviceDpi, Text);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Changed();
        if (_parentPainted) (Parent as LayoutPanel)?.PaintedTextChanged(this, AccessibleEvents.NameChange);
    }

    private void Changed()
    {
        Repaint();
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
            var s = TextMeasure.Size(Text, font, new Size(proposedSize.Width, int.MaxValue), TextFlags.Wrap);
            return new Size(proposedSize.Width, s.Height + 1);
        }
        var line = TextMeasure.Size(Text, font, Size.Empty, TextFlags.Measure);
        int lineHeight = TextMeasure.Size("Ag国", Theme.Font(_style, DeviceDpi), Size.Empty, TextFlags.Measure).Height;
        return new Size(line.Width + 1, Math.Max(line.Height, lineHeight) + 1);
    }

    protected override void OnPaint(PaintEventArgs e)
        => Draw(e.Graphics, ClientRectangle);

    internal void DrawInParent(Graphics graphics)
    {
        if (Visible && !Collapsed) Draw(graphics, Bounds);
    }

    private void Draw(Graphics graphics, Rectangle bounds)
    {
        var color = !Enabled ? P.TextDisabled : _color ?? ColorOf(_role);
        var align = Align switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        TextRenderer.DrawText(graphics, Text, TextFont, bounds, color,
            (_wrap ? TextFlags.Wrap : TextFlags.Line) | align | TextFormatFlags.PreserveGraphicsClipping);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => _parentPainted
        ? new PaintedTextAccessible(this) : base.CreateAccessibilityInstance();

    private sealed class PaintedTextAccessible(TextBlock owner) : AccessibleObject
    {
        public override string? Name { get => owner.AccessibleName ?? owner.Text; set => owner.AccessibleName = value; }
        public override string? Description => owner.AccessibleDescription;
        public override string? Value => owner.Text;
        public override AccessibleRole Role => AccessibleRole.StaticText;
        public override AccessibleObject? Parent => owner.Parent?.AccessibilityObject;
        public override Rectangle Bounds => owner.Parent is { IsHandleCreated: true } host
            ? host.RectangleToScreen(owner.Bounds) : Rectangle.Empty;
        public override AccessibleStates State => (owner.Enabled ? AccessibleStates.None : AccessibleStates.Unavailable)
            | (owner.Visible && !owner.Collapsed ? AccessibleStates.None : AccessibleStates.Invisible)
            | (VisibleBounds.IsEmpty ? AccessibleStates.Offscreen : AccessibleStates.None);
        public override int GetChildCount() => 0;
        private Rectangle VisibleBounds
        {
            get
            {
                if (!owner.Visible || owner.Collapsed) return Rectangle.Empty;
                var bounds = Bounds;
                for (var host = owner.Parent; host != null; host = host.Parent)
                {
                    if (!host.IsHandleCreated) return Rectangle.Empty;
                    bounds.Intersect(host.RectangleToScreen(host.ClientRectangle));
                }
                return bounds;
            }
        }
        public override AccessibleObject? HitTest(int x, int y) => VisibleBounds.Contains(x, y) ? this : null;
        public override AccessibleObject? Navigate(AccessibleNavigation direction)
        {
            if (Parent is not { } parent) return null;
            for (int i = 0; i < parent.GetChildCount(); i++)
                if (ReferenceEquals(parent.GetChild(i), this))
                    return direction switch
                    {
                        AccessibleNavigation.Next => parent.GetChild(i + 1),
                        AccessibleNavigation.Previous => parent.GetChild(i - 1),
                        _ => null,
                    };
            return null;
        }
    }
}
