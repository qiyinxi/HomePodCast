using System.Drawing;
using HomePodCast.UI;

namespace HomePodCast.Tests;

public class IconTests
{
    [Fact]
    public void App_icon_is_embedded_with_the_small_sizes()
    {
        using var small = new Icon(Icons.App, 16, 16);
        Assert.Equal(16, small.Width);
        using var taskbar = new Icon(Icons.App, 48, 48); // 256 is a PNG frame, which Explorer reads but Icon skips
        Assert.Equal(48, taskbar.Width);
    }

    [Fact]
    public void Tray_icon_shows_a_status_dot_only_while_connected()
    {
        using var idle = Icons.Tray(StreamState.Idle);
        using var streaming = Icons.Tray(StreamState.Streaming);
        using var a = idle.ToBitmap();
        using var b = streaming.ToBitmap();
        int s = b.Width;
        int c = s - 1 - (int)(s * 0.21f + Math.Max(1f, s / 16f)); // the dot's centre
        var dot = b.GetPixel(c, c);
        Assert.True(dot.G > dot.R && dot.G > dot.B, $"expected green, got {dot}");
        var plain = a.GetPixel(c, c);
        Assert.True(plain.B > plain.G, $"expected the blue tile, got {plain}");
    }
}
