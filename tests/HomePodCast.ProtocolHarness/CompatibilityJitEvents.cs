using System.Diagnostics.Tracing;

namespace HomePodCast.ProtocolHarness;

// Diagnostic-only native runtime method events; callbacks are delivered off the paced sender thread.
// Event IDs/payloads: https://learn.microsoft.com/dotnet/fundamentals/diagnostics/runtime-method-events
internal sealed class CompatibilityJitEvents : EventListener
{
    private readonly object _gate = new();
    private readonly Dictionary<(ulong Method, long Thread), Started> _started = new(256);
    private readonly Completed[] _completed = new Completed[256];
    private int _count;
    private readonly DateTime _began = DateTime.UtcNow;
    private record struct Started(DateTime At, string Type, string Method);
    private record struct Completed(DateTime At, string Type, string Method, long Thread, double Milliseconds, uint Flags);

    public CompatibilityJitEvents()
    {
        // Subscribe after derived fields exist: EventListener's base constructor can discover sources immediately.
        EventSourceCreated += (_, args) => EnableRuntime(args.EventSource);
        foreach (var source in EventSource.GetSources()) EnableRuntime(source);
    }

    private void EnableRuntime(EventSource? source)
    {
        if (source?.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventId is not (143 or 145) || data.PayloadNames == null || data.Payload == null) return;
        object? Field(string name)
        {
            for (int i = 0; i < data.PayloadNames.Count; i++)
                if (data.PayloadNames[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return data.Payload[i];
            return null;
        }
        if (Field("MethodNamespace") is not string type || !type.StartsWith("HomePodCast.", StringComparison.Ordinal)) return;
        if (Field("MethodID") is not ulong method || Field("MethodName") is not string name) return;
        var key = (method, data.OSThreadId);
        lock (_gate)
        {
            if (data.EventId == 145) _started[key] = new(data.TimeStamp, type, name);
            else if (_started.Remove(key, out var start))
            {
                double duration = (data.TimeStamp - start.At).TotalMilliseconds;
                if (duration >= 5 || type == "HomePodCast.Net.PtpRealtimeSender" && name == "Send")
                    _completed[_count++ % _completed.Length] = new(start.At, type, name, data.OSThreadId,
                        duration, Field("MethodFlags") is uint flags ? flags : 0);
            }
        }
    }

    public void Report()
    {
        lock (_gate)
        {
            int first = Math.Max(0, _count - _completed.Length);
            for (int i = first; i < _count; i++)
            {
                var item = _completed[i % _completed.Length];
                Console.WriteLine($"Compatibility JIT: atMs={(item.At - _began).TotalMilliseconds:F1}, " +
                    $"method={item.Type}.{item.Method}, thread={item.Thread}, durationMs={item.Milliseconds:F1}, flags=0x{item.Flags:x}");
            }
        }
    }

    public static void SelfTest()
    {
        using var events = new CompatibilityJitEvents();
        // Compile without invoking the sender: no socket, clock, device or audio is opened by this check.
        var method = typeof(HomePodCast.Net.PtpRealtimeSender).GetMethod("Send",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
        long deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            lock (events._gate) if (events._count != 0) break;
            if (Environment.TickCount64 >= deadline) throw new Exception("Runtime JIT diagnostic events were not observed");
            Thread.Sleep(10);
        }
        events.Report();
    }
}
