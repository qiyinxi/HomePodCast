using System.Diagnostics;

namespace HomePodCast.GuiSmoke;

/// <summary>Cross-process UIA regression client. It only accepts windows owned by the unit-test host; starts no app.</summary>
internal static class UiaProbe
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new ArgumentException("Expected root, source, left and right test handles.");
            var handles = args.Select(s => new IntPtr(long.Parse(s))).ToArray();
            Native.GetWindowThreadProcessId(handles[0], out uint owner);
            if (Process.GetProcessById((int)owner).ProcessName != "testhost")
                throw new InvalidOperationException("The UIA probe only operates on the unit-test host.");
            void CheckOwner(IntPtr hwnd)
            {
                Native.GetWindowThreadProcessId(hwnd, out uint actual);
                if (actual != owner) throw new InvalidOperationException("Test window belongs to another process.");
            }
            foreach (var hwnd in handles) CheckOwner(hwnd);
            var cached = Uia.Snapshot(handles[0]);
            Console.WriteLine("PROGRESS: cached tree"); Console.Out.Flush();
            var live = Uia.SnapshotLive(handles[0]);
            static string[] Contents(Node root) => root.Descendants().Prepend(root)
                .Select(n => $"{n.Type}|{n.Name}|{n.Hwnd}|{n.Rect}|{n.Enabled}|{n.ClassName}|{n.AutomationId}")
                .Order(StringComparer.Ordinal).ToArray();
            if (!Contents(cached).SequenceEqual(Contents(live)))
                throw new InvalidOperationException("Live and cached UIA readers disagree.");
            Console.WriteLine("READY"); Console.Out.Flush();
            var dialog = new IntPtr(long.Parse(Console.ReadLine()!));
            CheckOwner(dialog);
            _ = Uia.Snapshot(dialog);
            Native.PostMessage(dialog, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var wait = Stopwatch.StartNew();
            while (Native.IsWindowVisible(dialog) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            if (Native.IsWindowVisible(dialog)) throw new TimeoutException("Test dialog did not close.");
            _ = Uia.Snapshot(handles[0]);
            Console.WriteLine("PROGRESS: modal closed"); Console.Out.Flush();
            for (int i = 0; i < 4; i++)
            {
                Native.Msaa(handles[2 + i % 2])!.accDoDefaultAction(0);
                _ = Uia.Snapshot(handles[0]);
                Console.WriteLine($"PROGRESS: room {i}"); Console.Out.Flush();
            }
            foreach (var source in new[] { "本地影音文件", "电脑实时声音" })
            {
                Native.Msaa(handles[1])!.set_accValue(0, source);
                if (Native.MsaaValue(handles[1]) != source) throw new InvalidOperationException("Source did not change.");
                _ = Uia.Snapshot(handles[0]);
                Console.WriteLine("PROGRESS: source " + (source == "本地影音文件" ? "file" : "live")); Console.Out.Flush();
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
