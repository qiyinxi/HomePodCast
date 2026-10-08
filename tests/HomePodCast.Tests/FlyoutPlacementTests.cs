using System.Drawing;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>Where the tray flyout opens: next to the icon, on the work-area side of the taskbar, always on screen.</summary>
public class FlyoutPlacementTests
{
    private const int Gap = 18, Margin = 18; // 12 logical px at 150 %
    private static readonly Size Flyout = new(522, 600);

    // A 2560×1440 monitor at 150 %: 72 px taskbar on one side.
    private static readonly Rectangle Monitor = new(0, 0, 2560, 1440);
    private static readonly Rectangle WorkBottom = Rectangle.FromLTRB(0, 0, 2560, 1368);
    private static readonly Rectangle WorkTop = Rectangle.FromLTRB(0, 72, 2560, 1440);
    private static readonly Rectangle WorkLeft = Rectangle.FromLTRB(72, 0, 2560, 1440);
    private static readonly Rectangle WorkRight = Rectangle.FromLTRB(0, 0, 2488, 1440);

    private static Rectangle Place(Rectangle anchor, Rectangle work, TaskbarEdge edge, Size? size = null) =>
        new(FlyoutPlacement.Place(anchor, work, edge, size ?? Flyout, Gap, Margin), size ?? Flyout);

    [Fact]
    public void Bottom_taskbar_opens_above_it_centred_on_the_icon()
    {
        var icon = new Rectangle(1200, 1386, 36, 36);
        var r = Place(icon, WorkBottom, TaskbarEdge.Bottom);
        Assert.Equal(1218 - Flyout.Width / 2, r.X);       // centred on the icon
        Assert.Equal(1368 - Gap, r.Bottom);                // gap above the taskbar
    }

    [Fact]
    public void Icon_near_the_screen_corner_keeps_the_flyout_inside_the_work_area()
    {
        var icon = new Rectangle(2480, 1386, 36, 36);
        var r = Place(icon, WorkBottom, TaskbarEdge.Bottom);
        Assert.Equal(2560 - Margin, r.Right);
        Assert.Equal(1368 - Gap, r.Bottom);

        var left = Place(new Rectangle(4, 1386, 36, 36), WorkBottom, TaskbarEdge.Bottom);
        Assert.Equal(Margin, left.X);
    }

    [Fact]
    public void Icon_in_the_overflow_area_above_the_taskbar_gets_the_flyout_above_it()
    {
        var icon = new Rectangle(2100, 1200, 36, 36); // the ^ panel sits above the taskbar
        var r = Place(icon, WorkBottom, TaskbarEdge.Bottom);
        Assert.Equal(1200 - Gap, r.Bottom);
    }

    [Fact]
    public void Top_taskbar_opens_below_it()
    {
        var icon = new Rectangle(1800, 18, 36, 36);
        var r = Place(icon, WorkTop, TaskbarEdge.Top);
        Assert.Equal(72 + Gap, r.Y);
        Assert.Equal(1818 - Flyout.Width / 2, r.X);
    }

    [Fact]
    public void Left_taskbar_opens_to_its_right_centred_on_the_icon()
    {
        var icon = new Rectangle(18, 700, 36, 36);
        var r = Place(icon, WorkLeft, TaskbarEdge.Left);
        Assert.Equal(72 + Gap, r.X);
        Assert.Equal(718 - Flyout.Height / 2, r.Y);
    }

    [Fact]
    public void Right_taskbar_opens_to_its_left_and_stays_above_the_bottom()
    {
        var icon = new Rectangle(2506, 1380, 36, 36); // icons near the bottom of a vertical taskbar
        var r = Place(icon, WorkRight, TaskbarEdge.Right);
        Assert.Equal(2488 - Gap, r.Right);
        Assert.Equal(1440 - Margin, r.Bottom);
    }

    [Fact]
    public void Monitor_left_of_the_primary_has_negative_coordinates()
    {
        var work = Rectangle.FromLTRB(-1920, 0, 0, 1032);
        var r = Place(new Rectangle(-60, 1040, 24, 24), work, TaskbarEdge.Bottom, new Size(348, 400));
        Assert.Equal(-Margin, r.Right);
        Assert.Equal(1032 - Gap, r.Bottom);
    }

    [Fact]
    public void Flyout_larger_than_the_work_area_keeps_its_top_left_on_screen()
    {
        var small = Rectangle.FromLTRB(0, 0, 400, 300);
        var r = Place(new Rectangle(380, 310, 16, 16), small, TaskbarEdge.Bottom, new Size(500, 450));
        Assert.Equal(new Point(0, 0), r.Location);

        var tight = Place(new Rectangle(380, 310, 16, 16), small, TaskbarEdge.Bottom, new Size(390, 290)); // fits, but not with margins
        Assert.True(small.Contains(tight), $"{tight} outside {small}");
    }

    [Fact]
    public void Cursor_fallback_anywhere_on_any_edge_stays_on_screen()
    {
        var random = new Random(7);
        var works = new[] { (WorkBottom, TaskbarEdge.Bottom), (WorkTop, TaskbarEdge.Top), (WorkLeft, TaskbarEdge.Left), (WorkRight, TaskbarEdge.Right) };
        for (int i = 0; i < 2000; i++)
        {
            var (work, edge) = works[i % works.Length];
            var anchor = new Rectangle(random.Next(-50, 2610), random.Next(-50, 1490), random.Next(1, 40), random.Next(1, 40));
            var size = new Size(random.Next(300, 700), random.Next(200, 900));
            var r = Place(anchor, work, edge, size);
            Assert.True(work.Contains(r), $"{r} outside {work} (anchor {anchor}, {edge})");
        }
    }

    [Theory]
    [InlineData(0, 0, 2560, 1368, "Bottom")]
    [InlineData(0, 72, 2560, 1440, "Top")]
    [InlineData(72, 0, 2560, 1440, "Left")]
    [InlineData(0, 0, 2488, 1440, "Right")]
    public void Taskbar_edge_comes_from_the_work_area(int left, int top, int right, int bottom, string expected) =>
        Assert.Equal(Enum.Parse<TaskbarEdge>(expected), FlyoutPlacement.EdgeOf(Monitor, Rectangle.FromLTRB(left, top, right, bottom), new Rectangle(1280, 720, 1, 1)));

    [Theory]
    [InlineData(2500, 1430, "Bottom")]
    [InlineData(2500, 5, "Top")]
    [InlineData(3, 700, "Left")]
    [InlineData(2555, 700, "Right")]
    public void Auto_hidden_taskbar_is_on_the_side_nearest_the_icon(int x, int y, string expected) =>
        Assert.Equal(Enum.Parse<TaskbarEdge>(expected), FlyoutPlacement.EdgeOf(Monitor, Monitor, new Rectangle(x, y, 1, 1)));
}
