using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HomePodCast.UI;

internal static partial class Icons
{
    public static readonly Color Idle = Color.FromArgb(150, 150, 150);
    public static readonly Color Connecting = Color.FromArgb(224, 161, 0);
    public static readonly Color Streaming = Color.FromArgb(47, 168, 79);
    public static readonly Color Error = Color.FromArgb(214, 69, 69);

    public static Color For(StreamState state) => state switch
    {
        StreamState.Streaming => Streaming,
        StreamState.Connecting => Connecting,
        StreamState.Retrying => Error,
        _ => Idle,
    };

    /// <summary>A little speaker: dark rounded body with a coloured "mesh" dot showing the state.</summary>
    public static Icon Speaker(Color state, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var body = new GraphicsPath();
            var r = new RectangleF(7 * s, 2 * s, 18 * s, 28 * s);
            float rad = 7 * s;
            body.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            body.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            body.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            body.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            body.CloseFigure();
            using (var b = new SolidBrush(Color.FromArgb(43, 43, 43))) g.FillPath(b, body);
            using (var b = new SolidBrush(state)) g.FillEllipse(b, 10 * s, 11 * s, 12 * s, 12 * s);
            using (var b = new SolidBrush(Color.FromArgb(140, 140, 140))) g.FillEllipse(b, 13.5f * s, 4 * s, 5 * s, 5 * s);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
