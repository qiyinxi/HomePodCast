using HomePodCast.Net;

namespace HomePodCast.UI;

// A speaker another device (an iPhone, a Mac) is playing to is never taken over without asking (TakeoverPolicy): the 连接
// click reads the speakers' flag first and asks; every other connect is checked in the controller and waits while the
// speaker is busy. Our own last session is remembered in the config (OwnSession), so a restart after a crash knows the
// flag may still be ours. A HomePod held by its own home-theater Apple TV (Net/HomeTheater.cs) is taken over by every
// connect while 设置 → 「HomePod 当 Apple TV 家庭影院音箱时」 says 临时接管 (the default); with 先询问 it is asked about
// like any other sender's session, naming the Apple TV.
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
        // The last scan (TXT records) tells which busy HomePod its home-theater Apple TV holds.
        Controller.BusyReader = new InfoBusyReader(SpeakerInfo, () => LastScan);
        Controller.HomeTheaterMode = Config.HomeTheaterTakeover;
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

    private void SaveOwnSession() => SaveOwnSession(Controller);

    private void SaveOwnSession(StreamController controller)
    {
        _ownSessionLive = controller.OwnSession.Live;
        var record = controller.OwnSession.Record(DateTime.UtcNow);
        if (record == Config.LastOwnSession) return;
        Config.LastOwnSession = record;
        Config.TrySave();
    }

    /// <summary>
    /// A controller the mixer made for a destination (the startup one is watched in InitTakeover): its session to the
    /// selected speaker is saved as soon as it begins or ends, read from this controller, since Controller may be the idle
    /// startup one again by the time the post runs. 2026-10-27: such sessions were saved only by the minute's timer, and
    /// their end never, so a crash in their first minute left the next start taking our own session for another sender's.
    /// </summary>
    private void WatchOwnSession(StreamController controller)
    {
        bool live = false;
        controller.Changed += () => _ui.Post(_ =>
        {
            if (_quitting || controller.OwnSession.Live == live) return;
            live = !live;
            // Only the selected speaker's: what the next start's controller restores. Forgotten (another sender took the
            // speaker over): a record saved for that speaker goes.
            string? selection = OwnSession.KeyOf(Config);
            if (selection != null && (controller.OwnSession.Record(DateTime.UtcNow)?.Key ?? Config.LastOwnSession?.Key) == selection)
                SaveOwnSession(controller);
        }, null);
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

    /// <summary>设置 → 「HomePod 当 Apple TV 家庭影院音箱时」 changed: saved, and the next check uses it.</summary>
    public void SetHomeTheaterMode(HomeTheaterMode mode)
    {
        if (Config.HomeTheaterTakeover == mode) return;
        Config.HomeTheaterTakeover = mode;
        Config.Save();
        Controller.HomeTheaterMode = mode;
        Log.Info($"home theater: {(mode == HomeTheaterMode.TakeOver ? "take the HomePod over temporarily" : "ask first")}");
        Controller.RecheckSpeaker(); // a connect waiting for a home theater's HomePod decides again now
    }

    /// <summary>
    /// The user clicked 连接: read every selected speaker's flag (off the UI thread, a 2-second timeout each) and, when one
    /// is busy with another sender's session, ask before taking it over. 取消 leaves everything as it was (an automatic
    /// connect that waits for the speaker keeps waiting). A HomePod held by its home-theater Apple TV is taken over without
    /// asking while the setting says 临时接管.
    /// </summary>
    private async void ConnectManual(IWin32Window? owner)
    {
        if (Mixer != null) { await ConnectMixerManually(Mixer.SelectedKey, owner); return; }
        if (!HasOutputSelection)
        {
            ShowMain();
            return;
        }
        if (_checkingClick) return; // one click is enough
        _checkingClick = true;
        try
        {
            bool ours = Controller.OwnSession.IsRecent(OwnSession.KeyOf(Config), DateTime.UtcNow);
            var plan = GroupPlan.FromConfig(Config);
            var targets = SelectedSpeakers().Where(s => CheckGroupMemberBeforeManualConnect(plan, s.DeviceId))
                .Select(s => new BusyTarget(s.DeviceId, s.Name, s.Address, s.Port)).ToList();
            var reading = await Controller.BusyReader!.ReadAsync(targets, CancellationToken.None);
            if (_quitting) return;
            var mode = Config.HomeTheaterTakeover;
            if (TakeoverPolicy.Decide(ConnectTrigger.Manual, reading.State, ours, reading.Holder, mode) != TakeoverAction.Ask)
            {
                Connect(ConnectTrigger.Manual); // the controller checks once more right before connecting (and takes a home theater over)
                return;
            }
            var appleTv = reading.Holder == BusyHolder.HomeTheater ? reading.HomeTheater : null;
            Log.Info($"takeover check (click): {reading.Detail}; " +
                     (appleTv != null ? "held by its home-theater Apple TV (setting: ask first)" : "another sender's session") + ": asking");
            bool confirmed = TakeoverDialog.Confirm(owner ?? (_form.Visible ? _form : null), string.Join(" / ", reading.BusyNames), appleTv?.Name);
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
            Connect(ConnectTrigger.TakeOver, appleTv?.Name);
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

    internal async Task ConnectMixerManually(string? key, IWin32Window? owner)
    {
        if (Mixer == null || _checkingClick) return;
        var destination = key == null ? MixerDestinationResolver.Resolve(Config,
            MixerRoutes.Read(Config).Settings?.Default ?? MixerRoute.Current, LastScan) : Mixer.DestinationForKey(key);
        if (destination?.Config is not { } config) { ShowMain(); return; }
        key = destination.Key;
        var controller = Mixer.ControllerForKey(key) ?? _legacyController!;
        _checkingClick = true;
        try
        {
            var targets = SpeakerSelection.Of(config, LastScan)
                .Select(s => new BusyTarget(s.DeviceId, s.Name, s.Address, s.Port)).ToList();
            var reader = controller.BusyReader ?? new InfoBusyReader(SpeakerInfo, () => LastScan);
            var reading = await reader.ReadAsync(targets, CancellationToken.None);
            if (_quitting) return;
            var trigger = ConnectTrigger.Manual;
            bool ours = controller.OwnSession.IsRecent(OwnSession.KeyOf(config), DateTime.UtcNow);
            if (TakeoverPolicy.Decide(trigger, reading.State, ours, reading.Holder, Config.HomeTheaterTakeover) == TakeoverAction.Ask)
            {
                if (!TakeoverDialog.Confirm(owner, string.Join(" / ", reading.BusyNames), reading.HomeTheater?.Name) || _quitting) return;
                trigger = ConnectTrigger.TakeOver;
            }
            if (Mixer.DestinationForKey(key) != null) await Mixer.ReconnectAsync(key, trigger);
            else if (MixerDestinationResolver.Resolve(Config, MixerRoutes.Read(Config).Settings?.Default ?? MixerRoute.Current, LastScan).Key == key)
            {
                _wantConnected = true; _nightDisconnected = false;
                await Mixer.ConnectAsync(Config, trigger, key);
            }
        }
        catch (Exception ex) { Log.Warn($"mixer manual connect {key}: {ex.Message}"); }
        finally { _checkingClick = false; }
    }
}
