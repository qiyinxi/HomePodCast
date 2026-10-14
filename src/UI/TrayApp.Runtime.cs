using HomePodCast.Update;

namespace HomePodCast.UI;

// A .NET update replaced the runtime this copy started on while it ran (RuntimeWatch): from then on, whatever it uses
// for the first time can fail, an update check (2026-10-13) as much as the next stream. 首页 says so with 「立即重启」, and
// the tray says it once. Nothing restarts without that click; the restart brings the app back as it was (AppRestart).
// A self-contained copy (the Microsoft Store package) has no shared runtime: nothing is watched, nothing is shown.
internal sealed partial class TrayApp
{
    private RuntimeWatch? _runtimeWatch;

    /// <summary>The runtime this copy started on is gone: 首页 shows the restart notice.</summary>
    public bool RuntimeRemoved { get; private set; }

    /// <summary>Called once from the constructor, after InitUpdate (the balloon's click handler).</summary>
    /// <param name="resume">This copy was started by 「立即重启」: what it does with the speaker (for the log).</param>
    private void InitRuntimeWatch(bool? resume)
    {
        if (resume is { } connect)
            Log.Info($"restarted by 「立即重启」: {(connect ? "connecting again, as before" : "not connecting: it was not connected")}");
        _runtimeWatch = RuntimeWatch.For(AppEnvironment.RuntimeFolder, File.Exists, () => _ui.Post(_ => OnRuntimeRemoved(), null));
        _runtimeWatch?.Start();
    }

    private void OnRuntimeRemoved()
    {
        if (_quitting || RuntimeRemoved) return;
        RuntimeRemoved = true;
        _form.ShowRuntimeNotice();
        ShowBalloon(L.T(".NET 已更新，需要重新启动 HomePodCast。点这里查看。"), ToolTipIcon.Warning, () => ShowMain(AppPage.Home));
    }

    /// <summary>
    /// 首页's 「立即重启」: quits the way 退出 does (the stream stopped with a TEARDOWN, per-app volumes and players restored,
    /// settings saved), and Program.RunGui starts the new copy: into the tray if the window was hidden, connecting if a
    /// connection was wanted (also one that 夜间自动断开 let go: the click shows someone is there).
    /// </summary>
    public void RestartForRuntime()
    {
        if (_quitting) return;
        var restart = new AppRestart.Request("because a .NET update removed the runtime this copy started on",
            Tray: !_form.Visible, Connected: _wantConnected || _nightDisconnected);
        Log.Info($"runtime: 「立即重启」 ({(restart.Tray ? "into the tray" : "with the window")}, " +
                 $"{(restart.Connected == true ? "connecting again" : "not connected")}, {Controller.State})");
        AppRestart.Requested = restart;
        try
        {
            Quit();
        }
        catch (Exception ex)
        {
            // Quitting runs code this copy never used before, which may need what the update removed: whatever failed,
            // leave, so that the new copy starts.
            Log.Error($"runtime: quitting for the restart failed: {UpdateFlow.Describe(ex)}");
            ExitThread();
        }
    }
}
