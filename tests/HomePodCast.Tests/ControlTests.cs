using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

/// <summary>Value and keyboard rules of the custom controls (created off-screen, never shown).</summary>
public class ControlTests
{
    [Fact]
    public void Slider_clamps_to_the_selectable_range_and_reports_only_user_changes_as_scroll() => Sta.Run(() =>
    {
        using var s = new FluentSlider { Minimum = 0, Maximum = 500, Floor = 100 };
        int changed = 0, scrolled = 0;
        s.ValueChanged += (_, _) => changed++;
        s.Scroll += (_, _) => scrolled++;

        Assert.Equal(100, s.Value); // below the floor is not selectable
        s.Value = 50;
        Assert.Equal(100, s.Value);
        s.Value = 120;
        Assert.Equal(120, s.Value);
        s.Value = 999;
        Assert.Equal(500, s.Value);
        Assert.Equal(2, changed);
        Assert.Equal(0, scrolled); // programmatic

        s.Ceiling = 300;
        Assert.Equal(300, s.Value);
        s.Floor = 350; // a floor above the ceiling wins
        Assert.Equal(350, s.Value);
    });

    [Theory]
    [InlineData(Keys.Right, 121)]
    [InlineData(Keys.Up, 121)]
    [InlineData(Keys.Oemplus, 121)]
    [InlineData(Keys.Add, 121)]
    [InlineData(Keys.Left, 119)]
    [InlineData(Keys.Down, 119)]
    [InlineData(Keys.OemMinus, 119)]
    [InlineData(Keys.Subtract, 119)]
    [InlineData(Keys.PageUp, 130)]
    [InlineData(Keys.PageDown, 110)]
    [InlineData(Keys.Home, 105)]
    [InlineData(Keys.End, 500)]
    public void Slider_keys_move_by_small_and_large_steps(Keys key, int expected) => Sta.Run(() =>
    {
        using var s = new FluentSlider { Minimum = 0, Maximum = 500, SmallChange = 1, LargeChange = 10, Floor = 105 };
        s.Value = 120;
        int scrolled = 0;
        s.Scroll += (_, _) => scrolled++;
        Assert.True(s.HandleKey(key));
        Assert.Equal(expected, s.Value);
        Assert.Equal(1, scrolled);
        Assert.False(s.HandleKey(Keys.A));
    });

    [Fact]
    public void Slider_keys_stop_at_the_floor() => Sta.Run(() =>
    {
        using var s = new FluentSlider { Minimum = 0, Maximum = 500, LargeChange = 10, Floor = 105 };
        s.Value = 108;
        s.HandleKey(Keys.PageDown);
        Assert.Equal(105, s.Value);
        int scrolled = 0;
        s.Scroll += (_, _) => scrolled++;
        s.HandleKey(Keys.Left); // already at the floor: nothing changes, nothing is reported
        Assert.Equal(105, s.Value);
        Assert.Equal(0, scrolled);
    });

    [Fact]
    public void Slider_maps_the_rail_to_values_within_the_selectable_range() => Sta.Run(() =>
    {
        using var s = new FluentSlider { Minimum = 0, Maximum = 100, Floor = 20, Ceiling = 80 };
        s.Size = new Size(400, 40);
        Assert.Equal(20, s.ValueAt(new Point(0, 20)));     // left end: clamped up to the floor
        Assert.Equal(80, s.ValueAt(new Point(400, 20)));   // right end: clamped down to the ceiling
        Assert.InRange(s.ValueAt(new Point(200, 20)), 49, 51);

        using var v = new FluentSlider { Minimum = -12, Maximum = 12, Orientation = Orientation.Vertical };
        v.Size = new Size(40, 300);
        Assert.Equal(12, v.ValueAt(new Point(20, 0)));     // vertical: top is the maximum
        Assert.Equal(-12, v.ValueAt(new Point(20, 300)));
        Assert.InRange(v.ValueAt(new Point(20, 150)), -1, 1);
    });

    [Fact]
    public void Segmented_keys_select_and_only_user_changes_are_committed() => Sta.Run(() =>
    {
        using var seg = new Segmented("a", "b", "c", "d");
        int committed = 0, changed = 0;
        seg.SelectionChangeCommitted += (_, _) => committed++;
        seg.SelectedIndexChanged += (_, _) => changed++;
        seg.SelectedIndex = 1;
        Assert.Equal((0, 1), (committed, changed));
        Assert.True(seg.HandleKey(Keys.Right));
        Assert.Equal(2, seg.SelectedIndex);
        Assert.True(seg.HandleKey(Keys.End));
        Assert.Equal(3, seg.SelectedIndex);
        seg.HandleKey(Keys.Right); // already last: no change
        Assert.True(seg.HandleKey(Keys.Home));
        Assert.Equal(0, seg.SelectedIndex);
        Assert.Equal(3, committed);
        Assert.Equal("a", seg.SelectedText);
    });

    [Fact]
    public void Combo_box_arrow_keys_change_the_selection_while_closed() => Sta.Run(() =>
    {
        using var combo = new FluentComboBox();
        combo.Items.AddRange(["一", "二", "三"]);
        combo.SelectedIndex = 0;
        int committed = 0;
        combo.SelectionChangeCommitted += (_, _) => committed++;
        Assert.True(combo.HandleKey(Keys.Down));
        Assert.True(combo.HandleKey(Keys.Down));
        Assert.True(combo.HandleKey(Keys.Down));
        Assert.Equal("三", combo.SelectedText);
        Assert.True(combo.HandleKey(Keys.Home));
        Assert.Equal(0, combo.SelectedIndex);
        Assert.Equal(3, committed);

        combo.Items.RemoveAt(2);
        combo.SelectedIndex = 1;
        combo.Items.RemoveAt(1); // selection gone with its item
        Assert.Equal(-1, combo.SelectedIndex);
    });

    [Fact]
    public void Password_box_is_masked_until_revealed_and_clear_forgets_and_masks_again() => Sta.Run(() =>
    {
        using var box = new PasswordBox("VLC");
        int changed = 0;
        box.PasswordChanged += (_, _) => changed++;
        Assert.False(box.Revealed); // masked by default
        Assert.Equal("", box.Password);

        box.Password = "typed";
        Assert.Equal(1, changed);
        box.Revealed = true;
        Assert.True(box.Revealed);
        Assert.Equal("typed", box.Password); // showing it keeps what was typed
        box.Revealed = false;
        Assert.Equal("typed", box.Password);

        box.Revealed = true;
        box.Clear();
        Assert.Equal("", box.Password);
        Assert.False(box.Revealed);
    });

    [Fact]
    public void Toggle_switch_reports_user_flips_separately() => Sta.Run(() =>
    {
        using var toggle = new ToggleSwitch("夜间模式");
        int changed = 0, toggled = 0;
        toggle.CheckedChanged += (_, _) => changed++;
        toggle.Toggled += (_, _) => toggled++;
        toggle.Checked = true;
        Assert.Equal((1, 0), (changed, toggled));
        toggle.Flip();
        Assert.False(toggle.Checked);
        Assert.Equal((2, 1), (changed, toggled));
    });
}

internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
