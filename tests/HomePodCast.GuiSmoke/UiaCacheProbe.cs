using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// TEMPORARY experiment (claude/uia-cache-race), removed before the PR is final: reads the raw bulk cache response
/// that AutomationElement.FromHandle parses, and records every malformed one with the tree around the break.
/// </summary>
internal static class UiaCacheProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("GUI_UIA_PROBE") == "1";
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Assembly Client = typeof(AutomationElement).Assembly;
    private static readonly Type CoreApi = Client.GetType("MS.Internal.Automation.UiaCoreApi", true)!;
    private static readonly MethodInfo NodeFromHandle = CoreApi.GetMethod("UiaNodeFromHandle", Any)!;
    private static readonly MethodInfo GetUpdatedCache = CoreApi.GetMethod("UiaGetUpdatedCache", Any)!;
    private static readonly MethodInfo Build = Client.GetType("MS.Internal.Automation.CacheHelper", true)!
        .GetMethod("BuildAutomationElementsFromResponse", Any)!;
    private static readonly MethodInfo UiaRequest = typeof(CacheRequest).GetMethod("GetUiaCacheRequest", Any)!;

    private static readonly object Gate = new();
    private static string _phase = "scenario", _kind = "scenario";
    private static readonly Stopwatch PhaseClock = Stopwatch.StartNew();
    private static readonly Dictionary<string, int[]> ByPhase = []; // phase kind → [collections, malformed]
    private static int _collections, _malformed, _exhausted, _recovered, _details;
    public static string? OutFile;

    public static void Reset(string? outFile)
    {
        lock (Gate)
        {
            OutFile = outFile;
            ByPhase.Clear();
            _collections = _malformed = _exhausted = _recovered = _details = 0;
            _phase = _kind = "scenario";
            PhaseClock.Restart();
        }
    }

    /// <summary>Iteration 0: the ordinary scenario; 1: the first app run of a stress job; later: repeated runs.</summary>
    public static void SetPhase(int iteration, string kind)
    {
        lock (Gate)
        {
            _phase = $"{iteration}/{kind}";
            _kind = iteration == 0 ? kind : (iteration == 1 ? "1st " : "nth ") + kind;
            PhaseClock.Restart();
        }
    }

    private static string Kind(string phase) => _kind;

    public static AutomationElement Collect(CacheRequest request, IntPtr hwnd)
    {
        var hnode = (SafeHandle)Call(NodeFromHandle, null, hwnd)!;
        if (hnode.IsInvalid) throw new ElementNotAvailableException("no UIA node for the window");
        object uiaRequest = UiaRequest.Invoke(request, null)!;
        var normalize = Enum.ToObject(GetUpdatedCache.GetParameters()[2].ParameterType, 0);
        object response = Call(GetUpdatedCache, null, hnode, uiaRequest, normalize, null)!;
        var tree = (string?)response.GetType().GetProperty("TreeStructure", Any)!.GetValue(response);
        var data = (object[,]?)response.GetType().GetProperty("RequestedData", Any)!.GetValue(response);
        string? problem = Check(tree, data, out int failAt);
        lock (Gate)
        {
            _collections++;
            var counts = ByPhase.TryGetValue(Kind(_phase), out var c) ? c : ByPhase[Kind(_phase)] = new int[2];
            counts[0]++;
            if (problem != null)
            {
                _malformed++;
                counts[1]++;
                Record(hwnd, tree, data, problem, failAt);
            }
        }
        var root = (AutomationElement?)Call(Build, null, uiaRequest, response);
        return root ?? throw new InvalidOperationException("malformed UIA cache response (parser returned no root): " + problem);
    }

    public static void Recovered(int attempt)
    {
        lock (Gate) { _recovered++; Append($"    recovered at attempt {attempt}"); }
    }

    public static void ExhaustedBudget(Exception ex)
    {
        lock (Gate) { _exhausted++; Append($"!!! retry budget exhausted in {_phase}: {ex.GetType().Name}: {ex.Message}"); }
    }

    public static string Summary()
    {
        lock (Gate)
            return $"UIA probe: {_collections} collections, {_malformed} malformed, {_recovered} recovered by retry, " +
                   $"{_exhausted} exhausted; by phase: " +
                   string.Join(", ", ByPhase.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value[1]}/{p.Value[0]}"));
    }

    private static object? Call(MethodInfo method, object? target, params object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Simulates CacheHelper.ParseTreeDescription exactly; returns why it would fail, if it would.</summary>
    private static string? Check(string? tree, object[,]? data, out int failAt)
    {
        failAt = -1;
        if (tree == null) return data == null ? null : "tree null, data present";
        int rows = data?.GetLength(0) ?? 0, index = 0, prop = 0;
        string? error = null;
        int at = -1;
        bool? Node(int depth) // true: a node; false: not a node (end of siblings); null: fatal
        {
            if (tree.Length == 0) return false;
            if (index >= tree.Length) { error = $"IOOR: string ended at {index} where depth {depth} expects a node or ')'"; at = index; return null; }
            char c = tree[index];
            if (c != '(' && c != 'P') return false;
            index++;
            if (c == 'P')
            {
                if (prop >= rows) { error = $"IOOR: 'P' #{prop} at {index - 1} has no property row (rows {rows})"; at = index - 1; return null; }
                prop++;
            }
            while (true)
            {
                var child = Node(depth + 1);
                if (child == null) return null;
                if (child == false) break;
            }
            if (index >= tree.Length) { error = $"IOOR: string ended at {index}, missing ')' at depth {depth}"; at = index; return null; }
            if (tree[index] != ')') { error = $"null root: '{tree[index]}' at {index} where depth {depth} expects ')'"; at = index; return null; }
            index++;
            return true;
        }
        var root = Node(0);
        if (root == null) { failAt = at; return error; }
        if (root == false) { failAt = 0; return "null root: the structure starts without a node"; }
        if (index != tree.Length) { failAt = index; return $"null root: extra characters after {index} of {tree.Length}"; }
        if (data != null && prop != rows) { failAt = index; return $"null root: {prop} 'P' nodes but {rows} property rows"; }
        return null;
    }

    private static void Record(IntPtr hwnd, string? tree, object[,]? data, string problem, int failAt)
    {
        int rows = data?.GetLength(0) ?? 0, cols = data?.GetLength(1) ?? 0;
        string t = tree ?? "";
        var head = $"--- {_phase} +{PhaseClock.ElapsedMilliseconds} ms hwnd {hwnd:X}: {problem}; length {t.Length}, " +
                   $"P {t.Count(ch => ch == 'P')}, '(' {t.Count(ch => ch == '(')}, ')' {t.Count(ch => ch == ')')}, rows {rows}x{cols}; " +
                   $"caller {Caller()}";
        Console.WriteLine("probe " + head);
        if (_details++ > 40) { Append(head + " (outline omitted)"); return; }
        var sb = new StringBuilder(head).AppendLine();
        sb.AppendLine("structure: " + t);
        // The outline: each node with its row, as far as rows exist; the break is marked.
        int depth = 0, prop = 0;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (i == failAt) sb.Append(' ', depth * 2).AppendLine("<<<<<<<< parser fails here");
            if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (c != 'P' && c != '(') { sb.Append(' ', depth * 2).AppendLine($"?? '{c}'"); continue; }
            string row = c == 'P' ? (prop < rows ? Row(data!, prop) : "(no row)") : "(no properties)";
            sb.Append(' ', depth * 2).AppendLine($"{c}#{(c == 'P' ? prop : -1)} {row}");
            if (c == 'P') prop++;
            depth++;
        }
        if (failAt >= t.Length) sb.Append(' ', depth * 2).AppendLine("<<<<<<<< parser fails here (end of string)");
        for (; prop < rows; prop++) sb.AppendLine($"unclaimed row #{prop} {Row(data!, prop)}");
        Append(sb.ToString());
    }

    private static string Row(object[,] data, int row)
    {
        string V(int col) => col < data.GetLength(1) ? data[row, col] switch
        {
            null => "null",
            ControlType type => type.ProgrammaticName.Replace("ControlType.", ""),
            string s => "\"" + (s.Length > 50 ? s[..47] + "..." : s).Replace('\n', ' ') + "\"",
            var o => o.ToString() ?? "",
        } : "-";
        // Columns: node, Name, ControlType, BoundingRectangle, NativeWindowHandle, IsEnabled, ClassName, AutomationId.
        return $"{V(2)} {V(1)} rect={V(3)} hwnd={(data[row, 4] is int h ? h.ToString("X") : V(4))} class={V(6)} id={V(7)}";
    }

    private static string Caller()
    {
        var frames = new StackTrace(false).GetFrames().Select(f => f.GetMethod()).Where(m => m?.DeclaringType != null
            && (m.DeclaringType == typeof(Walker) || m.DeclaringType.DeclaringType == typeof(Walker)));
        return string.Join(" < ", frames.Take(3).Select(m => m!.Name));
    }

    private static void Append(string text)
    {
        if (OutFile != null) File.AppendAllText(OutFile, text + Environment.NewLine);
    }
}
