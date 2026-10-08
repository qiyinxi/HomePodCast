using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HomePodCast.UI;

/// <summary>Text roles of the type ramp (sizes in points at 96 DPI).</summary>
internal enum TextStyle { Caption, Body, BodyStrong, Subtitle, Title, Display, Metric }

/// <summary>
/// Windows 11 (Fluent) look for the WinForms UI: light/dark palette that follows Windows (or the config
/// "Theme": auto | light | dark), the Windows accent colour, fonts per DPI, the icon font, and DWM window
/// styling (dark title bar, Mica on 22H2+). Everything is in 96-DPI units and scaled with <see cref="Dp"/>.
/// </summary>
internal static partial class Theme
{
    public const string Auto = "auto", Light = "light", Dark = "dark";

    /// <summary>Theme colours. Status colours (green/amber/red/grey) only ever mean a state.</summary>
    public sealed record Palette(
        bool IsDark,
        Color Window, Color Card, Color CardBorder, Color Divider,
        Color Control, Color ControlHover, Color ControlPressed, Color ControlBorder,
        Color Subtle, Color SubtlePressed, Color NavHover, Color NavSelected,
        Color Text, Color TextSecondary, Color TextDisabled, Color TextOnAccent,
        Color Accent, Color AccentHover, Color AccentPressed, Color AccentText,
        Color Track, Color ToggleBorder, Color ToggleKnob, Color Thumb, Color ThumbBorder, Color MeterTrack,
        Color Success, Color Caution, Color Critical, Color Neutral,
        Color Popup, Color PopupBorder, Color FocusOuter, Color FocusInner);

    private static string _mode = Auto;
    private static bool _watching;
    private static SynchronizationContext? _ui;

    public static Palette P { get; private set; } = Build(false, null);
    public static bool IsDark => P.IsDark;

    /// <summary>Raised on the UI thread after the palette changed (Windows theme, accent, or the setting).</summary>
    public static event Action? Changed;

