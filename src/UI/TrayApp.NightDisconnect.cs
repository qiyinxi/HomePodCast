namespace HomePodCast.UI;

// 设置 → 「夜间自动断开」 (NightDisconnectPolicy): inside the night window, once the keyboard and mouse were not touched and
// nothing audible was sent for the set time, the speaker is let go; when the window ends, the stream this ended connects
// again (ConnectTrigger.Automatic, as at startup). Any connect or disconnect in between is the user's, and ends that.
internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _nightTimer = new() { Interval = (int)NightDisconnectPolicy.CheckInterval.TotalMilliseconds };

    /// <summary>At the end of the send chain: when the stream last carried sound.</summary>
    private readonly Audio.SoundActivity _soundActivity = new();

    /// <summary>夜间自动断开 ended the stream (or held back the startup connect), and connects it when the window ends.</summary>
    private bool _nightDisconnected;

    /// <summary>Called once from the constructor, after InitEffects (so the sound is measured after every effect) and before the first connect.</summary>
    private void InitNightDisconnect()
    {
        Controller.Effects.Add(_soundActivity);
        _nightTimer.Tick += (_, _) => CheckNight();
        _nightTimer.Start();
    }

    private void DisposeNightDisconnect() => _nightTimer.Dispose();

    /// <summary>
    /// Disconnects or reconnects when the policy says so (UI thread); true when it did either. <paramref name="startup"/>:
    /// the startup auto-connect is about to run, and is held back in the window when nobody is there (a Windows Update
    /// restart at 3 a.m. would otherwise take the HomePod for the check's 30 seconds).
    /// </summary>
    private bool CheckNight(bool startup = false)
    {
        if (_quitting) return false;
        var sinceInput = Native.SinceLastInput();
        var sinceSound = _soundActivity.SilentFor(Environment.TickCount64);
        var idle = NightDisconnectPolicy.Idle(sinceInput, sinceSound);
        var now = TimeOnly.FromDateTime(DateTime.Now);
        switch (NightDisconnectPolicy.Decide(Config, now, startup || _wantConnected, _nightDisconnected, idle))
        {
            case NightAction.Disconnect:
                Log.Info($"night disconnect: no keyboard or mouse input for {Minutes(sinceInput)}, no sound for {Minutes(sinceSound)}" +
                         $"{(startup ? ": not connecting at startup" : "")}; connecting again at {NightDisconnectPolicy.Format(Config.NightDisconnectUntil)}");
                _wantConnected = false;
                _nightDisconnected = true;
                string status = NightDisconnectedText(Config.NightDisconnectUntil);
                _connection.Post(() => Controller.Stop(status));
                return true;
            case NightAction.Reconnect:
                Log.Info("night disconnect: the night window is over, connecting again");
                _nightDisconnected = false; // once: without a speaker selected, Connect only shows the window
                Connect(ConnectTrigger.Automatic); // a busy speaker is waited for, never taken over
                return true;
            default:
                return false;
        }
    }

    private static string Minutes(TimeSpan? span) => span switch
    {
        null => "?",
        { } s when s == TimeSpan.MaxValue => "this whole run",
        { } s => $"{s.TotalMinutes:F0} min",
    };

    /// <summary>The status line (首页, the flyout, the tray icon's tooltip) after 夜间自动断开.</summary>
    internal static string NightDisconnectedText(TimeOnly until) =>
        L.F("夜间已自动断开，{0} 自动重新连接", NightDisconnectPolicy.Format(until));

    /// <summary>设置 → 「夜间自动断开」 and its window and idle time: saved, and the next check uses them.</summary>
    public void SetNightDisconnect(bool on, TimeOnly from, TimeOnly until, int idleMinutes)
    {
        idleMinutes = NightDisconnectPolicy.ClampIdleMinutes(idleMinutes);
        if (Config.NightDisconnect == on && Config.NightDisconnectFrom == from && Config.NightDisconnectUntil == until &&
            Config.NightDisconnectIdleMinutes == idleMinutes) return;
        Config.NightDisconnect = on;
        Config.NightDisconnectFrom = from;
        Config.NightDisconnectUntil = until;
        Config.NightDisconnectIdleMinutes = idleMinutes;
        Config.Save();
        Log.Info(on
            ? $"night disconnect: on, {NightDisconnectPolicy.Format(from)}-{NightDisconnectPolicy.Format(until)} after {idleMinutes} min idle"
            : "night disconnect: off");
        if (!_nightDisconnected) return;
        // The status line names the reconnect time: a new one, or (turned off) none, since what it ended now stays ended.
        _nightDisconnected = on;
        string status = on ? NightDisconnectedText(until) : L.T("未连接");
        _connection.Post(() => Controller.Stop(status));
    }
}
