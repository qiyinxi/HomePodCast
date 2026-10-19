using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace HomePodCast;

internal static partial class Native
{
    /// <summary>
    /// The local address this PC sends from to reach <paramref name="destination"/>: the route table's best route and
    /// its source address (GetBestRoute2), the same choice connecting a socket makes, but without a socket. A UDP socket
    /// that Connect binds is listed as 0.0.0.0 (every address) in Windows' UDP table for as long as it is open, though
    /// its own local address is the one chosen. 2026-10-16: the tests' loopback guard failed on a 0.0.0.0 port, and
    /// NetworkProfile.ForSpeaker kept such a probe open through the Network List Manager lookup (about 100 ms).
    /// Throws a Win32Exception when there is no route.
    /// </summary>
    internal static unsafe IPAddress SourceAddressFor(IPAddress destination)
    {
        var to = new byte[SockaddrInetSize];
        var serialized = new IPEndPoint(destination, 0).Serialize(); // sockaddr_in or sockaddr_in6, as Windows lays them out
        serialized.Buffer.Span[..serialized.Size].CopyTo(to);
        var route = new byte[512]; // MIB_IPFORWARD_ROW2 (104 bytes), not read
        var from = new byte[SockaddrInetSize];
        int error;
        fixed (byte* pTo = to, pRoute = route, pFrom = from)
            error = GetBestRoute2(null, 0, null, pTo, 0, pRoute, pFrom);
        if (error != 0) throw new Win32Exception(error);

        // SOCKADDR_INET: family, then sockaddr_in's address at 4, or sockaddr_in6's at 8 and its scope at 24.
        switch (BinaryPrimitives.ReadUInt16LittleEndian(from))
        {
            case AfInet:
                return new IPAddress(from.AsSpan(4, 4));
            case AfInet6:
                var address = new IPAddress(from.AsSpan(8, 16));
                // A scope only where one belongs, as a connected socket's local address and the adapters' addresses have it.
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                    ? new IPAddress(from.AsSpan(8, 16), BinaryPrimitives.ReadUInt32LittleEndian(from.AsSpan(24))) : address;
            default:
                throw new InvalidOperationException($"the route to {destination} has no source address");
        }
    }

    private const int SockaddrInetSize = 28, AfInet = 2, AfInet6 = 23;

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial int GetBestRoute2(void* interfaceLuid, uint interfaceIndex, void* sourceAddress,
        byte* destinationAddress, uint addressSortOptions, byte* bestRoute, byte* bestSourceAddress);

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

    internal const uint RestartNoCrash = 1, RestartNoHang = 2, RestartNoPatch = 4, RestartNoReboot = 8;

    /// <summary>Lets Restart Manager (installers) restart the app with these arguments after closing it.</summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegisterApplicationRestart(string commandLine, uint flags);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeBeginPeriod(uint ms);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeEndPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>How long ago the last keyboard or mouse input of this Windows session was; null if Windows doesn't say.</summary>
    public static TimeSpan? SinceLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)); // both wrap at 2^32 ms
    }

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