    /// <summary>The configured mode ("auto", "light" or "dark"); setting it applies at once.</summary>
    public static string Mode
    {
        get => _mode;
        set
        {
            _mode = Normalize(value);
            Refresh();
        }
    }

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => Auto,
    };

    /// <summary>Start following Windows' light/dark and accent changes; call on the UI thread once a control exists.</summary>
    public static void Watch()
    {
        if (_watching) return;
        _watching = true;
        _ui = SynchronizationContext.Current;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                if (_ui != null) _ui.Post(_ => Refresh(), null);
                else Refresh();
            }
        };
    }

    public static void Refresh()
    {
        bool dark = _mode == Dark || _mode == Auto && SystemUsesDark();
        var p = Build(dark, ReadAccent(dark));
        if (p == P) return;
        P = p;
        Changed?.Invoke();
    }

    /// <summary>HKCU\…\Themes\Personalize AppsUseLightTheme (0 = dark).</summary>
    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The Windows accent in the shade Fluent uses for fills: Dark1 on light, Light2 on dark.</summary>
    private static Color? ReadAccent(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
            {
                int i = (dark ? 1 : 4) * 4;
                return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
            }
        }
        catch { }
        return null;
    }

    private static Palette Build(bool dark, Color? accent)
    {
        if (dark)
        {
            var a = accent ?? Color.FromArgb(0x60, 0xCD, 0xFF);
            return new Palette(true,
                Window: Hex(0x202020), Card: Hex(0x2B2B2B), CardBorder: Hex(0x383838), Divider: Hex(0x353535),
                Control: Hex(0x373737), ControlHover: Hex(0x3D3D3D), ControlPressed: Hex(0x323232), ControlBorder: Hex(0x474747),
                Subtle: Hex(0x353535), SubtlePressed: Hex(0x303030), NavHover: Hex(0x2A2A2A), NavSelected: Hex(0x2F2F2F),
                Text: Hex(0xFFFFFF), TextSecondary: Hex(0xC9C9C9), TextDisabled: Hex(0x787878), TextOnAccent: Hex(0x000000),
                Accent: a, AccentHover: Blend(a, Color.Black, 0.10), AccentPressed: Blend(a, Color.Black, 0.20), AccentText: a,
                Track: Hex(0x9A9A9A), ToggleBorder: Hex(0x9A9A9A), ToggleKnob: Hex(0xD0D0D0), Thumb: Hex(0x454545), ThumbBorder: Hex(0x555555),
                MeterTrack: Hex(0x3C3C3C),
                Success: Hex(0x6CCB5F), Caution: Hex(0xFCE100), Critical: Hex(0xFF99A4), Neutral: Hex(0x9D9D9D),
                Popup: Hex(0x2C2C2C), PopupBorder: Hex(0x404040), FocusOuter: Hex(0xFFFFFF), FocusInner: Hex(0x000000));
        }
        var l = accent ?? Color.FromArgb(0x00, 0x5F, 0xB8);
        return new Palette(false,
            Window: Hex(0xF3F3F3), Card: Hex(0xFBFBFB), CardBorder: Hex(0xE3E3E3), Divider: Hex(0xE8E8E8),
            Control: Hex(0xFFFFFF), ControlHover: Hex(0xF6F6F6), ControlPressed: Hex(0xF0F0F0), ControlBorder: Hex(0xD3D3D3),
            Subtle: Hex(0xEDEDED), SubtlePressed: Hex(0xE6E6E6), NavHover: Hex(0xEAEAEA), NavSelected: Hex(0xE5E5E5),
            Text: Hex(0x1B1B1B), TextSecondary: Hex(0x5C5C5C), TextDisabled: Hex(0xA3A3A3), TextOnAccent: Hex(0xFFFFFF),
            Accent: l, AccentHover: Blend(l, Color.White, 0.10), AccentPressed: Blend(l, Color.White, 0.22), AccentText: l,
            Track: Hex(0x8A8A8A), ToggleBorder: Hex(0x858585), ToggleKnob: Hex(0x5C5C5C), Thumb: Hex(0xFFFFFF), ThumbBorder: Hex(0xD0D0D0),
            MeterTrack: Hex(0xE2E2E2),
            Success: Hex(0x0F7B0F), Caution: Hex(0x9D5D00), Critical: Hex(0xC42B1C), Neutral: Hex(0x8A8A8A),
            Popup: Hex(0xF9F9F9), PopupBorder: Hex(0xD6D6D6), FocusOuter: Hex(0x1B1B1B), FocusInner: Hex(0xFFFFFF));
    }

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    // ---------------------------------------------------------------- sizes and fonts

    /// <summary>96-DPI units → device pixels.</summary>
    public static int Dp(float value, int dpi) => (int)Math.Round(value * dpi / 96f);

    private static readonly Dictionary<(TextStyle, int, string), Font> Fonts = new();
    private static readonly Dictionary<(int, int), Font> IconFonts = new();
    private static string? _semibold;

    private static float Points(TextStyle style) => style switch
    {
        TextStyle.Caption => 9f,
        TextStyle.Body or TextStyle.BodyStrong => 10f,
        TextStyle.Subtitle => 12f,
        TextStyle.Metric => 15f,
        TextStyle.Title => 20f,
        TextStyle.Display => 30f,
        _ => 10f,
    };

    /// <summary>A UI font for the current language (L.FontName), in pixels for <paramref name="dpi"/>; cached, never dispose.</summary>
    public static Font Font(TextStyle style, int dpi)
    {
        var key = (style, dpi, L.FontName);
        lock (Fonts)
        {
            if (Fonts.TryGetValue(key, out var font)) return font;
            float px = Points(style) * dpi / 72f;
            bool strong = style is TextStyle.BodyStrong or TextStyle.Subtitle or TextStyle.Title or TextStyle.Display or TextStyle.Metric;
            font = strong && SemiboldFamily() is { } semi
                ? new Font(semi, px, FontStyle.Regular, GraphicsUnit.Pixel)
                : new Font(L.FontName, px, strong ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return Fonts[key] = font;
        }
    }

    /// <summary>"Segoe UI Semibold" / "Yu Gothic UI Semibold" when the UI font has one; YaHei and JhengHei use Bold.</summary>
    private static string? SemiboldFamily()
    {
        if (_semibold != null) return _semibold.Length == 0 ? null : _semibold;
        _semibold = "";
        try
        {
            using var family = new FontFamily(L.FontName + " Semibold");
            _semibold = family.Name;
        }
        catch (ArgumentException) { }
        return _semibold.Length == 0 ? null : _semibold;
    }

    private static string? _iconFamily;

    /// <summary>Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10); same code points.</summary>
    public static string IconFamily => _iconFamily ??= PickIconFamily();

    private static string PickIconFamily()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try
            {
                using var family = new FontFamily(name);
                return name;
            }
            catch (ArgumentException) { }
        }
        return "Segoe UI Symbol";
    }

    /// <summary>The icon font at <paramref name="size"/> logical pixels.</summary>
    public static Font IconFont(int size, int dpi)
    {
        lock (IconFonts)
        {
            if (IconFonts.TryGetValue((size, dpi), out var font)) return font;
            return IconFonts[(size, dpi)] = new Font(IconFamily, size * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }

    // ---------------------------------------------------------------- windows

    private const int DwmUseImmersiveDarkMode = 20, DwmUseImmersiveDarkModeOld = 19;
    private const int DwmWindowCornerPreference = 33, DwmSystemBackdropType = 38;

    /// <summary>Windows 11 22H2 (build 22621) has DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public static bool MicaSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Dark or light title bar, and the Mica backdrop where Windows has it (the client area stays solid).</summary>
    public static void StyleWindow(Form form)
    {
        if (!form.IsHandleCreated) return;
        var h = form.Handle;
        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(h, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(h, DwmUseImmersiveDarkModeOld, ref dark, sizeof(int));
        if (MicaSupported)
        {
            int mica = 2; // DWMSBT_MAINWINDOW
            DwmSetWindowAttribute(h, DwmSystemBackdropType, ref mica, sizeof(int));
        }
        // Repaint the frame now (it would otherwise keep the old colours until the next activation).
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    /// <summary>Small rounded corners for popups on Windows 11 (ignored elsewhere).</summary>
    public static void RoundPopup(IntPtr handle)
    {
        int round = 3; // DWMWCP_ROUNDSMALL
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
    }

    /// <summary>Scroll bars (and other common-control parts) in the light or dark system style.</summary>
    public static void StyleScrollBars(Control control)
    {
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Code points of the Segoe Fluent Icons / Segoe MDL2 Assets font.</summary>
internal static class Glyph
{
    public const string Home = "";
    public const string Mixer = "";
    public const string Microphone = "";
    public const string Settings = "";
    public const string Speaker = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Refresh = "";
    public const string ChevronDown = "";
    public const string Minus = "";
    public const string Plus = "";
    public const string CheckMark = "";
    public const string Globe = "";
    public const string Appearance = "";
    public const string Power = "";
    public const string Link = "";
    public const string Keyboard = "";
    public const string Moon = "";
    public const string Sync = "";
    public const string Devices = "";
    public const string Info = "";
    public const string OpenInNew = "";
    public const string Warning = "";
    public const string Gauge = "";
}
