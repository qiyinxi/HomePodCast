using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class PaintedContainerTests
{
    [Fact]
    public void Display_only_subtrees_have_no_windows_and_keep_accessible_hierarchy_and_screen_bounds() => Sta.Run(() =>
    {
        using var host = new StackPanel { Size = new Size(500, 350) };
        var text = new TextBlock("nested text");
        var icon = new GlyphBadge(Glyph.Speaker);
        var metric = new MetricTile("count");
        var row = Ui.Row(text, icon, text);
        var card = Ui.Card(row, metric);
        host.Controls.Add(card);
        LayoutPanel.Place(card, 13, 15, 450, LayoutPanel.HeightFor(card, 450));
        LayoutPanel.CreateWindows(host, () => false);
        Assert.True(host.IsHandleCreated);
        Assert.All(All(card), control => Assert.False(control.IsHandleCreated, control.GetType().Name));
        Assert.True(card.ParentPainted && row.ParentPainted && icon.ParentPainted && metric.ParentPainted);
        var root = host.AccessibilityObject;
        Assert.Same(card.AccessibilityObject, root.GetChild(0));
        Assert.Same(row.AccessibilityObject, card.AccessibilityObject.GetChild(0));
        Assert.Same(text.AccessibilityObject, row.AccessibilityObject.GetChild(1));
        Assert.Same(row.AccessibilityObject, text.AccessibilityObject.Parent);
        var location = host.PointToScreen(new Point(card.Left + row.Left + text.Left, card.Top + row.Top + text.Top));
        Assert.Equal(new Rectangle(location, text.Size), text.AccessibilityObject.Bounds);
        Assert.Same(text.AccessibilityObject, root.HitTest(location.X + 1, location.Y + 1));
        metric.SetValue("12");
        Assert.Equal("12", metric.AccessibilityObject.Value);
        Assert.Equal("12", metric.AccessibilityObject.Description);
    });

    [Fact]
    public void Adding_a_native_input_promotes_its_ancestors_and_preserves_the_managed_parent_and_tab_stop() => Sta.Run(() =>
    {
        using var host = new StackPanel { Size = new Size(400, 300) };
        var row = Ui.Row(null, new TextBlock("caption"));
        var card = Ui.Card(row);
        host.Controls.Add(card);
        LayoutPanel.Place(host, 0, 0, 400, 300);
        LayoutPanel.CreateWindows(host, () => false);
        Assert.False(row.IsHandleCreated || card.IsHandleCreated);
        var button = new FluentButton("action") { TabIndex = 7 };
        row.Controls.Add(button);
        LayoutPanel.CreateWindows(host, () => false);
        Assert.True(card.IsHandleCreated && row.IsHandleCreated && button.IsHandleCreated);
        Assert.False(card.ParentPainted || row.ParentPainted);
        Assert.True(button.TabStop);
        Assert.Equal(7, button.TabIndex);
        Assert.Same(row, button.Parent);
        Assert.Same(card, row.Parent);
        Assert.Equal(row.Handle, GetParent(button.Handle));
        Assert.False(row.Controls.OfType<TextBlock>().Single().IsHandleCreated);
    });

    [Fact]
    public void A_detached_dynamic_subtree_does_not_create_windows_before_the_host_can_lay_it_out() => Sta.Run(() =>
    {
        using var root = new StackPanel();
        var row = Ui.Row(null, new TextBlock("caption"));
        var card = Ui.Card(row);
        root.Controls.Add(card);
        row.Controls.Add(new FluentButton("added later"));
        Assert.All(All(root), control => Assert.False(control.IsHandleCreated, control.GetType().Name));
        LayoutPanel.Place(root, 0, 0, 400, 300);
        Assert.All(All(root), control => Assert.False(control.IsHandleCreated, control.GetType().Name));
    });

    [Fact]
    public void Nested_visual_changes_invalidate_the_native_surface_and_hidden_groups_leave_accessibility() => Sta.Run(() =>
    {
        using var host = new StackPanel { Size = new Size(400, 300) };
        var metric = new MetricTile("count");
        var card = Ui.Card(metric);
        host.Controls.Add(card);
        LayoutPanel.Place(host, 0, 0, 400, 300);
        LayoutPanel.CreateWindows(host, () => false);
        var invalidated = new List<Rectangle>();
        host.Invalidated += (_, e) => invalidated.Add(e.InvalidRect);
        metric.SetValue("3");
        var expected = metric.Bounds;
        expected.Offset(card.Location);
        Assert.Contains(invalidated, bounds => bounds.Contains(expected));
        card.Visible = false;
        Assert.False(metric.Visible);
        Assert.Equal(0, host.AccessibilityObject.GetChildCount());
        card.Visible = true;
        Assert.True(metric.Visible);
        Assert.Equal(1, host.AccessibilityObject.GetChildCount());
        Assert.False(card.IsHandleCreated || metric.IsHandleCreated);
        card.Enabled = false;
        Assert.True(metric.AccessibilityObject.State.HasFlag(AccessibleStates.Unavailable));
        card.Collapsed = true;
        Assert.Equal(0, host.AccessibilityObject.GetChildCount());
    });

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Parent_painting_matches_native_cards_icons_wrapped_text_and_indicators_pixel_for_pixel(string mode) => Sta.Run(() =>
    {
        using var theme = new ThemeScope(mode);
        using var expectedHost = Build(native: true);
        using var paintedHost = Build(native: false);
        Assert.Equal(All(expectedHost).Select(c => c.Bounds), All(paintedHost).Select(c => c.Bounds));
        using var expected = new Bitmap(expectedHost.Width, expectedHost.Height);
        using var actual = new Bitmap(paintedHost.Width, paintedHost.Height);
        expectedHost.DrawToBitmap(expected, expectedHost.ClientRectangle);
        paintedHost.DrawToBitmap(actual, paintedHost.ClientRectangle);
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
            if (expected.GetPixel(x, y) != actual.GetPixel(x, y))
            {
                expected.Save(Path.Combine(AppContext.BaseDirectory, "native-container.png"));
                actual.Save(Path.Combine(AppContext.BaseDirectory, "painted-container.png"));
                Assert.Fail($"Different pixel at {x},{y}: " + string.Join("; ", All(paintedHost).Select(c => $"{c.GetType().Name} {c.Bounds}")));
            }

        static StackPanel Build(bool native)
        {
            var host = new StackPanel { Size = new Size(400, 300) };
            var text = new TextBlock("Text 国 with a wrapped second line and accents äö", wrap: true, nativeWindow: native);
            var icon = new GlyphBadge(Glyph.Speaker);
            var app = new AppIconBox(null);
            var dot = new StatusDot();
            var meter = new LevelMeter();
            var metric = new MetricTile("count", "ms");
            metric.SetValue("128"); meter.SetLevel(0.6f);
            var divider = new Divider();
            if (native) foreach (var c in new Control[] { icon, app, dot, meter, metric, divider }) _ = c.Handle;
            var row = Ui.Row(text, icon, app, dot, text);
            var card = Ui.Card(row, divider, metric, meter);
            host.Controls.Add(card);
            LayoutPanel.Place(card, 17, 19, 360, LayoutPanel.HeightFor(card, 360));
            return host;
        }
    });

    [Fact]
    public void Painted_hit_testing_clips_at_each_container_and_at_the_native_host() => Sta.Run(() =>
    {
        using var host = new StackPanel { Size = new Size(200, 100) };
        var text = new TextBlock("clipped text");
        var row = Ui.Row(null, text);
        var card = Ui.Card(row);
        host.Controls.Add(card);
        card.Bounds = new Rectangle(150, 10, 120, 100);
        row.Bounds = new Rectangle(10, 10, 100, 50);
        text.Bounds = new Rectangle(10, 0, 150, 30);
        LayoutPanel.CreateWindows(host, () => false);
        var inside = host.PointToScreen(new Point(175, 25));
        var outside = host.PointToScreen(new Point(220, 25));
        Assert.Same(text.AccessibilityObject, text.AccessibilityObject.HitTest(inside.X, inside.Y));
        Assert.Null(text.AccessibilityObject.HitTest(outside.X, outside.Y));
        Assert.False(text.IsHandleCreated || row.IsHandleCreated || card.IsHandleCreated);
    });

    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var descendant in All(child)) yield return descendant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    private sealed class ThemeScope : IDisposable
    {
        private readonly string _before = Theme.Mode;
        internal ThemeScope(string mode) => Theme.Mode = mode;
        public void Dispose() => Theme.Mode = _before;
    }
}
