namespace HomePodCast.UI.Controls;

/// <summary>
/// A Fluent text box for typing a password: masked by default (the system bullet; Windows blocks copying it
/// while masked), with an eye button that shows or hides what was typed. It only ever holds what is being
/// typed: a saved password is never put back into it.
/// </summary>
internal sealed class PasswordBox : FluentControl, ISurface
{
    private readonly TextBox _box;
    private readonly FluentButton _reveal;

    public PasswordBox(string accessibleName)
    {
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            UseSystemPasswordChar = true,
            MaxLength = 256,
            AccessibleName = accessibleName,
            TabIndex = 0,
        };
        _reveal = new FluentButton("", ButtonKind.Subtle, Glyph.View) { ButtonHeight = 26, TabIndex = 1 };
        Controls.Add(_box);
        Controls.Add(_reveal);
        Cursor = Cursors.IBeam;
        _reveal.Cursor = Cursors.Default;

        _box.TextChanged += (_, _) => PasswordChanged?.Invoke(this, EventArgs.Empty);
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None) return;
            e.Handled = true;
            e.SuppressKeyPress = true; // no beep
            Submitted?.Invoke(this, EventArgs.Empty);
        };
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _reveal.Click += (_, _) => Revealed = !Revealed;
        ShowRevealState();
        ApplyColors();
    }

    /// <summary>What has been typed.</summary>
    public string Password
    {
        get => _box.Text;
        internal set => _box.Text = value;
    }

    /// <summary>The typed text is shown in clear (the eye button); false = masked.</summary>
    public bool Revealed
    {
        get => !_box.UseSystemPasswordChar;
        set
        {
            if (Revealed == value) return;
            _box.UseSystemPasswordChar = !value;
            ShowRevealState();
        }
    }

    /// <summary>The typed text changed.</summary>
    public event EventHandler? PasswordChanged;

    /// <summary>Enter was pressed in the box.</summary>
    public event EventHandler? Submitted;

    /// <summary>Forget what was typed and mask again.</summary>
    public void Clear()
    {
        _box.Clear();
        Revealed = false;
    }

    public Color SurfaceColor => P.Control;

    private void ShowRevealState()
    {
        _reveal.Glyph = Revealed ? Glyph.Hide : Glyph.View;
        _reveal.AccessibleName = Revealed ? L.T("隐藏密码") : L.T("显示密码");
    }

    private void ApplyColors()
    {
        _box.BackColor = P.Control;
        _box.ForeColor = P.Text;
    }

    protected override void OnThemeChanged()
    {
        ApplyColors();
        base.OnThemeChanged();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(200), Dp(32));

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var font = StyleFont(TextStyle.Body);
        if (!ReferenceEquals(_box.Font, font)) _box.Font = font;
        int button = Dp(_reveal.ButtonHeight);
        int pad = Dp(10);
        _reveal.SetBounds(Width - button - Dp(3), (Height - button) / 2, button, button);
        int boxH = _box.PreferredHeight;
        _box.SetBounds(pad, (Height - boxH) / 2, Math.Max(0, _reveal.Left - pad - Dp(4)), boxH);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        float radius = Dp(4);
        Shapes.FillRound(g, P.Control, r, radius);
        Shapes.BorderRound(g, P.ControlBorder, r, radius, Hairline);
        if (_box.Focused)
        {
            // Fluent text box: an accent line along the bottom while it has focus.
            int inset = (int)radius;
            using var accent = new SolidBrush(P.Accent);
            g.FillRectangle(accent, inset, Height - Dp(2), Width - inset * 2, Dp(2));
        }
    }
}
