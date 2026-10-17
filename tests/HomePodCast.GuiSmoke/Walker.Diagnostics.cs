namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    /// <summary>All three troubleshooting sections remain reachable; none of their actions is invoked by browsing.</summary>
    private void CheckDiagnostics(Node tree)
    {
        var pane = PagePane(tree, "诊断").Hwnd;
        string[] sections = [T("音画同步测试"), T("日志"), T("诊断报告")];
        string[] actions = [T("开始"), T("打开日志文件夹"), T("导出诊断")];
        var seen = new HashSet<string>();
        var buttons = new HashSet<string>();
        Scroll(pane, Native.SB_TOP);
        try
        {
            for (int i = 0; i < 12; i++)
            {
                // Keep the pane handle: after scrolling, its title may no longer be visible.
                var page = Find(Uia.Snapshot(MainWindow()), node => node.Hwnd == pane)
                    ?? throw new StepFailed("the diagnostics scroll pane disappeared");
                var viewport = page.Rect;
                foreach (string section in sections)
                    if (Find(page, node => node.Type == "Text" && node.Name == section && node.Shown && node.HasSize
                            && viewport.Contains(node.Rect)) != null) seen.Add(section);
                foreach (string action in actions)
                    if (Find(page, node => node.Type == "Button" && node.Name == action && node.Shown && node.HasSize
                            && viewport.Contains(node.Rect)) != null) buttons.Add(action);
                var info = Native.VerticalScroll(pane);
                if ((Native.Style(pane) & Native.WS_VSCROLL) == 0 || info is not { } position || position.nPos + position.nPage > position.nMax) break;
                // Overlap viewports so a section or button split at one edge can be fully visible on the next pass.
                int next = Math.Min(position.nMax - (int)position.nPage + 1,
                    position.nPos + Math.Max(1, (int)position.nPage / 2));
                if (next <= position.nPos) break;
                Scroll(pane, 4 /* SB_THUMBPOSITION */ | next << 16);
                if (Native.VerticalScroll(pane) is { } after && after.nPos == position.nPos) break;
            }
        }
        finally { Scroll(pane, Native.SB_TOP); }
        foreach (string section in sections) Expect(seen.Contains(section), "诊断", $"section was never shown: {section}");
        foreach (string action in actions) Expect(buttons.Contains(action), "诊断", $"action was never shown: {action}");
    }
}
