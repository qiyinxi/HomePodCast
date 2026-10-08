namespace HomePodCast.UI.Controls;

/// <summary>Two columns side by side when there is room, otherwise one above the other.</summary>
internal sealed class Columns : LayoutPanel
{
    private readonly Control _left, _right;

    public Columns(Control left, Control right)
    {
        _left = left;
        _right = right;
        Controls.Add(left);
        Controls.Add(right);
    }
    public int MinColumnWidth { get; set; } = 330;
    public int Gap { get; set; } = 12;

    /// <summary>Share of the width for the left column when side by side.</summary>
    public double LeftShare { get; set; } = 0.5;

    protected override int Arrange(int width, bool apply)
    {
        int gap = Dp(Gap);
        if (width >= Dp(MinColumnWidth) * 2 + gap)
        {
            int lw = (int)Math.Round((width - gap) * LeftShare), rw = width - gap - lw;
            int lh = HeightFor(_left, lw), rh = HeightFor(_right, rw);
            if (apply)
            {
                Place(_left, 0, 0, lw, lh);
                Place(_right, lw + gap, 0, rw, rh);
            }
            return Math.Max(lh, rh);
        }
        int h1 = HeightFor(_left, width), h2 = HeightFor(_right, width);
        if (apply)
        {
            Place(_left, 0, 0, width, h1);
            Place(_right, 0, h1 + (h1 > 0 && h2 > 0 ? gap : 0), width, h2);
        }
        return h1 + h2 + (h1 > 0 && h2 > 0 ? gap : 0);
    }
}

/// <summary>Equal tiles in as many columns as fit (at least <see cref="MinTileWidth"/> wide each).</summary>
internal sealed class TileGrid : LayoutPanel
{
    public int MinTileWidth { get; set; } = 120;
    public int Gap { get; set; } = 8;

    protected override int Arrange(int width, bool apply)
    {
        var tiles = Controls.Cast<Control>().Where(c => !IsCollapsed(c)).ToList();
        if (tiles.Count == 0) return 0;
        int gap = Dp(Gap);
        int minW = Math.Max(Dp(MinTileWidth), tiles.Max(WidthFor));
        int cols = Math.Clamp((width + gap) / (minW + gap), 1, tiles.Count);
        int rows = (tiles.Count + cols - 1) / cols;
        int tileW = (width - gap * (cols - 1)) / cols;
        int tileH = tiles.Max(t => HeightFor(t, tileW));
        if (apply)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                int c = i % cols, r = i / cols;
                int x = c * (tileW + gap);
                int w = c == cols - 1 ? width - x : tileW;
                Place(tiles[i], x, r * (tileH + gap), w, tileH);
            }
        }
        return rows * tileH + (rows - 1) * gap;
    }
}

/// <summary>Small builders for the pages.</summary>
internal static class Ui
{
    public static RowPanel Row(Control? fill, params Control[] items) => Row(8, fill, items);

    public static RowPanel Row(int gap, Control? fill, params Control[] items)
    {
        var row = new RowPanel { Gap = gap, Fill = fill };
        row.Controls.AddRange(items);
        return row;
    }

    public static StackPanel Stack(int gap, params Control[] items)
    {
        var stack = new StackPanel { Gap = gap };
        stack.Controls.AddRange(items);
        return stack;
    }

    public static Card Card(params Control[] items)
    {
        var card = new Card();
        card.Controls.AddRange(items);
        return card;
    }

    /// <summary>Card heading.</summary>
    public static TextBlock Header(string text) => new(text, TextStyle.Subtitle);

    /// <summary>Section heading on a page (between cards).</summary>
    public static TextBlock Section(string text) => new(text, TextStyle.BodyStrong);

    /// <summary>Small secondary text, wrapped.</summary>
    public static TextBlock Note(string text = "") => new(text, TextStyle.Caption, TextRole.Secondary, wrap: true);

    public static FluentButton IconButton(string glyph, string accessibleName, string? tip = null, ToolTip? tips = null)
    {
        var b = new FluentButton("", ButtonKind.Subtle, glyph) { AccessibleName = accessibleName };
        if (tips != null) tips.SetToolTip(b, tip ?? accessibleName);
        return b;
    }

    public static FluentSlider Slider(int min, int max, int small = 1, int large = 10) => new()
    {
        Minimum = min,
        Maximum = max,
        SmallChange = small,
        LargeChange = large,
    };
}
