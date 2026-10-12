using System.Runtime.InteropServices;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// The runner's screen: its resolution (ChangeDisplaySettingsEx, not saved) and the primary display's scale, set the way
/// Windows Settings does it (DisplayConfigSetDeviceInfo with the undocumented DPI-scale packets -3/-4, as in
/// github.com/imniko/SetDPI). A scale change applies at once to per-monitor-aware apps started afterwards; no sign-out.
/// </summary>
internal static class Display
{
    /// <summary>The scales Windows steps through, as percentages; the packets count steps from the recommended one.</summary>
    private static readonly int[] Scales = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    // ---------------------------------------------------------------- resolution

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr param);

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    public static Size Resolution()
    {
        var dm = NewDevMode();
        return EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) ? new Size(dm.dmPelsWidth, dm.dmPelsHeight) : Size.Empty;
    }

    /// <summary>The modes the display offers.</summary>
    public static List<Size> Modes()
    {
        var list = new List<Size>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            var size = new Size(dm.dmPelsWidth, dm.dmPelsHeight);
            if (!list.Contains(size)) list.Add(size);
        }
        return list;
    }

    /// <summary>Switches the primary display to <paramref name="size"/> for this session; returns what it is afterwards.</summary>
    public static (bool Ok, string Detail) SetResolution(Size size)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettings(null, -1, ref dm)) return (false, "current mode unreadable");
        dm.dmPelsWidth = size.Width;
        dm.dmPelsHeight = size.Height;
        dm.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
        int result = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        return (result == 0, $"ChangeDisplaySettingsEx {size.Width}x{size.Height}: {result} (0 = done), now {Resolution().Width}x{Resolution().Height}");
    }

    // ---------------------------------------------------------------- scale

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_GET { public HEADER header; public int minScaleRel, curScaleRel, maxScaleRel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SET { public HEADER header; public int scaleRel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId;
        public uint id, modeInfoIdx;
        public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator;
        public int scanLineOrdering, targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE { public int infoType; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PATH[] pathArray, ref uint modes, [Out] MODE[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_GET packet);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SET packet);

    /// <summary>The primary display's source (the first active path).</summary>
    private static PATH_SOURCE? PrimarySource()
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0) return null;
        var paths = new PATH[pathCount];
        var modes = new MODE[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0 || pathCount == 0) return null;
        return paths[0].source;
    }

    public sealed record ScaleInfo(int Current, int Recommended, int Maximum);

    public static ScaleInfo? Scale()
    {
        if (PrimarySource() is not { } source) return null;
        var get = new DPI_GET { header = new HEADER { type = -3, size = Marshal.SizeOf<DPI_GET>(), adapterId = source.adapterId, id = source.id } };
        if (DisplayConfigGetDeviceInfo(ref get) != 0) return null;
        int min = Math.Abs(get.minScaleRel);
        int cur = Math.Clamp(get.curScaleRel, get.minScaleRel, get.maxScaleRel);
        if (min + get.maxScaleRel >= Scales.Length) return null;
        return new ScaleInfo(Scales[min + cur], Scales[min], Scales[min + get.maxScaleRel]);
    }

    /// <summary>Sets the primary display's scale (100, 125, 150 …); false with the reason when Windows doesn't offer it here.</summary>
    public static (bool Ok, string Detail) SetScale(int percent)
    {
        if (PrimarySource() is not { } source) return (false, "no active display path");
        var info = Scale();
        if (info == null) return (false, "the display's scale can't be read");
        if (info.Current == percent) return (true, $"already {percent}%");
        if (percent > info.Maximum) return (false, $"{percent}% is not offered at {Resolution().Width}x{Resolution().Height} (at most {info.Maximum}%)");
        int target = Array.IndexOf(Scales, percent), recommended = Array.IndexOf(Scales, info.Recommended);
        if (target < 0 || recommended < 0) return (false, $"{percent}% is not a Windows scale");
        var set = new DPI_SET
        {
            header = new HEADER { type = -4, size = Marshal.SizeOf<DPI_SET>(), adapterId = source.adapterId, id = source.id },
            scaleRel = target - recommended,
        };
        int result = DisplayConfigSetDeviceInfo(ref set);
        if (result != 0) return (false, $"DisplayConfigSetDeviceInfo: {result}");
        // The monitor's DPI follows within a moment.
        uint want = (uint)(96 * percent / 100);
        for (int i = 0; i < 50 && Native.PrimaryMonitorDpi() != want; i++) Thread.Sleep(100);
        uint now = Native.PrimaryMonitorDpi();
        return now == want ? (true, $"scale {percent}% ({now} dpi)") : (false, $"scale set to {percent}%, but the monitor reports {now} dpi");
    }
}
