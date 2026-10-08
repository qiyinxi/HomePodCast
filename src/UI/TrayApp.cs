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
    public TrayApp(bool startHidden, EventWaitHandle showSignal, bool openMixer = false, bool openFlyout = false)
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

        Controller.Changed += () => _ui.Post(_ => OnControllerChanged(), null);
        Controller.HostResolved += host => _ui.Post(_ =>
        {
            if (Config.Host != host) { Config.Host = host; Config.Save(); }
        }, null);
        Controller.FirewallBlocked += () => _ui.Post(_ => OfferFirewallRule(), null);
        try
        {
            _api = new LocalApi(Config.LocalApiPort, () =>
            {
                bool streaming = Controller.State == StreamState.Streaming;
                return new
                {
                    app = "HomePodCast",
                    version = typeof(TrayApp).Assembly.GetName().Version?.ToString(3),
                    streaming,
                    device = Config.DeviceName,
                    latencyMs = Controller.EffectiveLatencyMs,
                    videoDelayMs = VideoDelayMs,
                    videoDelaySource = "estimate",
                };
            });
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Warn($"local API not started (port {Config.LocalApiPort}): {ex.Message}");
        }
        Controller.ArrivalToRenderMs = Config.ArrivalToRenderMs;
        Controller.ArrivalToRenderChanged += ms => _ui.Post(_ => { Config.ArrivalToRenderMs = ms; Config.Save(); }, null);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.Post(_ => KickIfWanted(), null);

        // Bring the existing window forward when a second instance is launched.
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => _ui.Post(_ => ShowMain(), null), null, -1, false);

        OnControllerChanged();
        if (!startHidden) ShowMain();
        if (openMixer)
        {
            // Wait for Application.Run's loop: a Post would also be dispatched by the COM wait inside the
            // firewall check below, and a form shown in there never gets its Shown/timer messages.
            void OpenMixer(object? s, EventArgs e) { Application.Idle -= OpenMixer; ShowMixer(); }
            Application.Idle += OpenMixer;
        }
        if (openFlyout)
        {
            void OpenFlyout(object? s, EventArgs e) { Application.Idle -= OpenFlyout; ShowFlyout(); }
            Application.Idle += OpenFlyout;
        }

        if (!Firewall.HasInboundAllowRule()) OfferFirewallRule();
        if (Config.DeviceId != null)
        {
            _form.SetDevices([new AirPlayDevice(Config.DeviceName ?? "?", Config.DeviceId,
                System.Net.IPAddress.TryParse(Config.Host, out var ip) ? ip : System.Net.IPAddress.None, 7000, "", new Dictionary<string, string>())]);
            if (Config.AutoConnect) Connect();
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
        Task.Run(Audio.SessionRouter.RestoreLeftovers); // apps left silent by a run that did not exit cleanly
    }

    // ---------------------------------------------------------------- actions

    public void Connect()
    {
        if (Config.DeviceId == null)
        {
            ShowMain();
            return;
        }
        _wantConnected = true;
        if (GroupPlan.FromConfig(Config) is { } group) // stereo pair / multi-room (experimental)
        {
            Controller.StartGroup(group, Config.LatencyMs, Config.Volume);
            return;
        }
        Controller.Start(Config.DeviceId, Config.Host, Config.LatencyMs, Config.Volume);
    }

    public void Disconnect()
    {
        _wantConnected = false;
        Task.Run(Controller.Stop);
    }

    public void ToggleConnection()
    {
        if (Controller.State == StreamState.Idle) Connect();
        else Disconnect();
    }

    public void SelectDevice(AirPlayDevice device)
    {
        if (StreamController.Normalize(device.DeviceId) == StreamController.Normalize(Config.DeviceId ?? "")) return;
        Config.DeviceId = device.DeviceId;
        Config.DeviceName = device.Name;
        Config.Host = device.Address.ToString();
        Config.Volume = null; // use the new speaker's own volume
        Config.Save();
        if (_wantConnected) Connect();
    }

    public void SetLatency(int ms)
    {
        if (Config.Scene == Scene.Custom) Config.CustomLatencyMs = ms;
        if (Config.LatencyMs == ms) return;
        Config.LatencyMs = ms;
        Config.Save();
        if (_wantConnected) Connect(); // latency is negotiated at SETUP, so reconnect
    }

    public void SetVolume(double percent)
    {
        percent = VolumeLimit.Clamp(percent, Config.VolumeCapPercent);
        Config.Volume = percent;
        Config.Save();
        if (Controller.Volume != percent || Controller.Muted) Controller.SetVolume(percent); // PreviewVolume may have sent it
    }

    /// <summary>
    /// A volume slider is moving: send it right away (the controller coalesces — latest value wins, one request
    /// in flight), so the speaker follows the drag. The slider's debounce then saves it via SetVolume/ApplyVolume.
    /// </summary>
    public void PreviewVolume(double percent) => Controller.SetVolume(VolumeLimit.Clamp(percent, Config.VolumeCapPercent));

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
            found = StereoPairs.Merge(found); // a stereo pair shows as one entry (experimental)
            // Keep the configured speaker in the list even if it didn't answer this time.
            if (Config.DeviceId != null && !found.Any(d =>
                    StreamController.Normalize(d.DeviceId).Equals(StreamController.Normalize(Config.DeviceId), StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new AirPlayDevice(Config.DeviceName ?? "?", Config.DeviceId, System.Net.IPAddress.None, 7000,
                    L.T("未发现"), new Dictionary<string, string>()));
            }
            _form.SetDevices(found);
            if (Config.DeviceId == null && found.FirstOrDefault(d => d.Model.StartsWith("AudioAccessory")) is { } homepod)
            {
                SelectDevice(homepod);
                if (Config.AutoConnect) Connect();
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

    public void RunSyncTest(IWin32Window owner)
    {
        if (Controller.State != StreamState.Streaming)
        {
            MessageBox.Show(owner, L.T("请先连接音箱，再做同步测试。"), L.T("音画同步测试"));
            return;
        }
        using var test = new SyncTestForm(Config.MeasuredAvOffsetMs ?? 0);

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

        var result = test.ShowDialog(owner);
        if (sender != null) sender.OnsetSent = null;
        if (local.Count > 0)
            Log.Info($"probe summary: local pipeline median {local.Order().ElementAt(local.Count / 2):F1} ms over {local.Count} clicks");
        if (result == DialogResult.OK)
        {
            Config.MeasuredAvOffsetMs = test.OffsetMs;
            Config.Save();
            Log.Info($"sync test: audio lags video by ~{test.OffsetMs} ms at latency {Config.LatencyMs} ms");
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
        _tray.ShowBalloonTip(3000, L.T("HomePod 音响"), L.T("已最小化到托盘，声音会继续推送。右键托盘图标可以退出。"), ToolTipIcon.Info);
    }

    private void OfferFirewallRule()
    {
        var answer = MessageBox.Show(
            L.T("HomePod 需要连回本程序（对时和丢包重传），但 Windows 防火墙还没有放行 HomePodCast。\n\n" +
                "点「是」会弹出管理员授权，添加一条只允许局域网、只在专用网络下生效的入站规则。"),
            L.T("需要防火墙放行"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        if (Firewall.RequestRule())
        {
            Log.Info("firewall rule added");
            if (_wantConnected) Connect();
        }
        else
        {
            MessageBox.Show(L.T("没有添加成功（可能取消了授权）。"), L.T("需要防火墙放行"));
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _ui.Post(async _ =>
            {
                await Task.Delay(3000); // let Wi-Fi come back
                if (_wantConnected) Connect();
            }, null);
    }

    private void KickIfWanted()
    {
        if (_wantConnected && Controller.State is StreamState.Retrying) Connect();
    }

    private void OnControllerChanged()
    {
        var c = Controller;
        if (c.State == StreamState.Idle && _wantConnected && c.StatusText == L.T(StreamController.TakenOverText)) _wantConnected = false;

        var tip = $"{L.T("HomePod 音响")} · {c.StatusText}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (c.State != _lastIconState)
        {
            var old = _tray.Icon;
            _tray.Icon = Icons.Speaker(Icons.For(c.State));
            old?.Dispose();
            _lastIconState = c.State;
        }
        if (c.State == StreamState.Streaming && c.Volume is { } v && Config.Volume != v)
        {
            Config.Volume = v;
            Config.Save();
        }
        _form.UpdateState();
        RaiseStateChanged();
    }

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
        _flyout?.Close();
        _form.Flush();
        _tray.Visible = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        DisposeSound();
        DisposeEffects();
        DisposePlayers();
        _api?.Dispose();
        Controller.Dispose();
        _tray.Dispose();
        ExitThread();
    }
}
