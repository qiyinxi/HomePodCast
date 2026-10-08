using System.Net.NetworkInformation;
using HomePodCast.Net;
using Microsoft.Win32;

namespace HomePodCast.UI;

internal sealed partial class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _toggleItem = new(L.T("连接"));
    private readonly MainForm _form;
    private StreamState _lastIconState = (StreamState)(-1);
    private LocalApi? _api;
    private bool _wantConnected;
    private bool _hintShown;

    public AppConfig Config { get; }
    public StreamController Controller { get; }

    public TrayApp(bool startHidden, EventWaitHandle showSignal, bool openMixer = false)
    {
        Config = AppConfig.Load();
        Controller = new StreamController(Config.FifoTargetMs);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(L.T("打开主界面"), null, (_, _) => ShowMain());
        menu.Items.Add(L.T("混音器"), null, (_, _) => ShowMixer());
        menu.Items.Add(LanguageMenu.Create(Config, Quit));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("退出"), null, (_, _) => Quit());
        _toggleItem.Click += (_, _) => ToggleConnection();
        InitSound(menu);

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = L.T("HomePod 音响") };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };

        _form = new MainForm(this);
        _ = _form.Handle; // create handle so BeginInvoke works before first show

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
                    videoDelayMs = streaming ? Controller.EffectiveLatencyMs + Config.VideoDelayExtraMs : 0,
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
        if (openMixer) _ui.Post(_ => ShowMixer(), null);

        if (!Firewall.HasInboundAllowRule()) OfferFirewallRule();
        if (Config.DeviceId != null)
        {
            _form.SetDevices([new AirPlayDevice(Config.DeviceName ?? "?", Config.DeviceId,
                System.Net.IPAddress.TryParse(Config.Host, out var ip) ? ip : System.Net.IPAddress.None, 7000, "", new Dictionary<string, string>())]);
            if (Config.AutoConnect) Connect();
        }
        RefreshDevices();
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
        Controller.SetVolume(percent);
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

    private MixerForm? _mixer;

    public void ShowMixer()
    {
        if (_mixer == null || _mixer.IsDisposed)
        {
            _mixer = new MixerForm(this);
            _mixer.FormClosed += (_, _) => _mixer = null;
            _mixer.Show();
        }
        _mixer.Activate();
    }

    public void ShowMain()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
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

        _statusItem.Text = c.StatusText;
        _toggleItem.Text = c.State == StreamState.Idle ? L.T("连接") : L.T("断开");
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
    }

    private void Quit()
    {
        _tray.Visible = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        DisposeSound();
        _api?.Dispose();
        Controller.Dispose();
        _tray.Dispose();
        ExitThread();
    }
}
