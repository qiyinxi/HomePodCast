using System.Drawing.Drawing2D;
using HomePodCast.Audio;

namespace HomePodCast.UI.Controls;

/// <summary>A view of the existing routing controls, not another saved layout.</summary>
internal sealed record RoomOutput(string Id, string Name, SpeakerPosition Position, int DelayMs, int VolumeOffset,
    bool Editable, SpeakerPosition[] Positions);

internal static class SpeakerRoomLayout
{
    internal static SpeakerPosition[] Positions(uint mask, IEnumerable<RoomOutput> outputs)
    {
        if ((mask & ~(CinemaChannels.FL | CinemaChannels.FR | CinemaChannels.FC)) == 0)
            return [SpeakerPosition.Left, SpeakerPosition.Right, SpeakerPosition.Stereo];
        var positions = CinemaChannels.Positions(mask).Select(Position).Where(p => p != null).Select(p => p!.Value)
            .Concat(outputs.Select(o => o.Position)).Append(SpeakerPosition.Stereo).Distinct().ToArray();
        return Enum.GetValues<SpeakerPosition>().Where(positions.Contains).ToArray();
    }

    internal static SpeakerPosition? Position(uint channel) => channel switch
    {
        CinemaChannels.FL => SpeakerPosition.Left, CinemaChannels.FR => SpeakerPosition.Right,
        CinemaChannels.FC => SpeakerPosition.Center, CinemaChannels.LFE => SpeakerPosition.Subwoofer,
        CinemaChannels.BL => SpeakerPosition.RearLeft, CinemaChannels.BR => SpeakerPosition.RearRight,
        CinemaChannels.SL => SpeakerPosition.SideLeft, CinemaChannels.SR => SpeakerPosition.SideRight, _ => null,
    };

    internal static uint Channel(SpeakerPosition position) => position switch
    {
        SpeakerPosition.Left => CinemaChannels.FL, SpeakerPosition.Right => CinemaChannels.FR,
        SpeakerPosition.Center => CinemaChannels.FC, SpeakerPosition.Subwoofer => CinemaChannels.LFE,
        SpeakerPosition.RearLeft => CinemaChannels.BL, SpeakerPosition.RearRight => CinemaChannels.BR,
        SpeakerPosition.SideLeft => CinemaChannels.SL, SpeakerPosition.SideRight => CinemaChannels.SR, _ => 0,
    };

    // Dolby's floor-speaker guides: front ±30°, 5.1 surrounds ±110°, 7.1 sides ±100° and rears ±145°.
    // https://www.dolby.com/about/support/guide/surround-sound-speaker-setup/
    // These are schematic positions, not measured distances or acoustic delay estimates.
    internal static double Angle(SpeakerPosition position, bool bothSurroundPairs) => position switch
    {
        SpeakerPosition.Left => -30, SpeakerPosition.Right => 30, SpeakerPosition.Center => 0,
        SpeakerPosition.SideLeft => bothSurroundPairs ? -100 : -110,
        SpeakerPosition.SideRight => bothSurroundPairs ? 100 : 110,
        SpeakerPosition.RearLeft => bothSurroundPairs ? -145 : -110,
        SpeakerPosition.RearRight => bothSurroundPairs ? 145 : 110, _ => 0,
    };

    internal static PointF Listener(Size size) => new(size.Width * .5f, size.Height * .46f);

    internal static int ButtonWidth(int width, int dpi) => Math.Min(Theme.Dp(68, dpi), Math.Max(Theme.Dp(48, dpi), width / 6));

    internal static PointF Location(SpeakerPosition position, bool bothSurroundPairs, Size size, int dpi = 96)
    {
        if (position == SpeakerPosition.Stereo) return new(size.Width * .5f, size.Height * .92f);
        if (position == SpeakerPosition.Subwoofer) return new(Math.Max(ButtonWidth(size.Width, dpi) / 2f + Theme.Dp(4, dpi), size.Width * .09f), Theme.Dp(30, dpi));
        double angle = Angle(position, bothSurroundPairs) * Math.PI / 180;
        float radius = Math.Min(size.Width / 2f - ButtonWidth(size.Width, dpi) / 2f - Theme.Dp(12, dpi), size.Height * .34f);
        // The same pixel radius on both axes keeps the displayed angles true at every aspect ratio. Front L/R sit
        // on a smaller circle so their labels cannot collide with C; radius never means distance or delay.
        if (position is SpeakerPosition.Left or SpeakerPosition.Right) radius *= .65f;
        var center = Listener(size);
        return new(center.X + (float)(radius * Math.Sin(angle)), center.Y - (float)(radius * Math.Cos(angle)));
    }

    internal static string Name(SpeakerPosition position) => position switch
    {
        SpeakerPosition.Left => L.T("前左"), SpeakerPosition.Right => L.T("前右"), _ => GroupPanel.PositionName(position),
    };

