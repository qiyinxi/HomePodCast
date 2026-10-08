using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HomePodCast.UI;

/// <summary>
/// <see cref="VolumeKeyMode.WhileStreaming"/> (and the other modes while Windows has no output device): a low-level
/// keyboard hook (WH_KEYBOARD_LL) that takes the keyboard's volume up / down / mute keys away from Windows while we
/// stream, so they change the HomePod instead of the Windows volume. Installed only while needed (<see cref="Install"/> / <see cref="Uninstall"/>; TrayApp unhooks on stop,
/// mode change and exit), on its own thread with its own message loop. While installed, every key press in the
/// system passes through <see cref="HookProc"/>, so it only decides (<see cref="VolumeKeyRules.Decide"/>), hands the
/// command to <c>post</c> (which only queues it for the UI thread) and returns: 1 swallows the key, anything else
/// goes on unchanged. Windows silently drops a hook that ever takes too long (LowLevelHooksTimeout), so the hook is
/// re-installed every minute while active.
/// <para>Limits: devices that send only WM_APPCOMMAND (no VK_VOLUME_* key event; some remotes, or vendor software
/// for mice and knobs) can't be caught here without a DLL injected into other programs: with them, use
/// <see cref="VolumeKeyMode.FollowWindows"/>. Input to elevated (administrator) windows doesn't pass through a hook
/// of a normal program, so the keys change Windows while such a window is in front.</para>
/// </summary>
internal sealed unsafe partial class VolumeKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13, WmQuit = 0x0012, WmTimer = 0x0113, PmNoRemove = 0;
    private const uint LlkhfUp = 0x80;
    private const uint RefreshMs = 60_000;

    /// <summary>The installed hook (one per process); read by the static callback.</summary>
    private static VolumeKeyHook? s_current;

    private readonly Action<VolumeKeyCommand> _post;
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile VolumeKeyMode _mode;
    private volatile bool _streaming, _outputDevice;
    private int _swallowedDown; // bits of the volume keys whose key-down was swallowed (hook thread only)

    /// <param name="post">Called on the hook thread for each command: must only queue it (e.g. SynchronizationContext.Post).</param>
    public VolumeKeyHook(Action<VolumeKeyCommand> post) => _post = post;

    public bool Installed => _thread != null;

    /// <summary>The state the callback decides by (also guards the moments around install and uninstall). Thread-safe.</summary>
    public void SetState(VolumeKeyMode mode, bool streaming, bool outputDevice)
    {
        _mode = mode;
        _streaming = streaming;
        _outputDevice = outputDevice;
    }

    /// <summary>Start the hook thread (no-op when installed). Call from the UI thread.</summary>
    public void Install()
    {
        if (_thread != null) return;
        var ready = new ManualResetEventSlim(); // not disposed: the thread may set it after a timed-out wait
        // Highest priority: while the hook is in, every key press in the system waits for this thread.
        var thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Volume-key hook", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait(5000);
        if (!thread.IsAlive) return; // SetWindowsHookEx failed (logged)
        _thread = thread;
        Log.Info("volume keys: keyboard hook on (volume keys go to the HomePod while streaming)");
    }

    /// <summary>Remove the hook and end its thread (no-op when not installed). Call from the UI thread.</summary>
    public void Uninstall()
    {
        var thread = _thread;
        if (thread == null) return;
        _thread = null;
        PostThreadMessageW(_threadId, WmQuit, 0, 0);
        if (!thread.Join(2000)) Log.Warn("volume keys: hook thread did not end");
        Log.Info("volume keys: keyboard hook off");
    }

    public void Dispose() => Uninstall();

    private void Run(ManualResetEventSlim ready)
    {
        IntPtr hook = IntPtr.Zero;
        UIntPtr timer = UIntPtr.Zero;
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessageW(out _, IntPtr.Zero, 0, 0, PmNoRemove); // create the queue before anyone posts WM_QUIT
            Volatile.Write(ref s_current, this);
            hook = SetHook();
            if (hook == IntPtr.Zero)
            {
                Log.Warn($"volume keys: SetWindowsHookEx failed (error {Marshal.GetLastPInvokeError()})");
                return;
            }
            timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, RefreshMs, IntPtr.Zero);
            ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message != WmTimer) continue;
                // Re-install, in case Windows dropped the hook after a timeout (it never says so).
                var fresh = SetHook();
                if (fresh == IntPtr.Zero) continue;
                UnhookWindowsHookEx(hook);
                hook = fresh;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"volume keys: hook thread: {ex.Message}");
        }
        finally
        {
            if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            Interlocked.CompareExchange(ref s_current, null, this);
            _swallowedDown = 0;
            ready.Set();
        }
    }

    private static IntPtr SetHook() => SetWindowsHookExW(WhKeyboardLl, &HookProc, GetModuleHandleW(null), 0);

    /// <summary>Runs for every key event in the system while installed: decide, queue, return. Never throws.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != 0)
        {
            try
            {
                var key = (KbdLlHookStruct*)lParam;
                if (Volatile.Read(ref s_current) is { } self && self.OnKey((int)key->VkCode, (key->Flags & LlkhfUp) == 0))
                    return 1;
            }
            catch
            {
                // let the key through
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool OnKey(int vk, bool down)
    {
        int bit = vk switch
        {
            VolumeKeyRules.VkVolumeUp => 1,
            VolumeKeyRules.VkVolumeDown => 2,
            VolumeKeyRules.VkVolumeMute => 4,
            _ => 0,
        };
        if (bit == 0) return false; // every other key: untouched
        var decision = VolumeKeyRules.Decide(_mode, _streaming, _outputDevice, vk, down, (_swallowedDown & bit) != 0);
        if (down && decision.Swallow) _swallowedDown |= bit;
        else _swallowedDown &= ~bit;
        if (decision.Command != VolumeKeyCommand.None) _post(decision.Command);
        return decision.Swallow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nint, nint, nint> proc, IntPtr hMod, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(IntPtr hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapseMs, IntPtr proc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(IntPtr hwnd, UIntPtr id);
}
