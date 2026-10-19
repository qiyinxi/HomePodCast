using HomePodCast.Update;

namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    private string? _guiUpdateFeed;

    private void RunUpdateNotes()
    {
        string current = System.Diagnostics.FileVersionInfo.GetVersionInfo(_exe).ProductVersion ?? "1.2.0";
        using var feed = new ReleaseFeedServer(current);
        _guiUpdateFeed = feed.Url;
        var config = BaseConfig(); config["UpdateChannel"] = "Beta";
        Start(config);
        Navigate("关于");
        Uia.Invoke(Button("检查更新"));
        string title = _t.F("新版本 {0}", "99.0.0-beta.2");
        IntPtr NotesWindow() => Native.TopLevelWindows(App.Id).FirstOrDefault(h => Native.WindowText(h) == title);
        var notes = WaitWindow(NotesWindow, "the release notes after a manual check", 15_000);
        var tree = Uia.Snapshot(notes);
        string fixture = _s.Language == "zh-CN" ? "更新说明测试" : "Release notes fixture";
        string other = _s.Language == "zh-CN" ? "Release notes fixture" : "更新说明测试";
        Expect(Has(tree, fixture), "notes", "the requested language is missing");
        Expect(!Has(tree, other), "notes", "both languages were rendered");
        Expect(Has(tree, "2026-10-20"), "notes", "the publication date is missing");
        Expect(Has(tree, T("更新")) && Has(tree, T("跳过此版本")) && Has(tree, T("稍后")), "notes", "an update action is missing");
        Layout(tree, "notes"); Dump(tree, "notes"); Shoot(notes, "release-notes", "Release notes in one language");
        // A user can close and reopen without fetching the body again.
        Uia.Invoke(Find(tree, n => n.Type == "Button" && n.Says(T("稍后"))) ?? throw new StepFailed("no Later button"));
        WaitUntil(() => NotesWindow() == IntPtr.Zero, "notes window to close", 5_000);
        Uia.Invoke(Button("查看说明"));
        notes = WaitWindow(NotesWindow, "notes reopened from About", 5_000);
        tree = Uia.Snapshot(notes);
        var pane = Find(tree, n => n.Hwnd != IntPtr.Zero && (Native.Style(n.Hwnd) & Native.WS_VSCROLL) != 0);
        Expect(pane != null, "notes", "long release notes have no scroll viewport");
        if (pane != null)
        {
            Scroll(pane.Hwnd, 7 /* SB_BOTTOM */);
            tree = Uia.Snapshot(notes);
            Layout(tree, "notes-bottom"); Dump(tree, "notes-bottom"); Shoot(notes, "release-notes-bottom", "Earlier version and end of release notes");
        }
        Expect(feed.Requests == 1, "notes", $"expected one release-list request, got {feed.Requests}");
        Uia.Invoke(Find(tree, n => n.Type == "Button" && n.Says(T("跳过此版本"))) ?? throw new StepFailed("no Skip button"));
        WaitUntil(() => NotesWindow() == IntPtr.Zero, "skip to close notes", 5_000);
        Quit(); CheckLog(idle: true);
    }
}
