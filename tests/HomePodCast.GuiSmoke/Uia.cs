using System.Text;
using System.Windows.Automation;

namespace HomePodCast.GuiSmoke;

/// <summary>One element of a window's UI Automation tree, read in one go (a cached snapshot).</summary>
internal sealed class Node
{
    public required AutomationElement Element { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; } // "Button", "Text", "Pane", … (ControlType without "ControlType.")
    public required Rectangle Rect { get; init; }
    public required IntPtr Hwnd { get; init; }
    public required bool Enabled { get; init; }
    public required string ClassName { get; init; }
    public required string AutomationId { get; init; }
    public Node? Parent { get; init; }
    public List<Node> Children { get; } = [];

    public bool HasSize => Rect.Width > 0 && Rect.Height > 0;

    private string? _text;

    /// <summary>The control's own text (its window text), read when asked.</summary>
    public string Text => _text ??= Hwnd != IntPtr.Zero ? Native.ControlText(Hwnd)
        : Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : "";

    /// <summary>
    /// Shows or is named <paramref name="text"/>. A settings row (SettingRow) may name its control after the row's title;
    /// such a button can also be found by what it says, its window text.
    /// </summary>
    public bool Says(string text) => Name == text || Text == text;

    /// <summary>Shown: its own window (if it has one) and every ancestor window are visible.</summary>
    public bool Shown
    {
        get
        {
            for (var n = this; n != null; n = n.Parent)
                if (n.Hwnd != IntPtr.Zero && !Native.IsWindowVisible(n.Hwnd)) return false;
            return true;
        }
    }

    /// <summary>Inside the window frame's title bar or a scroll bar (Windows' parts, not the app's).</summary>
    public bool InChrome
    {
        get
        {
            for (var n = this; n != null; n = n.Parent)
                if (n.Type is "TitleBar" or "ScrollBar" or "MenuBar" or "Thumb") return true;
            return false;
        }
    }

    public IEnumerable<Node> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public IEnumerable<Node> Ancestors()
    {
        for (var n = Parent; n != null; n = n.Parent) yield return n;
    }

    public string Describe() => $"{Type} \"{Shorten(Name)}\" [{Rect.X},{Rect.Y} {Rect.Width}x{Rect.Height}]";

    public static string Shorten(string text) => text.Length > 60 ? text[..57].Replace('\n', ' ') + "…" : text.Replace('\n', ' ');
}

/// <summary>UI Automation through System.Windows.Automation, with MSAA (oleacc) where the managed API has no access.</summary>
internal static class Uia
{
    private static readonly CacheRequest Request = MakeRequest();

