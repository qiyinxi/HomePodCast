using System.Drawing;
using System.Windows.Forms;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

/// <summary>Layout work and geometry of pages built off-screen, without starting TrayApp or showing a window.</summary>
public class LayoutTests
{
    [Fact]
    public void Placing_a_resized_container_arranges_each_descendant_once() => Sta.Run(() =>
    {
        using var host = new Panel();
        var stack = new CountingStack();
        var rows = Enumerable.Range(0, 24).Select(_ => new CountingStack()).ToArray();
        foreach (var row in rows)
        {
            row.Controls.Add(new TextBlock("Wrapped settings description", wrap: true));
            stack.Controls.Add(row);
        }
        host.Controls.Add(stack);
        stack.Applied = 0;
        foreach (var row in rows) row.Applied = 0;

        int height = stack.GetPreferredSize(new Size(640, 0)).Height;
        LayoutPanel.Place(stack, 0, 0, 640, height);

        Assert.Equal(1, stack.Applied);
        Assert.All(rows, row => Assert.Equal(1, row.Applied));
        Assert.Equal(640, rows[^1].Width);
        Assert.Equal(height, rows[^1].Bottom);
    });

    [Fact]
    public void Changing_descendant_text_reflows_even_when_the_container_bounds_stay_the_same() => Sta.Run(() =>
    {
        using var host = new Panel();
        var stack = new CountingStack();
        var inner = new CountingStack();
        var text = new TextBlock("Short", wrap: true);
        inner.Controls.Add(text);
        stack.Controls.Add(inner);
        host.Controls.Add(stack);
        LayoutPanel.Place(stack, 0, 0, 320, 200);
        int before = text.Height;
        // Suspend the root while the model changes, as a page refresh does.
        using (LayoutPanel.BatchLayout(stack))
            text.Text = string.Join(" ", Enumerable.Repeat("A long settings description.", 20));

        var bounds = stack.Bounds;
        stack.Applied = inner.Applied = 0;
        LayoutPanel.Place(stack, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        Assert.Equal(1, stack.Applied);
        Assert.Equal(1, inner.Applied);
        Assert.True(text.Height > before);
        Assert.Equal(text.Bottom, inner.Height);
        Assert.Equal(bounds, stack.Bounds);
    });

    [Fact]
    public void A_page_batches_collapsed_rows_and_keeps_the_remaining_rows_in_order() => Sta.Run(() =>
    {
        using var page = new CountingPage { Size = new Size(720, 480) };
        var rows = Enumerable.Range(0, 24).Select(_ => new CountingStack()).ToArray();
        using (LayoutPanel.BatchLayout(page))
        {
            foreach (var row in rows)
            {
                row.Controls.Add(new TextBlock("Night disconnect settings", wrap: true));
                page.Content.Controls.Add(row);
            }
        }
        int before = page.AutoScrollMinSize.Height;
        page.Applied = 0;
        foreach (var row in rows) row.Applied = 0;

        using (LayoutPanel.BatchLayout(page))
        {
            rows[4].Collapsed = true;
            rows[5].Collapsed = true;
            Assert.Equal(0, page.Applied);
            Assert.All(rows, row => Assert.Equal(0, row.Applied));
        }

        Assert.Equal(1, page.Applied);
        Assert.All(rows.Where(r => !r.Collapsed), row => Assert.Equal(1, row.Applied));
        Assert.True(page.AutoScrollMinSize.Height < before);
        Assert.Equal(rows[3].Bottom + page.Content.Gap, rows[6].Top);

        using (LayoutPanel.BatchLayout(page))
        {
            rows[4].Collapsed = false;
            rows[5].Collapsed = false;
        }
        Assert.Equal(before, page.AutoScrollMinSize.Height);
        Assert.Equal(rows[3].Bottom + page.Content.Gap, rows[4].Top);
        Assert.Equal(rows[5].Bottom + page.Content.Gap, rows[6].Top);
    });

    [Fact]
    public void Nested_layout_batches_wait_for_the_outer_batch() => Sta.Run(() =>
    {
        using var host = new Panel();
        var stack = new CountingStack();
        var text = new TextBlock("Short", wrap: true);
        stack.Controls.Add(text);
        host.Controls.Add(stack);
        stack.Applied = 0;
        using (LayoutPanel.BatchLayout(stack))
        {
            using (LayoutPanel.BatchLayout(stack)) text.Text = "Updated";
            Assert.Equal(0, stack.Applied);
        }
        Assert.Equal(1, stack.Applied);
    });

    [Fact]
    public void Navigation_pins_settings_to_the_bottom_and_retains_names_when_compact() => Sta.Run(() =>
    {
        using var host = new Panel();
        var rail = new NavRail();
        rail.Add("a", "Home");
        rail.Add("b", "Mixer");
        rail.Add("c", "Microphone and effects");
        var settings = rail.Add("d", "Settings", bottom: true);
        host.Controls.Add(rail);
        rail.SelectedIndex = 3;
        LayoutPanel.Place(rail, 0, 0, rail.WidthForWindow(1000), 680);
        Assert.All(rail.Items, item => Assert.False(item.Compact));
        Assert.Equal(672, settings.Bottom);
        Assert.True(settings.Top > rail.Items[2].Bottom + 100);

        LayoutPanel.Place(rail, 0, 0, rail.WidthForWindow(600), 460);
        Assert.Equal(64, rail.Width);
        Assert.All(rail.Items, item => Assert.True(item.Compact));
        Assert.Equal(452, settings.Bottom);
        Assert.Equal("Settings", settings.AccessibilityObject.Name);
        Assert.True(settings.Selected);
        Assert.True(settings.TabStop);
        Assert.All(rail.Items.Take(3), item => Assert.False(item.TabStop));
        int selected = -1;
        rail.Navigate += index => selected = index;
        rail.Items[0].AccessibilityObject.DoDefaultAction();
        Assert.Equal(0, selected);
        Assert.Equal(0, rail.SelectedIndex);

        LayoutPanel.Place(rail, 0, 0, rail.WidthForWindow(1000), 800);
        Assert.All(rail.Items, item => Assert.False(item.Compact));
        Assert.Equal(792, settings.Bottom);
    });

    [Fact]
    public void Resizing_a_page_stacks_columns_reduces_insets_and_centers_wide_content() => Sta.Run(() =>
    {
        using var page = new ScrollPage { Size = new Size(960, 600) };
        var left = new Card();
        left.Controls.Add(new TextBlock("Left card"));
        var right = new Card();
        right.Controls.Add(new TextBlock("Right card"));
        var columns = new Columns(left, right) { MinColumnWidth = 300 };
        page.Content.Controls.Add(columns);
        page.PerformLayout();
        Assert.Equal(left.Top, right.Top);
        Assert.True(right.Left > left.Right);
        int wideInset = columns.Left;

        page.Width = 520;
        page.PerformLayout();
        Assert.Equal(left.Left, right.Left);
        Assert.True(right.Top > left.Bottom);
        Assert.True(columns.Left < wideInset);
        Assert.Equal(16, columns.Left);
        Assert.Equal(page.Content.GetPreferredSize(new Size(page.Content.Width, 0)).Height, page.AutoScrollMinSize.Height);

        page.Width = 1600;
        page.PerformLayout();
        Assert.Equal(left.Top, right.Top);
        Assert.Equal(page.MaxContentWidth, page.Content.Width);
        int available = page.Width - SystemInformation.GetVerticalScrollBarWidthForDpi(page.DeviceDpi);
        Assert.InRange(available - page.Content.Right - page.Content.Left, 0, 1);
    });

    [Fact]
    public void A_stacked_setting_measures_its_content_at_the_actual_available_width() => Sta.Run(() =>
    {
        using var host = new Panel();
        var note = new TextBlock(string.Join(" ", Enumerable.Repeat("Long description", 18)), wrap: true);
        var content = new RowPanel();
        content.Controls.Add(note);
        using var row = new SettingRow(null, "Title", "Description", content) { ContentWidth = 600 };
        host.Controls.Add(row);
        int height = row.GetPreferredSize(new Size(320, 0)).Height;
        LayoutPanel.Place(row, 0, 0, 320, height);

        Assert.True(content.Top >= row.DescriptionText.Bottom);
        Assert.Equal(288, content.Width);
        Assert.Equal(note.GetPreferredSize(new Size(note.Width, 0)).Height, note.Height);
        Assert.Equal(content.Height, note.Height);
        Assert.Equal(content.Bottom + row.Padding.Bottom, row.Height);
    });

    [Fact]
    public void Narrow_settings_move_wide_controls_below_the_description() => Sta.Run(() =>
    {
        using var host = new Panel();
        var content = new Segmented("Take over", "Ask first");
        using var row = new SettingRow("a", "Home theater", "A description that needs room to remain readable.", content)
            { ContentWidth = 170 };
        host.Controls.Add(row);
        LayoutPanel.Place(row, 0, 0, 640, row.GetPreferredSize(new Size(640, 0)).Height);
        Assert.True(content.Left > row.DescriptionText.Right);

        LayoutPanel.Place(row, 0, 0, 470, row.GetPreferredSize(new Size(470, 0)).Height);
        Assert.Equal(row.DescriptionText.Left, content.Left);
        Assert.True(content.Top > row.DescriptionText.Bottom);
        Assert.True(row.DescriptionText.Width > 360);
        Assert.True(content.Bottom + row.Padding.Bottom <= row.Height);
    });

    private sealed class CountingStack : StackPanel
    {
        public int Applied;

        protected override int Arrange(int width, bool apply)
        {
            if (apply) Applied++;
            return base.Arrange(width, apply);
        }
    }

    private sealed class CountingPage : ScrollPage
    {
        public int Applied;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            Applied++;
            base.OnLayout(levent);
        }
    }
}
