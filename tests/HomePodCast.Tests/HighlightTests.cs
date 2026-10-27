using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// Hover, pressed and selected fills are visible on the surface they are drawn on, in both themes (2026-10-27, the
/// maintainer: 「多处选项卡框选阴影不全」), and state colours follow a theme switch. "Visible" here: at least 8 levels
/// apart in one colour channel.
/// </summary>
[Collection("Layout work")]
public class HighlightTests
{
    private const int Visible = 8;

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Hover_pressed_and_selected_fills_differ_from_their_surface(string mode) => Sta.Run(() =>
    {
        using var theme = new ThemeScope(mode);
        var p = Theme.P;
        using var rail = new NavRail();
        // The rail, and 音箱信息's device list (navigation entries on the page).
        foreach (var surface in new[] { rail.SurfaceColor, p.Window })
        {
            AtLeast(Visible, Theme.NavHoverOn(surface), surface, "navigation hover");
            AtLeast(Visible, Theme.NavSelectedOn(surface), surface, "navigation selected");
        }
        // Segments on a segmented control's track, subtle buttons on the page, a card, a text box or a list.
        foreach (var surface in new[] { p.Control, p.Card, p.Window, p.Popup })
        {
            AtLeast(Visible, Theme.SubtleOn(surface), surface, "subtle hover");
            AtLeast(Visible, Theme.SubtleOn(surface, pressed: true), surface, "subtle pressed");
        }
        AtLeast(Visible, p.Subtle, p.Popup, "list highlight"); // FluentMenuRenderer
    });

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Selected_and_hovered_navigation_entries_show_a_box_on_the_rail(string mode) => Sta.Run(() =>
    {
        // 2026-10-27: the light rail is E6E6E6 and the selected entry was filled with E5E5E5: only the accent pill showed.
        using var theme = new ThemeScope(mode);
        using var host = new Panel { Size = new Size(240, 400) };
        using var rail = new NavRail();
        host.Controls.Add(rail);
        var home = rail.Add(Glyph.Home, "首页");
        var mixer = rail.Add(Glyph.Mixer, "混音");
        rail.SelectedIndex = 0;
        rail.SetBounds(0, 0, 240, 400);
        rail.PerformLayout();
        Assert.True(home.Width > 0 && mixer.Width > 0);
        AtLeast(Visible, FillOf(home), rail.SurfaceColor, "selected entry");
        Hover(mixer, true);
        AtLeast(Visible, FillOf(mixer), rail.SurfaceColor, "hovered entry");
        Hover(home, true);
        AtLeast(Visible, FillOf(home), rail.SurfaceColor, "selected entry under the mouse");

        static Color FillOf(NavItem item) => Pixel(item, item.Width - Theme.Dp(6, item.DeviceDpi), item.Height / 2);
    });

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void A_hovered_segment_and_the_password_eye_button_show_their_hover(string mode) => Sta.Run(() =>
    {
        // 2026-10-27: in dark, Subtle (0x353535) on the control colour (0x373737) of the track and the password box.
        using var theme = new ThemeScope(mode);
        using var seg = new Segmented("一", "二") { Size = new Size(200, 34) };
        typeof(Segmented).GetField("_hot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(seg, 1);
        var segment = ((Rectangle[])typeof(Segmented).GetMethod("Segments", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(seg, null)!)[1];
        AtLeast(Visible, Pixel(seg, segment.X + Theme.Dp(3, seg.DeviceDpi), seg.Height / 2), Theme.P.Control, "hovered segment");

        using var box = new PasswordBox("VLC") { Size = new Size(200, 32) };
        box.PerformLayout();
        var eye = TestApp.Field<FluentButton>(box, "_reveal");
        Assert.True(eye.Width > 0);
        Hover(eye, true);
        AtLeast(Visible, Pixel(eye, Theme.Dp(2, eye.DeviceDpi), eye.Height / 2), Theme.P.Control, "hovered eye button");
    });

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void The_selected_speaker_position_stands_out_more_than_a_hovered_one(string mode) => Sta.Run(() =>
    {
        // 2026-10-27: in dark the selected position (NavSelected, +4 over the card) was fainter than a hovered one (+18).
        using var theme = new ThemeScope(mode);
        using var host = new Panel { Size = new Size(400, 600) };
        using var room = new SpeakerRoom();
        host.Controls.Add(room);
        room.Update(CinemaChannels.FL | CinemaChannels.FR, [], null);
        room.Size = new Size(400, room.GetPreferredSize(new Size(400, 0)).Height);
        room.PerformLayout();
        Assert.Equal(SpeakerPosition.Left, room.SelectedPosition);
        var buttons = room.Controls.Cast<Control>().ToList();
        var selected = buttons.Single(b => b.AccessibleName!.StartsWith("前左："));
        var hovered = buttons.Single(b => b.AccessibleName!.StartsWith("前右："));
        Hover(hovered, true);
        var card = Theme.P.Card;
        Color selectedFill = FillOf(selected), hoverFill = FillOf(hovered);
        AtLeast(Visible, hoverFill, card, "hovered position");
        Assert.True(Distance(selectedFill, card) > Distance(hoverFill, card),
            $"selected {selectedFill} is not stronger than hovered {hoverFill} on {card}");

        // Clear of the dashed border and the centred caption.
        static Color FillOf(Control b) => Pixel(b, b.Width - Theme.Dp(5, b.DeviceDpi), Theme.Dp(10, b.DeviceDpi));
    });

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void A_disabled_list_entry_under_the_keyboard_keeps_its_highlight(string mode) => Sta.Run(() =>
    {
        using var theme = new ThemeScope(mode);
        using var combo = new FluentComboBox { Size = new Size(200, 32) };
        combo.Items.AddRange(["客厅", "卧室（不可用）", "书房"]);
        combo.IsItemEnabled = i => i != 1;
        using var menu = combo.BuildMenu();
        _ = menu.Handle; // laid out, never shown
        menu.PerformLayout();
        var item = menu.Items[1];
        Assert.False(item.Enabled);
        item.Select();
        Assert.True(item.Selected); // the arrow keys stop on a disabled entry (WinForms selects it)
        using var bitmap = new Bitmap(item.Width, item.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Theme.P.Popup);
            menu.Renderer.DrawMenuItemBackground(new ToolStripItemRenderEventArgs(g, item));
        }
        Assert.Equal(Theme.P.Subtle.ToArgb(), bitmap.GetPixel(item.Width / 2, item.Height / 2).ToArgb());
    });

    [Fact]
    public void Password_box_focus_line_reaches_the_rounded_corners() => Sta.Run(() =>
    {
        // Like HotkeyBox: the full width, clipped to the outline. Inset by the radius, the grey corners showed at both ends.
        using var box = new PasswordBox("VLC") { Size = new Size(200, 32) };
        int radius = Theme.Dp(4, box.DeviceDpi), y = box.Height - 1; // the line's full row (the top one is antialiased)
        using var bitmap = new Bitmap(box.Width, box.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Theme.P.Window);
            box.PaintBox(g, focused: true);
        }
        int accent = Theme.P.Accent.ToArgb();
        Assert.Equal(accent, bitmap.GetPixel(radius - 1, y).ToArgb());
        Assert.Equal(accent, bitmap.GetPixel(box.Width - radius, y).ToArgb());
        Assert.Equal(accent, bitmap.GetPixel(box.Width / 2, y).ToArgb());
        Assert.NotEqual(accent, bitmap.GetPixel(0, y).ToArgb()); // the corner stays round
    });

