using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>Parent and executable of every running process (Toolhelp snapshot; needs no process access).</summary>
internal static class ProcessTree
{
    private const uint SnapProcess = 0x2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static Dictionary<uint, ProcessEntry> Snapshot()
    {
        var result = new Dictionary<uint, ProcessEntry>();
        IntPtr snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero) return result;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                result[e.ProcessId] = new ProcessEntry(e.ProcessId, e.ParentProcessId, RouteRules.KeyFor(e.ExeFile ?? ""));
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }
}
