namespace HomePodCast.GuiSmoke;

/// <summary>
/// Layout checks that need no knowledge of a page, on a window's UI Automation tree (every WinForms control is a window
/// of its own, so the tree is the real control hierarchy):
/// a control sticking out of its container (clipped: cut-off text or buttons; inside a scrolling page only sideways),
/// two sibling controls on top of each other, a button or navigation item without a name, a text squeezed to nothing,
/// and, in Latin-script languages, Chinese characters (a string that was never translated).
/// </summary>
internal static class Checks
{
    private const int Tolerance = 1;

    /// <param name="where">What is checked ("de-150 / 设置"), for the messages.</param>
    /// <param name="userData">Texts that come from the config or the speaker (names), not from the app's tables.</param>
    public static void Layout(Node root, string where, Texts texts, Report report, IReadOnlyCollection<string> userData)
    {
        var shown = root.Descendants().Where(n => n.Shown && !n.InChrome).ToList();

        foreach (var n in shown)
        {
            if (!n.HasSize || n.Parent is not { HasSize: true } parent || parent.InChrome) continue;
            var p = parent.Rect;
            var r = n.Rect;
            bool scrolls = parent.Hwnd != IntPtr.Zero && (Native.Style(parent.Hwnd) & Native.WS_VSCROLL) != 0;
            int left = p.Left - r.Left, right = r.Right - p.Right, top = p.Top - r.Top, bottom = r.Bottom - p.Bottom;
            var sides = new List<string>();
            if (left > Tolerance) sides.Add($"{left}px left");
            if (right > Tolerance) sides.Add($"{right}px right");
            if (!scrolls && top > Tolerance) sides.Add($"{top}px above");
            if (!scrolls && bottom > Tolerance) sides.Add($"{bottom}px below");
            if (sides.Count > 0)
                report.Fail(where, $"{n.Describe()} sticks out of {parent.Describe()} by {string.Join(", ", sides)} (clipped)");
        }

        foreach (var group in shown.Where(n => n.HasSize && n.Parent != null).GroupBy(n => n.Parent!))
        {
            var siblings = group.ToList();
            for (int i = 0; i < siblings.Count; i++)
            for (int j = i + 1; j < siblings.Count; j++)
            {
                var overlap = Rectangle.Intersect(siblings[i].Rect, siblings[j].Rect);
                if (overlap.Width > 2 && overlap.Height > 2)
                    report.Fail(where, $"{siblings[i].Describe()} and {siblings[j].Describe()} overlap ({overlap.Width}x{overlap.Height}px)");
            }
        }

        foreach (var n in shown)
        {
            if (n.Type is "Button" or "Hyperlink" or "TabItem" or "MenuItem" && string.IsNullOrWhiteSpace(n.Name))
                report.Fail(where, $"{n.Describe()} has no text or accessible name");
            else if (n.Type is "Slider" or "ComboBox" or "CheckBox" or "Edit" or "List" or "RadioButton" && string.IsNullOrWhiteSpace(n.Name))
                report.Warn(where, $"{n.Describe()} has no accessible name (a screen reader announces it without a label)");

            if (n.Name.Trim().Length > 0 && n.Type is "Text" or "Button" or "Hyperlink" or "TabItem" && n.Rect != Rectangle.Empty
                && (n.Rect.Width <= 1) != (n.Rect.Height <= 1))
                report.Fail(where, $"{n.Describe()} is squeezed to nothing");

            if (!texts.Cjk && Texts.HasCjk(n.Name) && !AllowedCjk(n.Name, userData))
                report.Fail(where, $"untranslated text in {texts.Language}: {n.Describe()}");
        }
    }

    private static bool AllowedCjk(string name, IReadOnlyCollection<string> userData)
    {
        var rest = name;
        foreach (var allowed in Texts.NativeLanguageNames.Concat(userData)) rest = rest.Replace(allowed, "");
        return !Texts.HasCjk(rest);
    }

    /// <summary>The window itself inside the screen's working area (a dialog or flyout pushed off-screen).</summary>
    public static void OnScreen(IntPtr hwnd, string where, Report report)
    {
        var bounds = Native.VisibleBounds(hwnd);
        var screen = Screen.FromHandle(hwnd).Bounds;
        if (!screen.Contains(bounds))
            report.Fail(where, $"window [{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}] is not inside the screen [{screen.Width}x{screen.Height}]");
    }
}
