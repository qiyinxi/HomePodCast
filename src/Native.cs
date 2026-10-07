using System.Runtime.InteropServices;

namespace HomePodCast;

internal static partial class Native
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeBeginPeriod(uint ms);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeEndPeriod(uint ms);

    /// <summary>Registers the calling thread with MMCSS (e.g. "Pro Audio"); dispose to revert.</summary>
    public static IDisposable EnterMmcss(string task)
    {
        uint index = 0;
        var h = AvSetMmThreadCharacteristics(task, ref index);
        return new Revert(() => { if (h != IntPtr.Zero) AvRevertMmThreadCharacteristics(h); });
    }

    private sealed class Revert(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>High-resolution waitable timer (Win10 1803+); falls back to a 1 ms timer period.</summary>
    public sealed class PreciseTimer : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly bool _fallback;

        public PreciseTimer()
        {
            _handle = CreateWaitableTimerEx(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_handle == IntPtr.Zero)
            {
                _fallback = true;
                timeBeginPeriod(1);
            }
        }

        /// <summary>Sleep for roughly the given number of QPC ticks.</summary>
        public void Sleep(long qpcTicks)
        {
            if (qpcTicks <= 0) return;
            if (_fallback)
            {
                Thread.Sleep(Math.Max(1, (int)(qpcTicks * 1000 / System.Diagnostics.Stopwatch.Frequency)));
                return;
            }
            long due = -(long)((double)qpcTicks * 10_000_000 / System.Diagnostics.Stopwatch.Frequency);
            if (due == 0) due = -1;
            if (SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_handle, 0xFFFFFFFF);
        }

        public void Dispose()
        {
            if (_fallback) timeEndPeriod(1);
            else CloseHandle(_handle);
        }
    }
}
