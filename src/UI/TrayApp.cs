using System.Net.NetworkInformation;
using HomePodCast.Net;
using Microsoft.Win32;

namespace HomePodCast.UI;

internal sealed partial class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly MainWindow _form;
    private StreamState _lastIconState = (StreamState)(-1);
    private LocalApi? _api;
    private bool _wantConnected;
    private bool _hintShown;
    private TrayFlyout? _flyout;
    private long _flyoutClosedAt;

    public AppConfig Config { get; }
    public StreamController Controller { get; }
    public Audio.AppRouting Routing { get; private set; } = null!;

    /// <summary>Extra inputs (e.g. a microphone) mixed into the stream; see <see cref="Audio.IMixSource"/>.</summary>
    public Audio.MixSources MixSources { get; } = new();

    /// <param name="openFlyout">Open the tray flyout once running (<c>gui --flyout</c>, for testing its look).</param>
    /// <param name="testOsd">Show the volume OSD once running (<c>gui --osd</c>, "muted" for <c>--osd-muted</c>; for testing its look).</param>
    /// <param name="updatedFrom">The version before an update (<c>--updated-from=</c>, passed by the update helper).</param>
    /// <param name="flyoutSignal">Set by a second <c>gui --flyout</c> (a test switch): open the flyout in this running instance.</param>
    public TrayApp(bool startHidden, EventWaitHandle showSignal, bool openMixer = false, bool openFlyout = false, string? testOsd = null,
        string? updatedFrom = null, EventWaitHandle? flyoutSignal = null)
    {
        Config = AppConfig.Load();
        Controller = new StreamController(Config.FifoTargetMs);
        SetUpRouting();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        InitSound();
        InitEffects();

        // Left click: the main window. Right click: the flyout (volume, scene, night mode, mic, connect, quit).
        _tray = new NotifyIcon { Visible = true, Text = L.T("HomePod 音响") };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMain();
            else if (e.Button == MouseButtons.Right) ShowFlyout();
        };

        _form = new MainWindow(this);
        _ = _form.Handle; // create handle so BeginInvoke works before first show
        _form.LatencyChanged += RaiseStateChanged;
        InitPlayers();
        InitCapture();

        Controller.Changed += () => _ui.Post(_ => OnControllerChanged(), null);
        Controller.HostResolved += host => _ui.Post(_ =>
        {
            if (Config.Host != host) { Config.Host = host; Config.Save(); }
        }, null);
        InitFirewall();
        InitUpdate(updatedFrom);
        try
        {
            _api = new LocalApi(Config.LocalApiPort, () => ApiStatus(Controller.State == StreamState.Streaming, Config.DeviceName,
                Controller.EffectiveLatencyMs, VideoDelayMs, HasCurrentCalibration));
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Warn($"local API not started (port {Config.LocalApiPort}): {ex.Message}");
        }
        Controller.ArrivalToRenderMs = Config.ArrivalToRenderMs;
        Controller.ArrivalToRenderChanged += ms => _ui.Post(_ => { Config.ArrivalToRenderMs = ms; Config.Save(); }, null);
        InitTakeover(); // before the first connect: a busy speaker is checked (TrayApp.Takeover.cs)
        InitPairing(); // before the first connect: an Apple TV's stored pairing is used (TrayApp.Pairing.cs)
        InitNightDisconnect(); // after InitEffects, before the first connect (TrayApp.NightDisconnect.cs)

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.Post(_ =>
        {
            KickIfWanted();
            OnNetworkAddressChanged(); // a new network may be Public (TrayApp.Firewall.cs)
        }, null);

        // Bring the existing window forward when a second instance is launched (its flyout for a second gui --flyout).
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => _ui.Post(_ => ShowMain(), null), null, -1, false);
        if (flyoutSignal != null)
            ThreadPool.RegisterWaitForSingleObject(flyoutSignal, (_, _) => _ui.Post(_ => { if (!_quitting) ShowFlyout(); }, null), null, -1, false);

        OnControllerChanged();
        if (!startHidden) ShowMain();
        if (openMixer)
        {
            // Wait for Application.Run's loop: a Post would also be dispatched by any COM wait that pumps messages
            // before it, and a form shown in there never gets its Shown/timer messages.
            void OpenMixer(object? s, EventArgs e) { Application.Idle -= OpenMixer; ShowMixer(); }
            Application.Idle += OpenMixer;
        }
        if (openFlyout)
        {
            void OpenFlyout(object? s, EventArgs e) { Application.Idle -= OpenFlyout; ShowFlyout(); }
            Application.Idle += OpenFlyout;
        }
        if (testOsd != null)
        {
            void OpenOsd(object? s, EventArgs e) { Application.Idle -= OpenOsd; ShowTestOsd(testOsd == "muted"); }
            Application.Idle += OpenOsd;
        }

        // The firewall rules and the speaker's network are read on the thread pool (COM): no COM wait pumps messages
        // in this constructor for them. A message, if one is needed, is posted to the UI thread.
        CheckFirewall(FirewallTrigger.Startup);
        if (Config.DeviceId != null)
        {
            _form.SetDevices([new AirPlayDevice(Config.DeviceName ?? "?", Config.DeviceId,
                System.Net.IPAddress.TryParse(Config.Host, out var ip) ? ip : System.Net.IPAddress.None, 7000, "", new Dictionary<string, string>())]);
            // In the night window with nobody at the PC (a restart for Windows Update), 夜间自动断开 connects at its end instead.
            if (Config.AutoConnect && !CheckNight(startup: true)) Connect(ConnectTrigger.Automatic);
        }
        RefreshDevices();
    }

    /// <summary>
    /// Something the tray flyout shows changed: connection, volume, mute, scene or latency, night mode, mic, cap.
    /// Raised on the UI thread.
    /// </summary>
    public event Action? StateChanged;

    private void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>The latency the scene or the slider asks for now (what 首页 shows), in ms.</summary>
    public int LatencyMs => _form.LatencyMs;

    /// <summary>Per-app routing: the capture follows the mixer's HomePod / 本机 / 两者 rules.</summary>
    private void SetUpRouting()
    {
        Routing = new Audio.AppRouting(Config);
        Controller.CaptureFactory = fifo => new Audio.RoutedCapture(fifo, RtpSender.SampleRate, Routing, MixSources);
        // Apps left silent by a run that did not exit cleanly. Not from a test profile: the real instance may be
        // running next to it, and its silenced apps look exactly like leftovers.
        if (AppConfig.Profile == null) Task.Run(Audio.SessionRouter.RestoreLeftovers);
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Connect and Disconnect run here, never on the UI thread: Start first stops the previous loop (up to 3 s, plus
    /// the speaker's TEARDOWN and the capture's shutdown). One at a time, and the last one asked for wins.
    /// </summary>
    private readonly LatestRequestQueue _connection = new("connection");

    /// <param name="trigger">Why: what a speaker busy with another sender means for this connection (TakeoverPolicy).</param>
    /// <param name="takenFrom">仍然连接 on a HomePod its home-theater Apple TV held: that Apple TV's name (for the 已连接 line).</param>
    public void Connect(ConnectTrigger trigger, string? takenFrom = null)
    {
        if (Config.DeviceId == null)
        {
            ShowMain();
            return;
        }
        _wantConnected = true;
        _nightDisconnected = false;
        // The settings are taken now; a volume set before the queued start runs is kept (volumeAsOf).
        string id = Config.DeviceId;
        string? host = Config.Host;
        int latency = Config.LatencyMs;
        double? volume = Config.Volume;
        int asOf = Controller.VolumeChanges;
        if (GroupPlan.FromConfig(Config) is { } group) // stereo pair / multi-room (experimental)
        {
            _connection.Post(() => Controller.StartGroup(group, latency, volume, asOf, trigger, takenFrom));
            return;
        }
        _connection.Post(() => Controller.Start(id, host, latency, volume, asOf, trigger, takenFrom));
    }

    public void Disconnect()
    {
        _wantConnected = false;
        _nightDisconnected = false; // the user's own disconnect: 夜间自动断开 doesn't connect it again
        _connection.Post(Controller.Stop);
    }

    /// <summary>连接 / 断开 (首页, the tray flyout). 连接 asks first when another device is playing to the speaker.</summary>
    public void ToggleConnection(IWin32Window? owner = null)
    {
        if (ShowsConnect(Controller.State)) ConnectManual(owner);
        else Disconnect();
    }

    /// <summary>
    /// The button says 连接 while nothing is connected: idle, and also waiting to retry (「找不到音箱…，30 秒后重试」), where
    /// 断开 read as if something were connected (2026-10-12). There a click connects at once instead of waiting.
    /// </summary>
    public static bool ShowsConnect(StreamState state) => state is StreamState.Idle or StreamState.Retrying;

    public void SelectDevice(AirPlayDevice device)
    {
        if (StreamController.Normalize(device.DeviceId) == StreamController.Normalize(Config.DeviceId ?? "")) return;
        Config.DeviceId = device.DeviceId;
        Config.DeviceName = device.Name;
        Config.Host = device.Address.ToString();
        Config.Volume = null; // use the new speaker's own volume
        Config.Save();
        if (_wantConnected) Connect(ConnectTrigger.Automatic); // another speaker: never taken over without a click
    }

    public void SetLatency(int ms)
    {
        if (Config.Scene == Scene.Custom) Config.CustomLatencyMs = ms;
        if (Config.LatencyMs == ms) return;
        Config.LatencyMs = ms;
        Config.Save();
        if (_wantConnected) Connect(ConnectTrigger.Reconnect); // latency is negotiated at SETUP, so reconnect
    }

    /// <summary>A volume chosen in the app (a page slider's debounce, the tray flyout): saved, sent, and Windows follows (跟随 Windows).</summary>
    public void SetVolume(double percent)
    {
        percent = VolumeLimit.Clamp(percent, Config.VolumeCapPercent);
        Config.Volume = percent;
        Config.Save();
        if (Controller.Volume != percent || Controller.Muted) Controller.SetVolume(percent); // PreviewVolume may have sent it
        _followLink?.HomePodChanged();
    }

    /// <summary>
    /// A volume slider is moving: send it right away (the controller coalesces — latest value wins, one request
    /// in flight), so the speaker follows the drag. The slider's debounce then saves it via SetVolume/ApplyVolume.
    /// </summary>
    public void PreviewVolume(double percent)
    {
        Controller.SetVolume(VolumeLimit.Clamp(percent, Config.VolumeCapPercent));
        _followLink?.HomePodChanged();
    }

    /// <summary>A volume chosen in the tray flyout (after its debounce): applied like the pages do, and they follow.</summary>
    public void ApplyVolume(double percent)
    {
        SetVolume(percent);
        _form.ShowVolume(Controller.Volume ?? percent);
        _form.ShowSoundOptions(); // a new volume also ends a mute
        RaiseStateChanged();
    }

    public async void RefreshDevices()
    {
        _form.SetScanning(true);
        try
        {
            var found = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3));
            LastScan = found; // TXT records and pair members for 设置 → 音箱信息, home theaters for the busy check
            Controller.RecheckSpeaker(); // a connect waiting for a HomePod may now see that its home-theater Apple TV holds it
            var scan = found;
            found = StereoPairs.Merge(found); // a stereo pair shows as one entry (experimental)
            // Keep the configured speaker in the list even if it didn't answer this time.
            if (Config.DeviceId != null && !found.Any(d =>
                    StreamController.Normalize(d.DeviceId).Equals(StreamController.Normalize(Config.DeviceId), StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new AirPlayDevice(Config.DeviceName ?? "?", Config.DeviceId, System.Net.IPAddress.None, 7000,
                    L.T("未发现"), new Dictionary<string, string>()));
            }
            _form.SetDevices(found);
            if (Config.DeviceId == null && FirstHomePod(found, scan) is { } homepod)
            {
                SelectDevice(homepod);
                // A home theater's HomePod: taken over (临时接管), or the status names its Apple TV at once (先询问).
                if (HomeTheater.HolderOf(homepod, scan) is { } tv) Log.Info($"first speaker: {Log.Name(homepod.Name)}, held by its home-theater Apple TV {Log.Name(tv.Name)}");
                if (Config.AutoConnect) Connect(ConnectTrigger.Automatic);
                _form.SetDevices(found);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"scan: {ex.Message}");
        }
        finally
        {
            _form.SetScanning(false);
        }
    }

    /// <summary>
    /// The speaker a first start selects: a HomePod, one that is not an Apple TV's home-theater speaker if there is one
    /// (connecting to that one takes it from the Apple TV); null when no HomePod was found.
    /// </summary>
    internal static AirPlayDevice? FirstHomePod(IReadOnlyList<AirPlayDevice> list, IReadOnlyList<AirPlayDevice> scan)
    {
        var homepods = list.Where(d => d.Model.StartsWith("AudioAccessory", StringComparison.Ordinal)).ToList();
        return homepods.FirstOrDefault(d => HomeTheater.OwnerOf(d, scan) == null) ?? homepods.FirstOrDefault();
    }

    public void RunSyncTest(IWin32Window owner)
    {
        if (Controller.State != StreamState.Streaming)
        {
            MessageBox.Show(owner, L.T("请先连接音箱，再做同步测试。"), L.T("音画同步测试"));
            return;
        }
        if (!TryGetCalibrationSnapshot(out var snapshot))
        {
            MessageBox.Show(owner, L.T("无法播放测试音，请检查所选音源后重新测试。"), L.T("音画同步测试"));
            return;
        }
        using var testRoute = Routing.BeginSyncTestRoute();
        int predicted = LatencyTuner.SoundLagMs(snapshot.EffectiveLatencyMs, Config, snapshot.CaptureExtraMs,
            snapshot.FifoTargetMs, snapshot.Context);
        var expectedSender = Controller.ActiveSender;
        using var test = new SyncTestForm(predicted, snapshot.Context.CaptureEndpointId, HasCurrentCalibration,
            () => IsCalibrationSnapshotCurrent(snapshot) && ReferenceEquals(Controller.ActiveSender, expectedSender));

        // Probe our own share of the latency: Windows mix -> packet leaving the PC.
        var sender = Controller.ActiveSender;
        var scheduled = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var local = new List<double>();
        test.ClickScheduled += when => scheduled.Enqueue(when);
        if (sender != null)
        {
            sender.OnsetSent = sent =>
            {
                while (scheduled.TryPeek(out var w) && sent - w > MediaClock.FromMs(400)) scheduled.TryDequeue(out _);
                if (scheduled.TryDequeue(out var when) && sent >= when)
                {
                    double ms = MediaClock.ToMs(sent - when);
                    lock (local) local.Add(ms);
                    Log.Info($"probe: click left the PC {ms:F1} ms after entering the Windows mix " +
                             $"(+{sender.LatencyFrames * 1000.0 / RtpSender.SampleRate:F0} ms requested playout delay)");
                }
            };
        }

        DialogResult result;
        try { result = test.ShowDialog(owner); }
        finally { if (sender != null) sender.OnsetSent = null; }
        double[] localTimes;
        lock (local) localTimes = local.ToArray();
        if (localTimes.Length > 0)
            Log.Info($"probe summary: local pipeline median {localTimes.Order().ElementAt(localTimes.Length / 2):F1} ms over {localTimes.Length} clicks");
        if (result == DialogResult.Retry && test.RequestedAction == SyncTestAction.Reset)
        {
            ResetSyncCalibration(snapshot.Context);
        }
        else if (result == DialogResult.OK)
        {
            bool current = test.CanUseMeasurement && IsCalibrationSnapshotCurrent(snapshot);
            if (current)
            {
                Config.MeasuredAvOffsetMs = test.OffsetMs;
                Config.Save();
                Log.Info($"sync test: audio lags video by ~{test.OffsetMs} ms at latency {Config.LatencyMs} ms");
            }
            if (test.RequestedAction == SyncTestAction.Apply)
            {
                // Say what actually happened: the path may have changed after the click, or the click never reached the stream.
                var outcome = !current ? SyncCalibrationOutcome.PathChanged
                    : localTimes.Length == 0 ? SyncCalibrationOutcome.NoTestSound
                    : ApplySyncCalibration(snapshot, test.OffsetMs);
                if (outcome != SyncCalibrationOutcome.Applied) Log.Warn($"sync calibration not saved: {outcome}");
                if (SyncCalibrationMessage(outcome) is { } message) MessageBox.Show(owner, message, L.T("音画同步测试"));
            }
        }
    }

    public void ShowMixer() => ShowMain(AppPage.Mixer);

    /// <summary>Bring the window forward, on <paramref name="page"/> if given (else the page it was on).</summary>
    public void ShowMain(AppPage? page = null)
    {
        if (page is { } p) _form.ShowPage(p);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    /// <summary>Open the flyout next to the tray icon; a second right-click closes it.</summary>
    public void ShowFlyout()
    {
        if (_flyout is { IsDisposed: false } open)
        {
            open.Close();
            return;
        }
        // Clicking the icon while the flyout is open first takes the focus away, which already closed it.
        if (Environment.TickCount64 - _flyoutClosedAt < 400) return;
        try
        {
            var anchor = FlyoutPlacement.IconRect(_tray) ?? new Rectangle(Cursor.Position, new Size(1, 1));
            var flyout = new TrayFlyout(this);
            flyout.FormClosed += (_, _) =>
            {
                _flyout = null;
                _flyoutClosedAt = Environment.TickCount64;
            };
            _flyout = flyout;
            flyout.ShowAt(anchor);
        }
        catch (Exception ex)
        {
            Log.Error($"tray flyout: {ex}");
            _flyout?.Dispose();
            _flyout = null;
            ShowMain(); // everything in the flyout is in the main window too
        }
    }

    public void ShowHiddenHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _balloonClick = null;
        _tray.ShowBalloonTip(3000, L.T("HomePod 音响"), L.T("已最小化到托盘，声音会继续推送。右键托盘图标可以退出。"), ToolTipIcon.Info);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Our session ends with the sleep: after a long one the speaker's flag is no longer ours (TrayApp.Takeover.cs).
        if (e.Mode == PowerModes.Suspend) OnSuspending();
        if (e.Mode == PowerModes.Resume)
            _ui.Post(async _ =>
            {
                await Task.Delay(3000); // let Wi-Fi come back
                if (CheckNight()) return; // woken in the night window with nobody there, or after it: 夜间自动断开 decides
                if (_wantConnected) Connect(ConnectTrigger.Reconnect);
            }, null);
    }

    private void KickIfWanted()
    {
        if (_wantConnected && Controller.State is StreamState.Retrying) Connect(ConnectTrigger.Reconnect);
    }

    private void OnControllerChanged()
    {
        // Quit stops the controller (which posts one last change) and then disposes the tray icon; seen on a real
        // machine as a NullReferenceException in NotifyIcon.UpdateIcon on every quit.
        if (_quitting) return;
        var c = Controller;
        if (_wantConnected && GivesUp(c.State, c.StatusText)) _wantConnected = false;

        var tip = $"{L.T("HomePod 音响")} · {c.StatusText}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (c.State != _lastIconState)
        {
            var old = _tray.Icon;
            _tray.Icon = Icons.Tray(c.State);
            old?.Dispose();
            _lastIconState = c.State;
        }
        var volume = c.Volume;
        if (SavesStreamingVolume(c, volume, Config, _pendingVolume != null))
        {
            Config.Volume = volume;
            Config.Save();
        }
        UpdateMonitorGuard();
        UpdateVolumeKeys(); // the hook is only in while streaming; following too
        _form.UpdateState();
        RaiseStateChanged();
    }

    /// <summary>
    /// The controller ended the session because another sender took the speaker (<see cref="StreamController.TakenOverText"/>):
    /// nothing reconnects by itself any more (a network change, a wake from sleep), so the speaker is never taken back.
    /// </summary>
    internal static bool GivesUp(StreamState state, string statusText) =>
        state == StreamState.Idle && statusText == L.T(StreamController.TakenOverText);

    /// <summary>
    /// The speaker's confirmed volume is saved as the selected speaker's volume (what the next connection starts with) only
    /// while the session streaming is the selected one. Right after a profile or the list switched speakers the previous
    /// one still streams until the queued restart has stopped it, and a change it reports meanwhile (a profile's lower cap
    /// lowers it) must not become the new speaker's volume: a later reconnect would send it there.
    /// </summary>
    /// <param name="volume">The controller's volume, read once (it may change on another thread).</param>
    /// <param name="pendingVolume">A key or hotkey volume waits to be saved (its debounce saves it instead).</param>
    internal static bool SavesStreamingVolume(StreamController c, double? volume, AppConfig cfg, bool pendingVolume) =>
        c.State == StreamState.Streaming && volume is { } v && cfg.Volume != v && !pendingVolume
        && c.OwnSession.IsLiveFor(OwnSession.KeyOf(cfg));

    private bool _quitting;

    /// <summary>Windows is closing us (shutdown, logoff, an installer's Restart Manager, Task Manager).</summary>
    internal void QuitForSystem()
    {
        Log.Info("closed by Windows (shutdown, installer or Task Manager): quitting");
        Quit();
    }

    internal void Quit()
    {
        if (_quitting) return; // the main window's FormClosing can call back in while we dispose it
        _quitting = true;
        var connecting = _connection.Close(); // nothing queued starts any more
        _flyout?.Close();
        _form.Flush();
        _tray.Visible = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        DisposeFirewall();
        DisposeUpdate();
        DisposeNightDisconnect();
        DisposeCaptureWatch();
        DisposeSound();
        DisposeEffects();
        DisposePlayers();
        _api?.Dispose();
        // A Start still running would otherwise begin a stream after the Stop below (it never faults).
        connecting.Wait(TimeSpan.FromSeconds(10));
        Controller.Dispose();
        DisposeTakeover(); // saves how our session ended (cleanly, by us)
        _tray.Dispose();
        ExitThread();
    }
}
