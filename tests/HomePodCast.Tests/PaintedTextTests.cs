using System.Drawing;
using System.Windows.Forms;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class PaintedTextTests
{
    [Fact]
    public void Painting_matches_windowed_text_and_invalidates_old_and_new_bounds() => Sta.Run(() =>
    {
        using var host = new StackPanel { Size = new Size(300, 160) };
        using var text = new TextBlock("Text 国\nsecond line", wrap: true, nativeWindow: true);
        host.Controls.Add(text);
        text.Bounds = new Rectangle(12, 10, 220, 70);
        using var expected = new Bitmap(text.Width, text.Height);
        text.DrawToBitmap(expected, new Rectangle(Point.Empty, text.Size));
        host.Controls.Remove(text);
        // A different instance: migration is deliberately forbidden once a native window exists.
        using var painted = new TextBlock(text.Text, wrap: true) { Bounds = text.Bounds };
        host.Controls.Add(painted);
        Assert.Equal(text.DeviceDpi, painted.DeviceDpi);
        Assert.Equal(text.TextFont, painted.TextFont);
        using var actual = new Bitmap(host.Width, host.Height);
        host.DrawToBitmap(actual, new Rectangle(Point.Empty, host.Size));
        for (int y = 0; y < text.Height; y++)
        for (int x = 0; x < text.Width; x++)
            Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x + painted.Left, y + painted.Top));
        var invalidated = new List<Rectangle>();
        _ = host.Handle;
        host.Invalidated += (_, e) => invalidated.Add(e.InvalidRect);
        var old = painted.Bounds;
        painted.Left += 30;
        Assert.Contains(invalidated, r => r.Contains(Rectangle.Union(old, painted.Bounds)));
        invalidated.Clear();
        painted.Role = TextRole.Caution;
        painted.Enabled = false;
        painted.Text = "changed";
        Assert.NotEmpty(invalidated);
        Assert.Equal("changed", painted.AccessibilityObject.Name);
        painted.AccessibleName = "status";
        Assert.Equal("status", painted.AccessibilityObject.Name);
        Assert.Equal("changed", painted.AccessibilityObject.Value);
        Assert.True(painted.AccessibilityObject.State.HasFlag(AccessibleStates.Unavailable));
        Assert.False(painted.IsHandleCreated);
    });

    [Fact]
    public void Visibility_and_accessible_sibling_navigation_follow_the_layout_order() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        using var first = new TextBlock("first");
        using var button = new FluentButton("button");
        using var last = new TextBlock("last");
        host.Controls.AddRange([first, button, last]);
        LayoutPanel.CreateWindows(host, () => false);
        var accessible = host.AccessibilityObject;
        Assert.Equal(3, accessible.GetChildCount());
        Assert.Same(first.AccessibilityObject, accessible.GetChild(0));
        Assert.Same(button.AccessibilityObject, first.AccessibilityObject.Navigate(AccessibleNavigation.Next));
        Assert.Same(button.AccessibilityObject, last.AccessibilityObject.Navigate(AccessibleNavigation.Previous));
        first.Visible = false;
        Assert.Equal(2, accessible.GetChildCount());
        first.Visible = true;
        Assert.Equal(3, accessible.GetChildCount());
        host.Visible = false;
        Assert.False(first.Visible);
        host.Visible = true;
        Assert.True(first.Visible);
        Assert.False(first.IsHandleCreated || last.IsHandleCreated);
    });

    [Fact]
    public void Native_services_and_plain_hosts_keep_a_window_but_dynamic_layout_texts_do_not() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        _ = host.Handle;
        using var tooltipText = new TextBlock("tooltip", nativeWindow: true);
        using var tips = new ToolTip();
        tips.SetToolTip(tooltipText, "details");
        host.Controls.Add(tooltipText);
        using var dynamicText = new TextBlock("added after the host's window");
        host.Controls.Add(dynamicText);
        Assert.True(tooltipText.IsHandleCreated);
        Assert.False(dynamicText.IsHandleCreated);
        Assert.Equal("details", tips.GetToolTip(tooltipText));
        var invalidated = new List<Rectangle>();
        host.Invalidated += (_, e) => invalidated.Add(e.InvalidRect);
        host.Controls.Remove(dynamicText);
        Assert.NotEmpty(invalidated);
        using var plainHost = new Panel();
        using var plainText = new TextBlock("plain WinForms host");
        plainHost.Controls.Add(plainText);
        LayoutPanel.CreateWindows(plainHost, () => false);
        Assert.False(plainText.ParentPainted);
        Assert.True(plainText.IsHandleCreated);
    });
}