    internal static string Short(SpeakerPosition position) => position switch
    {
        SpeakerPosition.Left => "FL", SpeakerPosition.Right => "FR", SpeakerPosition.Center => "C",
        SpeakerPosition.Subwoofer => "LFE", SpeakerPosition.RearLeft => "BL", SpeakerPosition.RearRight => "BR",
        SpeakerPosition.SideLeft => "SL", SpeakerPosition.SideRight => "SR", _ => "L + R",
    };

    /// <summary>Use the actual mixer route, including 5.1's split between two surround pairs and full-stereo downmix.</summary>
    internal static string EmptyText(SpeakerPosition position, uint mask, IReadOnlyList<RoomOutput> outputs)
    {
        uint channel = Channel(position);
        if (channel == 0 || (mask & channel) == 0) return L.T("此音源没有这个声道");
        var destinations = CinemaMatrix.Route(channel, mask,
            role => outputs.Any(o => o.Position != SpeakerPosition.Stereo && GroupSettings.ToCinema(o.Position) == role))
            .Where(route => outputs.Any(o => GroupSettings.ToCinema(o.Position) == route.Role))
            .Select(route => Name(GroupSettings.FromCinema(route.Role))).ToList();
        if (outputs.Any(o => o.Position == SpeakerPosition.Stereo)) destinations.Add(Name(SpeakerPosition.Stereo));
        return destinations.Count == 0 ? L.T("未分配：没有可播放的音箱")
            : L.F("未分配：混入{0}", string.Join(" / ", destinations.Distinct()));
    }
}

/// <summary>Top-down room drawn with the same palette and DPI units as the Fluent controls. Each position is a real
/// accessible child button: tab, arrows, Space/Enter and UI Automation use the same selection path as a mouse.</summary>
internal sealed class SpeakerRoom : LayoutPanel
{
    private readonly List<PositionButton> _buttons = [];
    private IReadOnlyList<RoomOutput> _outputs = [];
    private uint _mask;
    private string? _measuring;
    internal SpeakerPosition SelectedPosition { get; private set; } = SpeakerPosition.Left;
    internal event Action<SpeakerPosition>? PositionSelected;

    internal SpeakerRoom() { AccessibleName = L.T("音箱布局图"); AccessibleRole = AccessibleRole.Grouping; }

    internal void Update(uint mask, IReadOnlyList<RoomOutput> outputs, string? measuring)
    {
        _mask = mask; _outputs = outputs; _measuring = measuring;
        var positions = SpeakerRoomLayout.Positions(mask, outputs);
        if (!positions.SequenceEqual(_buttons.Select(b => b.Position)))
        {
            foreach (var button in _buttons) button.Dispose();
            _buttons.Clear();
            foreach (var position in positions)
            {
                var button = new PositionButton(this, position);
                _buttons.Add(button); Controls.Add(button);
            }
        }
        if (!positions.Contains(SelectedPosition)) SelectedPosition = SpeakerPosition.Left;
        foreach (var button in _buttons) button.RefreshDisplay();
        Invalidate();
        Relayout(Parent);
    }

    internal void SelectPosition(SpeakerPosition position)
    {
        if (!_buttons.Any(b => b.Position == position)) return;
        SelectedPosition = position;
        foreach (var button in _buttons) button.RefreshDisplay();
        PositionSelected?.Invoke(position);
    }

    private void MoveSelection(PositionButton from, Keys direction)
    {
        var origin = SpeakerRoomLayout.Location(from.Position, BothPairs, ClientSize, DeviceDpi);
        var candidates = _buttons.Where(b => b != from).Select(b => (Button: b, Point: SpeakerRoomLayout.Location(b.Position, BothPairs, ClientSize, DeviceDpi)))
            .Select(p => (p.Button, X: p.Point.X - origin.X, Y: p.Point.Y - origin.Y));
        var next = candidates.Where(p => direction switch
            { Keys.Left => p.X < -.01, Keys.Right => p.X > .01, Keys.Up => p.Y < -.01, _ => p.Y > .01 })
            .OrderBy(p => p.X * p.X + p.Y * p.Y).FirstOrDefault();
        if (next.Button != null) { SelectPosition(next.Button.Position); next.Button.Focus(); }
    }

    private bool BothPairs => _buttons.Any(b => b.Position == SpeakerPosition.SideLeft || b.Position == SpeakerPosition.SideRight)
        && _buttons.Any(b => b.Position == SpeakerPosition.RearLeft || b.Position == SpeakerPosition.RearRight);

