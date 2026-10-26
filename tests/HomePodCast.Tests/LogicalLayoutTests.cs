using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class LogicalLayoutTests
{
    [Fact]
    public void Mixed_rows_keep_logical_children_but_inputs_belong_to_the_native_surface() => Sta.Run(() =>
    {
        using var host = new StackPanel { Size = new Size(500, 400) };
        var button = new FluentButton("action") { TabIndex = 7 };
        var toggle = new ToggleSwitch { AccessibleName = "switch", TabIndex = 3 };
        var row = Ui.Row(null, new TextBlock("caption"), button, toggle);
        var card = Ui.Card(row);
        host.Controls.Add(card);
        var buttonAccessible = button.AccessibilityObject;
        var rowAccessible = row.AccessibilityObject;
        LogicalLayout.Enable(host);
        LayoutPanel.Place(card, 13, 15, 450, LayoutPanel.HeightFor(card, 450));
        LayoutPanel.CreateWindows(host, () => false);

        Assert.Same(host, button.Parent);
        Assert.Same(host, toggle.Parent);
        Assert.Same(row, LogicalLayout.Parent(button));
        Assert.Equal([card], LogicalLayout.Children(host));
        Assert.Equal([row], LogicalLayout.Children(card));
        Assert.Equal([row.Controls[0], button, toggle], LogicalLayout.Children(row));
        Assert.True(button.IsHandleCreated && toggle.IsHandleCreated);
        Assert.False(card.IsHandleCreated || row.IsHandleCreated);
        Assert.Equal(host.Handle, GetParent(button.Handle));
        Assert.Equal(host.Handle, GetParent(toggle.Handle));
        Assert.True(button.TabStop && toggle.TabStop);
        Assert.True(toggle.TabIndex < button.TabIndex);
        Assert.Same(buttonAccessible, button.AccessibilityObject);
        Assert.Same(rowAccessible, buttonAccessible.Parent);
        Assert.Same(buttonAccessible, rowAccessible.GetChild(1));
        Assert.Equal(FluentControl.SurfaceOf(row), FluentControl.SurfaceOf(LogicalLayout.Parent(button)));
        var expected = LogicalLayout.Bounds(button);
        expected.Offset(card.Location);
        expected.Offset(row.Location);
        Assert.Equal(expected, button.Bounds);
        Assert.Equal(host.RectangleToScreen(expected), buttonAccessible.Bounds);
    });

    [Fact]
    public void Disposing_a_logical_row_disposes_its_native_inputs_and_removes_both_tree_entries() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        var button = new FluentButton("action");
        var row = Ui.Row(null, new TextBlock("caption"), button);
        host.Controls.Add(row);
        LogicalLayout.Enable(host);
        LayoutPanel.CreateWindows(host, () => false);
        row.Dispose();
        Assert.True(button.IsDisposed);
        Assert.Empty(LogicalLayout.Children(host));
        Assert.DoesNotContain(button, host.Controls.Cast<Control>());
    });

    [Fact]
    public void Dynamic_rows_use_the_same_tree_without_recreating_the_surface_or_existing_inputs() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        var first = new FluentButton("first");
        var rows = Ui.Stack(4, Ui.Card(first));
        host.Controls.Add(rows);
        LogicalLayout.Enable(host);
        LayoutPanel.CreateWindows(host, () => false);
        var surface = host.Handle;
        var input = first.Handle;
        var second = new FluentSlider();
        var next = Ui.Card(second);
        rows.Controls.Add(next);
        LayoutPanel.CreateWindows(host, () => false);
        Assert.Equal(surface, host.Handle);
        Assert.Equal(input, first.Handle);
        Assert.False(rows.IsHandleCreated || next.IsHandleCreated);
        Assert.Equal(surface, GetParent(second.Handle));
        Assert.Same(next, LogicalLayout.Parent(second));
        Assert.Equal(2, LogicalLayout.Children(rows).Count());
    });

    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
}