    [Fact]
    public void A_glyph_role_is_read_from_the_palette_when_painting() => Sta.Run(() =>
    {
        // 2026-10-27: 首页's mute button copied the light theme's red once and kept it on the dark card after a switch.
        using var theme = new ThemeScope(Theme.Light);
        using var button = new FluentButton("", ButtonKind.Subtle, Glyph.Mute) { GlyphRole = TextRole.Critical };
        var light = Theme.P.Critical;
        Assert.Equal(light, button.GlyphColorFor(Theme.P.Text));
        Theme.Mode = Theme.Dark;
        Assert.NotEqual(light, Theme.P.Critical);
        Assert.Equal(Theme.P.Critical, button.GlyphColorFor(Theme.P.Text));
        button.GlyphRole = null;
        Assert.Equal(Theme.P.Text, button.GlyphColorFor(Theme.P.Text));
    });

    [Fact]
    public void Home_mute_glyph_keeps_the_state_colour_of_the_current_theme() => Sta.Run(() =>
    {
        using var theme = new ThemeScope(Theme.Light);
        var app = TestApp.Create(TestApp.Config());
        using var window = new MainWindow(app);
        app.Controller.SetMuted(true);
        var home = TestApp.Field<HomePage>(window, "_home");
        home.ShowSoundOptions();
        var mute = TestApp.Field<FluentButton>(home, "_mute");
        Assert.Equal(TextRole.Critical, mute.GlyphRole);
        Assert.Equal(Theme.P.Critical, mute.GlyphColorFor(Theme.P.Text));
        Theme.Mode = Theme.Dark; // only the palette changes: no state, volume or page event follows
        Assert.Equal(Theme.P.Critical, mute.GlyphColorFor(Theme.P.Text));
    });

    private static void Hover(Control control, bool on) =>
        typeof(FluentControl).GetField("_hover", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, on);

    private static Color Pixel(Control control, int x, int y)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        return bitmap.GetPixel(x, y);
    }

    private static int Distance(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static void AtLeast(int levels, Color fill, Color surface, string what) =>
        Assert.True(Distance(fill, surface) >= levels, $"{what}: {fill} on {surface} differs by {Distance(fill, surface)}");

    private sealed class ThemeScope : IDisposable
    {
        private readonly string _before = Theme.Mode;
        internal ThemeScope(string mode) => Theme.Mode = mode;
        public void Dispose() => Theme.Mode = _before;
    }
}
