using HomePodCast.Net;

namespace HomePodCast.UI;

// A speaker another device (an iPhone, a Mac) is playing to is never taken over without asking (TakeoverPolicy): the 连接
// click reads the speakers' flag first and asks; every other connect is checked in the controller and waits while the
// speaker is busy. Our own last session is remembered in the config (OwnSession), so a restart after a crash knows the
// flag may still be ours.
internal sealed partial class TrayApp
{
    /// <summary>While streaming, how often the session's "still alive" time is saved (what a crash leaves behind).</summary>
    private const int OwnSessionSaveMs = 60_000;

    private System.Windows.Forms.Timer? _ownSessionTimer;
    private bool _ownSessionLive;
    private bool _checkingClick;

    /// <summary>Called once from the constructor (UI thread), before the first connect.</summary>
    private void InitTakeover()
    {
        Controller.BusyReader = new InfoBusyReader(SpeakerInfo);
        Controller.OwnSession.Restore(Config.LastOwnSession);
        if (Config.LastOwnSession is { } last)
            Log.Info($"last session: {last.Key}, {(last.Clean ? "ended by us" : "last alive")} {last.AliveUtc:u}");
        _ownSessionTimer = new System.Windows.Forms.Timer { Interval = OwnSessionSaveMs };
        _ownSessionTimer.Tick += (_, _) =>
        {
            if (Controller.OwnSession.Live) SaveOwnSession();
        };
        _ownSessionTimer.Start();
        Controller.Changed += () => _ui.Post(_ =>
        {
            if (!_quitting && Controller.OwnSession.Live != _ownSessionLive) SaveOwnSession(); // a session began or ended
        }, null);
    }

    private void SaveOwnSession()
    {
        _ownSessionLive = Controller.OwnSession.Live;
        var record = Controller.OwnSession.Record(DateTime.UtcNow);
        if (record == Config.LastOwnSession) return;
        Config.LastOwnSession = record;
        Config.Save();
    }

    /// <summary>The PC goes to sleep (any thread): our session ends now as far as the speaker's flag is concerned.</summary>
    private void OnSuspending()
    {
        Controller.OwnSession.Suspending(DateTime.UtcNow);
        _ui.Post(_ => { if (!_quitting) SaveOwnSession(); }, null);
    }

    /// <summary>Quit: the controller has stopped (TEARDOWN sent), which is saved as a clean end.</summary>
    private void DisposeTakeover()
    {
        _ownSessionTimer?.Dispose();
        try { SaveOwnSession(); }
        catch (Exception ex) { Log.Warn($"saving the last session: {ex.Message}"); }
    }

    /// <summary>
    /// The user clicked 连接: read every selected speaker's flag (off the UI thread, a 2-second timeout each) and, when one
    /// is busy with another sender's session, ask before taking it over. 取消 leaves everything as it was (an automatic
    /// connect that waits for the speaker keeps waiting).
    /// </summary>
    private async void ConnectManual(IWin32Window? owner)
    {
        if (Config.DeviceId == null)
        {
            ShowMain();
            return;
        }
        if (_checkingClick) return; // one click is enough
        _checkingClick = true;
        try
        {
            bool ours = Controller.OwnSession.IsRecent(OwnSession.KeyOf(Config), DateTime.UtcNow);
            var targets = SelectedSpeakers().Select(s => new BusyTarget(s.DeviceId, s.Name, s.Address)).ToList();
            var reading = await Controller.BusyReader!.ReadAsync(targets, CancellationToken.None);
            if (_quitting) return;
            if (TakeoverPolicy.Decide(ConnectTrigger.Manual, reading.State, ours) != TakeoverAction.Ask)
            {
                Connect(ConnectTrigger.Manual); // the controller checks once more right before connecting
                return;
            }
            Log.Info($"takeover check (click): {reading.Detail}; another sender's session: asking");
            bool confirmed = TakeoverDialog.Confirm(owner ?? (_form.Visible ? _form : null), string.Join(" / ", reading.BusyNames));
            if (_quitting) return;
            if (!confirmed)
            {
                Log.Info("takeover: the user cancelled; nothing changes");
                return;
            }
            if (Controller.State == StreamState.Streaming)
            {
                Log.Info("takeover: connected meanwhile (the speaker became free)");
                return;
            }
            Log.Info("takeover: the user chose to connect anyway");
            Connect(ConnectTrigger.TakeOver);
        }
        catch (Exception ex)
        {
            Log.Warn($"takeover check (click): {ex.Message}");
            Connect(ConnectTrigger.Manual);
        }
        finally
        {
            _checkingClick = false;
        }
    }
}