    protected override int Arrange(int width, bool apply)
    {
        int height = Dp(360), bw = SpeakerRoomLayout.ButtonWidth(width, DeviceDpi), bh = Dp(44);
        if (apply)
            foreach (var button in _buttons)
            {
                var p = SpeakerRoomLayout.Location(button.Position, BothPairs, new Size(width, height), DeviceDpi);
                Place(button, (int)Math.Round(p.X) - bw / 2, (int)Math.Round(p.Y) - bh / 2, bw, bh);
            }
        return height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Shapes.BorderRound(g, P.ControlBorder, new Rectangle(Dp(2), Dp(2), Width - Dp(4), Height - Dp(4)), Dp(12), Dp(1));
        var center = SpeakerRoomLayout.Listener(ClientSize);
        using var guide = new Pen(P.Divider, Dp(1)) { DashStyle = DashStyle.Dot };
        foreach (var button in _buttons.Where(b => b.Position is not (SpeakerPosition.Stereo or SpeakerPosition.Subwoofer)))
            g.DrawLine(guide, center, new PointF(button.Left + button.Width / 2f, button.Top + button.Height / 2f));
        Shapes.FillRound(g, P.Control, new RectangleF(center.X - Dp(23), center.Y - Dp(18), Dp(46), Dp(36)), Dp(10));
        using (var person = new Pen(P.TextSecondary, Dp(1)))
        {
            g.DrawEllipse(person, center.X - Dp(5), center.Y - Dp(12), Dp(10), Dp(10));
            g.DrawArc(person, center.X - Dp(12), center.Y + Dp(1), Dp(24), Dp(18), 180, 180);
        }
        TextRenderer.DrawText(g, L.T("聆听位置"), Theme.Font(TextStyle.Caption, DeviceDpi),
            new Rectangle(0, (int)center.Y + Dp(22), Width, Dp(35)), P.TextSecondary,
            TextFlags.Line | TextFormatFlags.HorizontalCenter);
    }

    private sealed class PositionButton : FluentControl
    {
        private readonly SpeakerRoom _room;
        internal SpeakerPosition Position { get; }
        private string _caption = "", _names = "";
        private bool _assigned, _measuring;
        internal PositionButton(SpeakerRoom room, SpeakerPosition position)
        {
            _room = room; Position = position;
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true; AccessibleRole = AccessibleRole.PushButton;
        }
        internal void RefreshDisplay()
        {
            var assigned = _room._outputs.Where(o => o.Position == Position).ToArray();
            _assigned = assigned.Length > 0;
            _measuring = assigned.Any(o => o.Id == _room._measuring);
            _caption = SpeakerRoomLayout.Short(Position);
            _names = _assigned ? string.Join(" / ", assigned.Select(o => o.Name)) : L.T("未分配");
            AccessibleName = _assigned ? L.F("{0}：{1}", SpeakerRoomLayout.Name(Position),
                string.Join(" / ", assigned.Select(o => L.F("{0}，延迟 {1} 毫秒", o.Name, o.DelayMs))))
                : L.F("{0}：{1}", SpeakerRoomLayout.Name(Position), SpeakerRoomLayout.EmptyText(Position, _room._mask, _room._outputs));
            if (_measuring) AccessibleName = L.F("{0}：正在测量", AccessibleName);
            Text = _caption;
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            Invalidate();
        }
        protected override void OnClick(EventArgs e) { base.OnClick(e); _room.SelectPosition(Position); }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Modifiers != Keys.None) return;
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) _room.MoveSelection(this, e.KeyCode);
            else if (e.KeyCode is Keys.Enter or Keys.Space) _room.SelectPosition(Position);
            else return;
            e.Handled = true; e.SuppressKeyPress = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var bounds = new Rectangle(0, 0, Width, Height);
            bool selected = Position == _room.SelectedPosition;
            Shapes.FillRound(g, selected ? P.NavSelected : IsHover ? P.ControlHover : P.Card, bounds, Dp(6));
            using var pen = new Pen(_measuring ? P.Caution : selected ? P.Accent : P.ControlBorder, Dp(_measuring ? 2 : 1))
                { DashStyle = _assigned ? DashStyle.Solid : DashStyle.Dash };
            Shapes.DrawRound(g, pen, RectangleF.Inflate(bounds, -Dp(1), -Dp(1)), Dp(6));
            Shapes.Glyph(g, Glyph.Speaker, Theme.IconFont(12, DeviceDpi), new Rectangle(Dp(3), Dp(2), Dp(12), Dp(20)), P.TextSecondary);
            TextRenderer.DrawText(g, _caption, StyleFont(TextStyle.BodyStrong), new Rectangle(Dp(16), Dp(2), Width - Dp(19), Dp(20)), P.Text,
                TextFlags.Line | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, _names, StyleFont(TextStyle.Caption), new Rectangle(Dp(3), Dp(23), Width - Dp(6), Dp(18)), P.TextSecondary,
                TextFlags.Line | TextFormatFlags.HorizontalCenter);
            if (FocusVisible) DrawFocusRing(g, bounds, Dp(6));
        }
        protected override AccessibleObject CreateAccessibilityInstance() => new PositionAccessible(this);
        private sealed class PositionAccessible(PositionButton owner) : ControlAccessibleObject(owner)
        {
            public override string DefaultAction => L.T("选择");
            public override void DoDefaultAction()
            {
                if (owner.IsHandleCreated) owner.BeginInvoke(() => owner._room.SelectPosition(owner.Position));
            }
        }
    }
}
