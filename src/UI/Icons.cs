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

    private static Icon? _app;

    /// <summary>
    /// The app icon in every size (src/app.ico, drawn by tools/make_icon.py). Shared: forms use it as is, so
    /// never dispose it.
    /// </summary>
    public static Icon App => _app ??= LoadApp();

    private static Icon LoadApp()
    {
        using var stream = typeof(Icons).Assembly.GetManifestResourceStream("app.ico")
                           ?? throw new InvalidOperationException("app.ico is not embedded");
        return new Icon(stream);
    }

    /// <summary>
    /// The tray icon: the app icon at the tray's size, with a status dot in the corner while connecting (amber),
    /// streaming (green) or retrying (red). A new icon each call; the caller disposes it.
    /// </summary>
    public static Icon Tray(StreamState state)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var source = new Icon(App, size, size))
            using (var image = source.ToBitmap())
                g.DrawImage(image, new Rectangle(0, 0, size, size));
            if (state != StreamState.Idle)
            {
                float r = size * 0.21f, ring = Math.Max(1f, size / 16f);
                float cx = size - r - ring, cy = size - r - ring;
                using (var b = new SolidBrush(Color.White))
                    g.FillEllipse(b, cx - r - ring, cy - r - ring, 2 * (r + ring), 2 * (r + ring));
                using (var b = new SolidBrush(For(state)))
                    g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            }
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
