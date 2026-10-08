using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HomePodCast.Players;

/// <summary>Process, named-pipe and TCP-listener lookups for finding players (all read-only).</summary>
internal static partial class PlayerWin32
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60; // Windows 8.1+
    private const uint StillActive = 259;

    /// <summary>The process's full command line, or null (exited, or no access).</summary>
    public static string? CommandLine(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process.IsInvalid) return null;
        int size = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out int needed);
                if (status == 0)
                {
                    var us = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return us.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
                if (needed <= size) return null;
                size = needed;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>Full path of the process's executable, or null.</summary>
    public static string? ImagePath(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process.IsInvalid) return null;
        var buffer = new char[1024];
        int length = buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
    }

    public static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        return !process.IsInvalid && GetExitCodeProcess(process, out uint code) && code == StillActive;
    }

    /// <summary>A command line split the way the C runtime does it.</summary>
    public static string[] SplitArgs(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>The process that created the pipe this client handle is connected to; 0 if unknown.</summary>
    public static int PipeServerProcessId(SafePipeHandle handle) =>
        GetNamedPipeServerProcessId(handle, out uint pid) ? (int)pid : 0;

    /// <summary>Names of the named pipes that exist now (without the \\.\pipe\ prefix).</summary>
    public static HashSet<string> PipeNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\"))
                names.Add(path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? path[9..] : Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"players: listing pipes: {ex.Message}");
        }
        return names;
    }

    public readonly record struct Listener(IPEndPoint EndPoint, int Pid);

    /// <summary>Every listening TCP socket (IPv4 and IPv6) with the process that owns it.</summary>
    public static List<Listener> TcpListeners()
    {
        var result = new List<Listener>();
        ReadTable(AfInet, (row, _) =>
        {
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid
            uint addr = (uint)Marshal.ReadInt32(row, 4);
            int port = PortOf(Marshal.ReadInt32(row, 8));
            int pid = Marshal.ReadInt32(row, 20);
            result.Add(new Listener(new IPEndPoint(new IPAddress(addr), port), pid));
        }, 24);
        ReadTable(AfInet6, (row, _) =>
        {
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, remoteAddr[16], remoteScopeId, remotePort, state, owningPid
            var bytes = new byte[16];
            Marshal.Copy(row, bytes, 0, 16);
            uint scope = (uint)Marshal.ReadInt32(row, 16);
            int port = PortOf(Marshal.ReadInt32(row, 20));
            int pid = Marshal.ReadInt32(row, 52);
            result.Add(new Listener(new IPEndPoint(new IPAddress(bytes, scope), port), pid));
        }, 56);
        return result;
    }

    /// <summary>The process listening on a TCP port on any address; 0 if none.</summary>
    public static int ListenerOwner(int port) => TcpListeners().FirstOrDefault(l => l.EndPoint.Port == port).Pid;

    private static int PortOf(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order in the low 16 bits

    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3;

    private static void ReadTable(int family, Action<IntPtr, int> row, int rowSize)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint status = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (status == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew, size is updated
                if (status != 0) return;
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++) row(buffer + 4 + i * rowSize, i);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProcessHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial ProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(ProcessHandle process, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(ProcessHandle process, uint flags, [Out] char[] buffer, ref int size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(ProcessHandle process, int infoClass, IntPtr info, int length, out int returned);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
