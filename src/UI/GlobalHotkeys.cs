using System.Runtime.InteropServices;

namespace HomePodCast.UI;

/// <summary>
/// System-wide hotkeys (RegisterHotKey) on a message-only window owned by the UI thread. A combination
/// another program already holds fails to register; callers show that as "unavailable". Everything is
/// unregistered on Dispose (and by Windows when the process ends).
/// </summary>
internal sealed partial class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
    private const int ProbeId = 0xBFFF;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HashSet<HotkeyAction> _registered = [];

    public event Action<HotkeyAction>? Pressed;

    public GlobalHotkeys() => CreateHandle(new CreateParams { Parent = HwndMessage });

    /// <summary>Register (or re-register) an action. Volume keys auto-repeat while held; the others don't.</summary>
    public bool Register(HotkeyAction action, Hotkey key)
    {
        Unregister(action);
        bool repeat = action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown;
        if (!key.IsValid || !RegisterHotKey(Handle, Id(action), Flags(key, repeat), (uint)key.Key))
        {
            Log.Warn($"hotkey {key} for {action} not available (error {Marshal.GetLastPInvokeError()})");
            return false;
        }
        _registered.Add(action);
        return true;
    }

    public void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action)) UnregisterHotKey(Handle, Id(action));
    }

    public void UnregisterAll()
    {
        foreach (var a in _registered.ToList()) Unregister(a);
    }

    /// <summary>Is the combination free right now? Registers it for a moment and lets it go again.</summary>
    public bool Probe(Hotkey key)
    {
        if (!key.IsValid || !RegisterHotKey(Handle, ProbeId, Flags(key, false), (uint)key.Key)) return false;
        UnregisterHotKey(Handle, ProbeId);
        return true;
    }

    private static int Id(HotkeyAction action) => 1 + (int)action;

    private static uint Flags(Hotkey key, bool repeat)
    {
        uint f = repeat ? 0 : ModNoRepeat;
        if ((key.Modifiers & Keys.Control) != 0) f |= ModControl;
        if ((key.Modifiers & Keys.Alt) != 0) f |= ModAlt;
        if ((key.Modifiers & Keys.Shift) != 0) f |= ModShift;
        return f;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            int id = (int)m.WParam;
            if (id >= 1 && id <= Enum.GetValues<HotkeyAction>().Length) Pressed?.Invoke((HotkeyAction)(id - 1));
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        UnregisterAll();
        DestroyHandle();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
