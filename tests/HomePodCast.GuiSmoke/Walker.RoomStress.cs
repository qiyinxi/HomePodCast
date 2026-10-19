namespace HomePodCast.GuiSmoke;

/// <summary>
/// TEMPORARY experiment (claude/uia-cache-race), removed before the PR is final: restarts the app and repeats the
/// room-file picker (opened files and a cancelled picker) while reading the main window's UIA tree back to back.
/// </summary>
internal sealed partial class Walker
{
    private int _stressIteration;

    private void RunRoomStress(int minutes)
    {
        var config = GroupOutputsConfig();
        var deadline = DateTime.UtcNow.AddMinutes(minutes);
        for (int iteration = 1; iteration == 1 || DateTime.UtcNow < deadline; iteration++)
        {
            _stressIteration = iteration;
            UiaCacheProbe.SetPhase(iteration, "startup");
            Start(config);
            WaitUntil(() => Find(PagePane(Uia.Snapshot(MainWindow()), "首页"), n => n.Type == "Button" && n.Shown && n.Says(T("刷新")))
                is { Enabled: true }, "startup to settle", 15_000);
            UiaCacheProbe.SetPhase(iteration, "navigate");
            Navigate(GroupPageName);
            SetGroupSource(fileSource: true);
            if (iteration == 1) Shoot(MainWindow(), "room-stress", "group page before the room-file stress");
            bool cancelFirst = iteration % 2 == 0;
            if (cancelFirst) StressCancel(iteration);
            foreach (var (channels, mask, name) in new[] { (6, 0x3fu, "51"), (8, 0x63fu, "71") })
            {
                string path = Path.Combine(_dir, "room-layout-" + name + ".wav");
                if (!File.Exists(path)) WriteRoomWave(path, channels, mask);
                OpenRoomFile(path, dense: true);
            }
            if (!cancelFirst) StressCancel(iteration);
            UiaCacheProbe.SetPhase(iteration, "stop");
            StopApp();
        }
        _report.Note(Where("uia-probe"), $"{_stressIteration} app runs; " + UiaCacheProbe.Summary());
    }

    private void StressCancel(int iteration)
    {
        var main = MainWindow();
        UiaCacheProbe.SetPhase(iteration, "picker-cancel");
        Uia.Invoke(ReachRoomNode(n => n.Type == "Button" && n.Says(T("选择影音文件"))));
        var dialog = WaitWindow(() => Native.TopLevelWindows(App.Id).FirstOrDefault(hwnd => hwnd != main
            && Native.ClassName(hwnd) == "#32770" && Native.IsWindowVisible(hwnd)
            && Native.WindowRect(hwnd) is { Width: > 0, Height: > 0 }), "the room file picker (cancel)", 10_000, handlePrompts: false);
        WaitUntil(() => Find(Uia.Snapshot(dialog), IsRoomFileNameEdit) != null, "the picker filename control (cancel)", 5000,
            handlePrompts: false);
        var cancel = Find(Uia.Snapshot(dialog), n => n.Type == "Button" && n.AutomationId == "2")
            ?? throw new StepFailed("speaker room: file picker has no Cancel action");
        Uia.Invoke(cancel);
        WaitUntil(() => !Native.IsWindowVisible(dialog), "the cancelled picker to close", 10_000, handlePrompts: false);
        UiaCacheProbe.SetPhase(iteration, "after-cancel");
        Dense(2500);
        UiaCacheProbe.SetPhase(iteration, "settled");
    }

    /// <summary>Reads the main window's tree back to back (each read still has its own retry budget).</summary>
    private void Dense(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            try { Uia.Snapshot(MainWindow()); }
            catch (Exception ex) when (ex is not StepFailed) { UiaCacheProbe.ExhaustedBudget(ex); }
        }
    }
}