    private static CacheRequest MakeRequest()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in new[]
                 {
                     AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.NativeWindowHandleProperty, AutomationElement.IsEnabledProperty, AutomationElement.ClassNameProperty,
                     AutomationElement.AutomationIdProperty,
                 })
            request.Add(p);
        return request;
    }

    private const int Attempts = 4;

    /// <summary>Where a second read after a truncated tree is noted: the running scenario's report (the console without one).</summary>
    [ThreadStatic] internal static Action<string>? Noted;

    /// <summary>The whole tree under a window, read in one request (again if a control went away while it was read).</summary>
    public static Node Snapshot(IntPtr hwnd)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                AutomationElement root;
                using (Request.Activate()) root = AutomationElement.FromHandle(hwnd);
                return Convert(root, null);
            }
            catch (Exception ex) when (attempt < Attempts && ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException
                                           or InvalidOperationException && Native.IsWindow(hwnd))
            {
                Thread.Sleep(300);
            }
            catch (IndexOutOfRangeException ex) when (Native.IsWindow(hwnd) && RetriesTruncatedRead(ex, attempt))
            {
                string note = TruncatedReadNote(attempt, WalkerStep());
                if (Noted is { } noted) noted(note);
                else Console.WriteLine("note  " + note);
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// A tree that Windows cut off while windows under it were destroyed: read it again, within the same budget. Only
    /// the framework's parser of that response qualifies; a persistent truncation or an array error of our own still fails.
    /// </summary>
    /// <remarks>
    /// 2026-10-20: UIAutomationCore stops a bulk read when windows it is walking are destroyed and still returns what it
    /// has: every property row is complete, but the open nodes are never closed, so CacheHelper.ParseTreeDescription
    /// indexes past the end of the description. In CI (run 39339538389) it happened only on the first app start of a fresh
    /// runner, about 300 ms after the room-file picker closed, inside the speaker rows that the file's source rebuilds
    /// (dev: 4 of 10 first starts, 1.2.0: 0 of 10; none in 267 later starts). A local cross-process stress test
    /// (rebuilding those rows during back-to-back reads) cut reads off just as often with WinForms' own providers in
    /// place of the parent-painted text provider, so the app's tree is not malformed. The next read was always complete.
    /// </remarks>
    internal static bool RetriesTruncatedRead(IndexOutOfRangeException error, int attempt) =>
        attempt < Attempts && IsCacheCollectionError(error);

    internal static string TruncatedReadNote(int attempt, string? step) =>
        $"Windows returned a cut-off UI Automation tree{(step != null ? " in " + step : "")}; reading it again ({attempt + 1}/{Attempts})";

    /// <summary>The walker step that read the tree: its method, or the method a wait condition belongs to.</summary>
    private static string? WalkerStep()
    {
        foreach (var frame in new System.Diagnostics.StackTrace(1, false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType is not { } type || type != typeof(Walker) && type.DeclaringType != typeof(Walker)) continue;
            string name = method.Name; // a lambda is named <OpenRoomFile>b__5
            return name.StartsWith('<') && name.IndexOf('>') is > 1 and var end ? name[1..end] : name;
        }
        return null;
    }

    internal static bool IsCacheCollectionError(IndexOutOfRangeException error) =>
        // The runtime's hidden ThrowHelpers method is TargetSite; inspect the actual parser frames instead.
        new System.Diagnostics.StackTrace(error, false).GetFrames().Any(frame => frame.GetMethod() is
            { Name: "ParseTreeDescription", DeclaringType: { FullName: "MS.Internal.Automation.CacheHelper" } });

    internal static Node SnapshotLive(IntPtr hwnd)
    {
        Node Read(AutomationElement element, Node? parent)
        {
            var current = element.Current;
            var node = new Node
            {
                Element = element, Parent = parent, Name = current.Name,
                Type = current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                Rect = ToRectangle(current.BoundingRectangle), Hwnd = new IntPtr(current.NativeWindowHandle),
                Enabled = current.IsEnabled, ClassName = current.ClassName, AutomationId = current.AutomationId,
            };
            for (var child = TreeWalker.RawViewWalker.GetFirstChild(element); child != null;
                 child = TreeWalker.RawViewWalker.GetNextSibling(child)) node.Children.Add(Read(child, node));
            return node;
        }
        return Read(AutomationElement.FromHandle(hwnd), null);
    }

    private static Node Convert(AutomationElement e, Node? parent)
    {
        var node = new Node
        {
            Element = e,
            Name = Cached(e, AutomationElement.NameProperty) as string ?? "",
            Type = (Cached(e, AutomationElement.ControlTypeProperty) as ControlType)?.ProgrammaticName.Replace("ControlType.", "") ?? "?",
            Rect = ToRectangle(Cached(e, AutomationElement.BoundingRectangleProperty)),
            Hwnd = Cached(e, AutomationElement.NativeWindowHandleProperty) is int h ? new IntPtr(h) : IntPtr.Zero,
            Enabled = Cached(e, AutomationElement.IsEnabledProperty) is not false,
            ClassName = Cached(e, AutomationElement.ClassNameProperty) as string ?? "",
            AutomationId = Cached(e, AutomationElement.AutomationIdProperty) as string ?? "",
            Parent = parent,
        };
        AutomationElementCollection? children = null;
        try { children = e.CachedChildren; } catch (InvalidOperationException) { }
        if (children != null)
            foreach (AutomationElement c in children)
                node.Children.Add(Convert(c, node));
        return node;
    }

    private static object? Cached(AutomationElement e, AutomationProperty p)
    {
        try
        {
            var value = e.GetCachedPropertyValue(p, ignoreDefaultValue: false);
            return value == AutomationElement.NotSupported ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Rectangle ToRectangle(object? value)
    {
        if (value is not System.Windows.Rect r || r.IsEmpty || double.IsInfinity(r.Width) || double.IsNaN(r.Width)) return Rectangle.Empty;
        return new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
    }

    /// <summary>
    /// Clicks a control the way an accessibility tool would, without moving the mouse or taking the focus: a control with a
    /// window of its own (every WinForms control) through its MSAA default action, which is exactly what the app's
    /// AccessibleObject.DoDefaultAction does (NavItem selects its page; FluentButton posts its click, so a dialog it opens
    /// doesn't block this call); anything else through UI Automation's Invoke, select or toggle.
    /// </summary>
    public static void Invoke(Node node)
    {
        if (node.Hwnd != IntPtr.Zero && Native.MsaaDoDefaultAction(node.Hwnd)) return;
        var e = node.Element;
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            return;
        }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return;
        }
        throw new InvalidOperationException($"{node.Describe()} can't be invoked");
    }

    /// <summary>Text of the tree, one element per line (saved next to a failure, for finding out what the window had).</summary>
    public static string Dump(Node root)
    {
        var sb = new StringBuilder();
        void Walk(Node n, int depth)
        {
            sb.Append(' ', depth * 2).Append(n.Describe());
            if (n.Hwnd != IntPtr.Zero) sb.Append($" hwnd={n.Hwnd:X}{(Native.IsWindowVisible(n.Hwnd) ? "" : " hidden")}");
            if (n.AutomationId.Length > 0) sb.Append($" id={n.AutomationId}");
            if (!n.Enabled) sb.Append(" disabled");
            sb.AppendLine();
            foreach (var c in n.Children) Walk(c, depth + 1);
        }
        Walk(root, 0);
        return sb.ToString();
    }
}
