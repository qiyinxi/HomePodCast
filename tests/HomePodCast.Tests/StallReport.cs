using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace HomePodCast.Tests;

/// <summary>
/// What a test can say when its wait for another thread ran out: that thread's state in the kernel (running, ready for a
/// CPU, or waiting and on what), the CPU it and this process used meanwhile, who holds the loader lock, what this
/// process's other threads are doing, and how busy the machine is and with which processes. Takes half a second.
/// 2026-10-17 CI (run 39026442901): a 20 s wait for a Media Foundation open ran out with only "did not finish". In the
/// same 30 s every other open in the test process, two unrelated tests and a PowerShell that another test started were
/// several to hundreds of times slower than in other runs, and nothing recorded whether the thread waited, starved for
/// CPU or worked.
/// </summary>
internal static class StallReport
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);
    private static int s_prepared;

    /// <summary>
    /// Binds the native calls and compiles the snapshot before the wait: a call's first use loads its library under
    /// the loader lock, so a report taken while another thread holds that lock would otherwise wait for it too.
    /// </summary>
    public static void Prepare()
    {
        if (IntPtr.Size != 8 || Interlocked.Exchange(ref s_prepared, 1) != 0) return;
        try
        {
            var first = Snapshot.Take();
            Snapshot.Take().Since(first, GetCurrentThreadId());
        }
        catch (Exception) { } // Describe says what went wrong
    }

    /// <param name="threadId">The operating system id of the thread that did not finish; 0 for none in particular.</param>
    public static string Describe(int threadId = 0)
    {
        try
        {
            if (IntPtr.Size != 8) return "no stall report in a 32-bit process";
            var before = Snapshot.Take();
            Thread.Sleep(Window);
            return Snapshot.Take().Since(before, threadId);
        }
        catch (Exception ex)
        {
            return $"no stall report ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private sealed record ThreadInfo(int State, int WaitReason, int Priority, long Cpu);
    private sealed record ProcessInfo(string Name, long Cpu, uint HardFaults);

    private sealed class Snapshot
    {
        private long _ticks, _idle, _kernel, _user, _loaderLockOwner;
        private readonly Dictionary<long, ProcessInfo> _processes = [];
        private readonly Dictionary<long, ThreadInfo> _threads = [];

        public static Snapshot Take()
        {
            var snapshot = new Snapshot { _ticks = Stopwatch.GetTimestamp(), _loaderLockOwner = LoaderLockOwner() };
            GetSystemTimes(out snapshot._idle, out snapshot._kernel, out snapshot._user);
            snapshot.ReadProcesses();
            return snapshot;
        }

        /// <summary>SystemProcessInformation: every process with its CPU time, and this process's threads.</summary>
        private void ReadProcesses()
        {
            for (int size = 1 << 20; size <= 1 << 28; size *= 2)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQuerySystemInformation(5, buffer, size, out _);
                    if (status == unchecked((int)0xC0000004)) continue; // STATUS_INFO_LENGTH_MISMATCH: a larger buffer
                    if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation 0x{status:x8}");
                    for (int offset = 0; ; )
                    {
                        // SYSTEM_PROCESS_INFORMATION (64-bit): the documented fields at their fixed offsets, then its threads.
                        IntPtr process = buffer + offset;
                        int next = Marshal.ReadInt32(process, 0), threads = Marshal.ReadInt32(process, 4);
                        long pid = Marshal.ReadIntPtr(process, 80);
                        IntPtr name = Marshal.ReadIntPtr(process, 64);
                        _processes[pid] = new(name == 0 ? "Idle" : Marshal.PtrToStringUni(name, Marshal.ReadInt16(process, 56) / 2),
                            Marshal.ReadInt64(process, 40) + Marshal.ReadInt64(process, 48), (uint)Marshal.ReadInt32(process, 16));
                        if (pid == Environment.ProcessId)
                            for (int i = 0; i < threads; i++)
                            {
                                IntPtr thread = process + 0x100 + i * 0x50; // SYSTEM_THREAD_INFORMATION
                                _threads[Marshal.ReadIntPtr(thread, 48)] = new(Marshal.ReadInt32(thread, 68), Marshal.ReadInt32(thread, 72),
                                    Marshal.ReadInt32(thread, 56), Marshal.ReadInt64(thread, 0) + Marshal.ReadInt64(thread, 8));
                            }
                        if (next == 0) return;
                        offset += next;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("NtQuerySystemInformation: no buffer was large enough");
        }

        public string Since(Snapshot before, int threadId)
        {
            double seconds = (_ticks - before._ticks) / (double)Stopwatch.Frequency;
            string Cpus(long cpu) => (cpu / 1e7 / seconds).ToString("0.00", CultureInfo.InvariantCulture);
            var parts = new List<string>();

            if (threadId != 0)
                parts.Add(_threads.TryGetValue(threadId, out var thread)
                    ? $"thread {threadId} {StateText(thread)} at priority {thread.Priority}, " +
                      $"{(thread.Cpu - (before._threads.TryGetValue(threadId, out var then) ? then.Cpu : 0)) / 10_000} ms CPU in the last " +
                      $"{seconds.ToString("0.0", CultureInfo.InvariantCulture)} s, {thread.Cpu / 10_000} ms in all"
                    : $"thread {threadId} has ended");

            // Owned in both snapshots: held for at least the window, not just met in passing.
            parts.Add(_loaderLockOwner == 0 ? "loader lock free"
                : $"loader lock held by thread {_loaderLockOwner}{(_loaderLockOwner == threadId ? " (this one)" : "")}" +
                  (_loaderLockOwner == before._loaderLockOwner ? " for the whole window" : ""));

            var states = _threads.Values.GroupBy(StateText).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
            var busiest = _threads.Where(t => t.Key != threadId)
                .Select(t => (Id: t.Key, t.Value.Priority, Cpu: t.Value.Cpu - (before._threads.TryGetValue(t.Key, out var b) ? b.Cpu : 0)))
                .Where(t => t.Cpu > 0).OrderByDescending(t => t.Cpu).Take(3).Select(t => $"{t.Id} at priority {t.Priority} ({Cpus(t.Cpu)} CPUs)");
            long ours = Delta(before, Environment.ProcessId, p => p.Cpu);
            parts.Add($"this process {Cpus(ours)} CPUs, {Delta(before, Environment.ProcessId, p => p.HardFaults)} hard faults, " +
                $"threads: {string.Join(", ", states)}; busiest other threads: {Join(busiest)}");

            long idle = _idle - before._idle, all = _kernel - before._kernel + _user - before._user; // kernel time includes idle
            var processes = _processes.Where(p => p.Key != 0)
                .Select(p => (p.Value.Name, Cpu: Delta(before, p.Key, q => q.Cpu))).Where(p => p.Cpu > 0)
                .OrderByDescending(p => p.Cpu).Take(4).Select(p => $"{p.Name} ({Cpus(p.Cpu)} CPUs)");
            parts.Add($"machine {(all > 0 ? 100 * (all - idle) / all : 0)} % busy of {Environment.ProcessorCount} CPUs, " +
                $"{_processes.Keys.Sum(pid => Delta(before, pid, p => p.HardFaults))} hard faults; busiest processes: {Join(processes)}");
            return string.Join("; ", parts);
        }

        private long Delta(Snapshot before, long pid, Func<ProcessInfo, long> value) =>
            _processes.TryGetValue(pid, out var now) && before._processes.TryGetValue(pid, out var then) ? Math.Max(0, value(now) - value(then)) : 0;

        private static string Join(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "none";
    }

    private static readonly string[] States =
        ["initialized", "ready for a CPU", "running", "standby", "terminated", "waiting", "in transition", "deferred ready"];

    private static readonly string[] WaitReasons =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest", "WrExecutive",
        "WrFreePage", "WrPageIn", "WrPoolAllocation", "WrDelayExecution", "WrSuspended", "WrUserRequest", "WrEventPair",
        "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut", "WrRendezvous", "WrKeyedEvent",
        "WrTerminated", "WrProcessInSwap", "WrCpuRateControl", "WrCalloutStack", "WrKernel", "WrResource", "WrPushLock",
        "WrMutex", "WrQuantumEnd", "WrDispatchInt", "WrPreempted", "WrYieldExecution", "WrFastMutex", "WrGuardedMutex",
        "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault", "WrIoRing", "WrMdlCache", "WrRcu",
    ];

    /// <summary>"waiting (UserRequest)", "ready for a CPU", "running"…</summary>
    private static string StateText(ThreadInfo thread) => thread.State == 5
        ? $"waiting ({(thread.WaitReason >= 0 && thread.WaitReason < WaitReasons.Length ? WaitReasons[thread.WaitReason] : "reason " + thread.WaitReason)})"
        : thread.State >= 0 && thread.State < States.Length ? States[thread.State] : "state " + thread.State;

    /// <summary>The thread that holds the loader lock (DLL loads, thread starts and exits wait for it), 0 if none.</summary>
    private static long LoaderLockOwner()
    {
        if (NtQueryInformationProcess(-1, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) < 0) return 0;
        IntPtr loaderLock = Marshal.ReadIntPtr(basic.Peb, 0x110);             // PEB.LoaderLock, an RTL_CRITICAL_SECTION
        return loaderLock == 0 ? 0 : (long)Marshal.ReadIntPtr(loaderLock, 0x10); // its OwningThread
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, Peb, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll")] internal static extern int GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int needed);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out ProcessBasicInformation info, int length, out int needed);
}
