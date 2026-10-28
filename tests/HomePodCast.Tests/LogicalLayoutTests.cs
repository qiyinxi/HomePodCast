using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.UI;
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
        Assert.False(host.IsHandleCreated || button.IsHandleCreated || toggle.IsHandleCreated);
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
        card.Left += 11;
        expected.Offset(11, 0);
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
    public void Removing_a_native_input_releases_logical_ownership_without_reattaching_it_to_the_row() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        using var button = new FluentButton("removed");
        var row = Ui.Card(new TextBlock("caption"), button);
        host.Controls.Add(row);
        LogicalLayout.Enable(host);
        LayoutPanel.CreateWindows(host, () => false);
        host.Controls.Remove(button);
        Assert.Null(button.Parent);
        Assert.Null(LogicalLayout.Host(button));
        Assert.DoesNotContain(button, LogicalLayout.Children(row));
        Assert.False(row.IsHandleCreated);
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

    [Fact]
    public void Logical_visibility_and_enabled_state_preserve_each_inputs_own_choice() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        var toggle = new ToggleSwitch();
        var row = Ui.Card(toggle);
        host.Controls.Add(row);
        LogicalLayout.Enable(host);
        LayoutPanel.CreateWindows(host, () => false);
        var handle = toggle.Handle;
        Assert.True(toggle.Visible && toggle.Enabled);
        row.Enabled = false;
        Assert.False(toggle.Enabled);
        toggle.Enabled = false;
        row.Enabled = true;
        Assert.False(toggle.Enabled);
        toggle.Enabled = true;
        Assert.True(toggle.Enabled);
        row.Visible = false;
        Assert.False(toggle.Visible);
        toggle.Visible = false;
        row.Visible = true;
        Assert.False(toggle.Visible);
        toggle.Visible = true;
        Assert.True(toggle.Visible);
        row.Collapsed = true;
        Assert.False(toggle.Visible);
        row.Collapsed = false;
        Assert.True(toggle.Visible);
        Assert.Equal(handle, toggle.Handle);
        Assert.False(row.IsHandleCreated);
    });

    [Fact]
    public void Native_text_input_keeps_its_own_host_IME_properties_and_internal_tab_order() => Sta.Run(() =>
    {
        using var host = new StackPanel();
        var password = new PasswordBox("credential") { Password = "typed 国" };
        var editor = TestApp.Field<TextBox>(password, "_box");
        var reveal = TestApp.Field<FluentButton>(password, "_reveal");
        var ime = editor.ImeMode;
        var row = Ui.Card(password);
        host.Controls.Add(row);
        LogicalLayout.Enable(host);
        LayoutPanel.Place(row, 12, 14, 350, LayoutPanel.HeightFor(row, 350));
        LayoutPanel.CreateWindows(host, () => false);
        Assert.False(row.IsHandleCreated);
        Assert.True(password.IsHandleCreated && editor.IsHandleCreated && reveal.IsHandleCreated);
        Assert.Equal(host.Handle, GetParent(password.Handle));
        Assert.Equal(password.Handle, GetParent(editor.Handle));
        Assert.Same(password, editor.Parent);
        Assert.Equal(ime, editor.ImeMode);
        Assert.True(editor.UseSystemPasswordChar);
        Assert.Equal("typed 国", editor.Text);
        Assert.Equal(0, editor.TabIndex);
        Assert.Equal(1, reveal.TabIndex);
    });

    [Fact]
    public void Focus_tab_order_keyboard_messages_and_native_tooltip_registration_survive_flattening() => Sta.Run(() =>
    {
        using var form = new QuietForm { ClientSize = new Size(500, 300), Location = new Point(-20000, -20000),
            StartPosition = FormStartPosition.Manual, Opacity = 0, ShowInTaskbar = false };
        var host = new StackPanel { Size = form.ClientSize };
        var button = new FluentButton("action");
        var toggle = new ToggleSwitch();
        var slider = new FluentSlider { Value = 20 };
        var first = Ui.Card(button, toggle);
        var last = Ui.Card(slider);
        host.Controls.AddRange([first, last]);
        form.Controls.Add(host);
        using var tips = new ToolTip();
        tips.SetToolTip(toggle, "native hint");
        LogicalLayout.Enable(host);
        form.Show();
        LayoutPanel.Place(host, 0, 0, 500, 300);
        Application.DoEvents();
        Assert.True(button.Focus());
        Assert.True(host.SelectNextControl(button, forward: true, tabStopOnly: true, nested: true, wrap: false));
        Assert.True(toggle.Focused);
        Assert.Same(toggle.AccessibilityObject, first.AccessibilityObject.GetFocused());
        Assert.Same(button.AccessibilityObject, first.AccessibilityObject.Navigate(AccessibleNavigation.FirstChild));
        Assert.Same(toggle.AccessibilityObject, button.AccessibilityObject.Navigate(AccessibleNavigation.Next));
        SendMessage(toggle.Handle, 0x0100, (IntPtr)Keys.Space, IntPtr.Zero);
        Assert.True(toggle.Checked);
        Assert.True(host.SelectNextControl(toggle, forward: true, tabStopOnly: true, nested: true, wrap: false));
        Assert.True(slider.Focused);
        SendMessage(slider.Handle, 0x0100, (IntPtr)Keys.Right, IntPtr.Zero);
        Assert.Equal(21, slider.Value);
        Assert.True(host.SelectNextControl(slider, forward: false, tabStopOnly: true, nested: true, wrap: false));
        Assert.True(toggle.Focused);
        Assert.Equal("native hint", tips.GetToolTip(toggle));
        Assert.Equal(host.Handle, GetParent(toggle.Handle));
        Assert.False(first.IsHandleCreated || last.IsHandleCreated);
        first.TabIndex = 2;
        Assert.True(slider.TabIndex < button.TabIndex);
        form.Hide();
    });

    private sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [Theory]
    [InlineData(false, 280)]
    [InlineData(false, 500)]
    [InlineData(true, 280)]
    [InlineData(true, 500)]
    public void Mixed_cards_render_identical_pixels_with_native_or_logical_parents(bool dark, int width) => Sta.Run(() =>
    {
        var previous = Theme.Mode;
        try
        {
            Theme.Mode = dark ? Theme.Dark : Theme.Light;
            StackPanel Make(bool logical)
            {
                var surface = new StackPanel { Size = new Size(width, 250) };
                var toggle = new ToggleSwitch { AccessibleName = "enabled", Checked = true };
                var button = new FluentButton("action");
                var row = new SettingRow(Glyph.Speaker, "speaker", "description 国", toggle);
                var card = Ui.Card(new TextBlock("heading", TextStyle.Subtitle), row, Ui.Row(null, button));
                surface.Controls.Add(card);
                if (logical) LogicalLayout.Enable(surface);
                LayoutPanel.Place(card, 3, 5, width - 6, LayoutPanel.HeightFor(card, width - 6));
                LayoutPanel.CreateWindows(surface, () => false);
                return surface;
            }
            using var before = Make(false);
            using var after = Make(true);
            using var expected = new Bitmap(width, 250);
            using var actual = new Bitmap(width, 250);
            before.DrawToBitmap(expected, new Rectangle(Point.Empty, expected.Size));
            after.DrawToBitmap(actual, new Rectangle(Point.Empty, actual.Size));
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.True(expected.GetPixel(x, y) == actual.GetPixel(x, y), $"pixel {x},{y}: {expected.GetPixel(x, y)} != {actual.GetPixel(x, y)}");
        }
        finally { Theme.Mode = previous; }
    });

    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
