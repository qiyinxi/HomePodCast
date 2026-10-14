using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics.Capture;

namespace HomePodCast.VideoWindow;

internal sealed class ViewerForm : Form
{
    // GPU memory for queued pictures: 30 frames of 1080p (a 4K source), about a second at 30 fps; longer delays thin
    // the frames out (FrameCadence.IntervalFor).
    private const long QueueBytes = 256L * 1024 * 1024;
    private const int QueueFrames = 64;

    private readonly Gpu _gpu;
    private readonly FrameScaler _scaler;
    private readonly DelayQueue<GpuFrame> _queue = new(QueueBytes, QueueFrames);
    private readonly Panel _stage = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly VideoSurface _surface = new();
    private readonly FlowLayoutPanel _toolbar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
    private readonly Label _notice = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 6, 6, 0), Visible = false };
    private readonly CheckBox _auto = new() { AutoSize = true, Checked = true, Text = L.T("跟随 HomePod 延迟") };
    private readonly NumericUpDown _manual = new() { Minimum = 0, Maximum = 2000, Increment = 5, Width = 80, Enabled = false };
    private readonly NumericUpDown _offset = new() { Minimum = -500, Maximum = 500, Increment = 5, Width = 80 };
    private readonly CheckBox _autoLead = new() { AutoSize = true, Checked = true, Text = L.T("自动扣除显示延迟") };
    private readonly NumericUpDown _lead = new() { Minimum = 0, Maximum = 100, Increment = 1, Width = 70, Enabled = false };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMilliseconds(800), MaxResponseContentBufferSize = 16 * 1024 };
    private readonly CancellationTokenSource _cancel = new();
    private readonly int _port;
    private readonly bool _smoke;
    private readonly string? _smokeResult;
    private Presenter? _presenter;
    private IFrameSource? _source;
    private Size _frameSize;
    private bool _polling, _picking, _closing, _fullscreen, _statusConnected, _versionMismatch;
    private int _audioDelay;
    private string? _message;
    private Rectangle _restoreBounds;
    private FormWindowState _restoreState;

    private static string Title => L.T("同步观影窗口（实验性）"); // experimental since 1.1
    private static Version? ModuleVersion => typeof(ViewerForm).Assembly.GetName().Version;

    /// <param name="smoke">CI smoke test (gui-smoke): generated pictures instead of a captured window.</param>
    /// <param name="smokeResult">Where the smoke test's counts go when the window closes.</param>
    public ViewerForm(int port, int initialDelay, bool smoke = false, string? smokeResult = null)
    {
        _port = port; _smoke = smoke; _smokeResult = smokeResult;
        _audioDelay = initialDelay; _manual.Value = initialDelay;
        _gpu = new Gpu();
        _scaler = new FrameScaler(_gpu);

        SuspendLayout();
        // Per-monitor DPI (VideoWindow.csproj): sizes below are at 96 dpi and scaled to the monitor's.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Title;
        ClientSize = new Size(1100, 700); MinimumSize = new Size(640, 400);
        Font = new Font(L.FontName, 10); StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        var choose = Button(L.T("选择窗口…"), async () => await ChooseAsync());
        var stop = Button(L.T("停止采集"), () => StopCapture());
        var fullscreen = Button(L.T("全屏 / F11"), ToggleFullscreen);
        _manual.AccessibleName = L.T("手动画面延迟，毫秒"); _offset.AccessibleName = L.T("同步微调，毫秒");
        _lead.AccessibleName = L.T("显示延迟，毫秒");
        _toolbar.Controls.AddRange([choose, stop, _auto, Label(L.T("手动画面延迟，毫秒")), _manual,
            Label(L.T("同步微调，毫秒")), _offset, Button("−20 ms", () => Adjust(-20)), Button("+20 ms", () => Adjust(20)),
            _autoLead, _lead, fullscreen]);
        _stage.Controls.Add(_surface);
        Controls.Add(_stage); Controls.Add(_toolbar); Controls.Add(_notice); Controls.Add(_status);
        _notice.ForeColor = Color.FromArgb(0x9d, 0x5d, 0x00);
        ResumeLayout(false);
        PerformLayout();

        _status.Text = L.T("选择要观看的窗口；这里只延迟画面，音频仍由 HomePodCast 发送。");
        _auto.CheckedChanged += (_, _) => { _manual.Enabled = !_auto.Checked; UpdateDelay(); };
        _manual.ValueChanged += (_, _) => UpdateDelay(); _offset.ValueChanged += (_, _) => UpdateDelay();
        _autoLead.CheckedChanged += (_, _) => { _lead.Enabled = !_autoLead.Checked; UpdateLead(); };
        _lead.ValueChanged += (_, _) => UpdateLead();
        _stage.Resize += (_, _) => PlaceSurface();
        _tick.Tick += (_, _) => Tick(); _tick.Start();
        _poll.Tick += async (_, _) => await PollAsync(); _poll.Start();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; }
        };
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 8, 2, 0) };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => action(); return button;
    }
    private void Adjust(int delta) => _offset.Value = Math.Clamp(_offset.Value + delta, _offset.Minimum, _offset.Maximum);
    private int DelayMs => StatusDelay.Effective(_auto.Checked ? _audioDelay : (int)_manual.Value, (int)_offset.Value);

    private void UpdateDelay()
    {
        if (_source != null) _source.DelayMs = DelayMs;
        if (_presenter != null) _presenter.DelayMs = DelayMs;
    }

    private void UpdateLead()
    {
        if (_presenter != null) _presenter.ManualLeadMs = _autoLead.Checked ? null : (int)_lead.Value;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CreatePresenter();
        // The swap chain belongs to the surface's window: should WinForms ever recreate it, the presenter goes with it.
        _surface.HandleDestroyed += (_, _) => { if (!_closing) { _presenter?.Dispose(); _presenter = null; } };
        _surface.HandleCreated += (_, _) => { if (!_closing) CreatePresenter(); };
        PlaceSurface();
        Tick();
    }

    private void CreatePresenter()
    {
        if (_presenter != null) return;
        Presenter? presenter = null;
        try
        {
            presenter = new Presenter(_gpu, _queue, _surface.Handle);
            var created = presenter;
            presenter.FrameSizeChanged += size => Post(() => { _frameSize = size; PlaceSurface(); });
            presenter.Failed += hr => Post(() =>
            {
                if (_presenter != created) return;
                // Its thread has ended (a lost device, say): nothing more is shown until the window is opened again.
                StopCapture(L.F("画面显示失败，错误代码 {0}。采集已停止。", Failure.Code(hr)));
                _presenter?.Dispose(); _presenter = null;
            });
            _presenter = presenter;
            UpdateDelay(); UpdateLead();
            presenter.Active = _source != null;
        }
        catch (Exception ex)
        {
            presenter?.Dispose();
            _message = L.F("画面显示失败，错误代码 {0}。采集已停止。", Failure.Code(ex.HResult));
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_smoke && _presenter != null) StartSource(new SyntheticSource(_gpu, _scaler, _queue), "smoke");
    }

    private async Task ChooseAsync()
    {
        if (_picking || _closing || _presenter == null) return;
        _picking = true;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) { StopCapture(L.T("此 Windows 环境不支持窗口采集。")); return; }
            var picker = new GraphicsCapturePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
            var item = await picker.PickSingleItemAsync();
            if (item == null || _closing) return;
            StopCapture();
            StartSource(new CaptureEngine(_gpu, _scaler, _queue, item), item.DisplayName);
        }
        catch (Exception ex) { StopCapture(Failure.Capture(ex)); }
        finally { _picking = false; }
    }

    private void StartSource(IFrameSource source, string name)
    {
        source.DelayMs = DelayMs;
        source.Closed += () => Post(() => { if (_source == source) StopCapture(L.T("源窗口已关闭，采集已停止。")); });
        source.Failed += error => Post(() => { if (_source == source) StopCapture(error); });
        _source = source;
        _message = null;
        _presenter!.Active = true;
        // While capturing, this window stays out of every capture: a whole-screen source would otherwise show itself.
        ExcludeFromCapture(source is CaptureEngine);
        Text = Title + " · " + name;
        Tick();
    }

    private void Post(Action action) { if (!_closing && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }

    private void StopCapture(string? message = null)
    {
        var source = _source; _source = null; source?.Dispose();
        _queue.Clear();
        if (_presenter != null) { _presenter.Active = false; _presenter.Clear(); }
        _frameSize = Size.Empty;
        _message = message;
        if (_closing) return;
        PlaceSurface();
        ExcludeFromCapture(false);
        Text = Title;
        Tick();
    }

    /// <summary>The status line (only written when it changes) and the measured display latency.</summary>
    private void Tick()
    {
        if (_closing) return;
        if (_presenter != null && _autoLead.Checked && _presenter.LeadMs != (int)_lead.Value)
            _lead.Value = Math.Clamp(_presenter.LeadMs, (int)_lead.Minimum, (int)_lead.Maximum);
        string text;
        if (_source == null) text = _message ?? L.T("选择要观看的窗口；这里只延迟画面，音频仍由 HomePodCast 发送。");
        else
        {
            var buffered = _queue.Buffered;
            string mode = !_auto.Checked ? L.T("手动") : _statusConnected ? L.T("已跟随本机音频状态") : L.T("本机状态不可用，保持上次延迟");
            text = L.F("画面延迟 {0} ms · {1} · 缓存 {2} 帧 / {3} MiB", DelayMs, mode, buffered.Frames, buffered.Bytes / 1048576);
        }
        if (_status.Text != text) _status.Text = text;
    }

    private async Task PollAsync()
    {
        if (_polling || _closing || _port is < 1 or > 65535) return;
        _polling = true;
        try
        {
            var status = StatusDelay.Read(await _http.GetStringAsync($"http://127.0.0.1:{_port}/v1/status", _cancel.Token));
            if (_closing) return;
            _audioDelay = status.DelayMs; _statusConnected = true; UpdateDelay();
            bool mismatch = status.Version != null && !StatusDelay.SameVersion(status.Version, ModuleVersion);
            if (mismatch != _versionMismatch)
            {
                _versionMismatch = mismatch;
                _notice.Text = L.F("模块版本 {0} 与 HomePodCast {1} 不同，请下载同一版本的模块。", ModuleVersion?.ToString(3), status.Version);
                _notice.Visible = mismatch && !_fullscreen;
            }
        }
        catch (Exception) { if (!_closing) _statusConnected = false; }
        finally { _polling = false; }
    }

    /// <summary>The picture keeps its aspect ratio inside the black stage; DXGI stretches the frame to this window.</summary>
    private void PlaceSurface()
    {
        var area = _stage.ClientRectangle;
        if (_frameSize.IsEmpty || area.Width <= 0 || area.Height <= 0) { _surface.Bounds = area; return; }
        double scale = Math.Min(area.Width / (double)_frameSize.Width, area.Height / (double)_frameSize.Height);
        int w = Math.Max(1, (int)Math.Round(_frameSize.Width * scale)), h = Math.Max(1, (int)Math.Round(_frameSize.Height * scale));
        _surface.Bounds = new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h);
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            var screen = Screen.FromControl(this).Bounds;
            // Maximized: Bounds is the maximized rectangle; the size to come back to is RestoreBounds.
            _restoreState = WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None; Bounds = screen;
            _toolbar.Visible = false; _status.Visible = false; _notice.Visible = false;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable; Bounds = _restoreBounds; WindowState = _restoreState;
            _toolbar.Visible = true; _status.Visible = true; _notice.Visible = _versionMismatch;
        }
        _fullscreen = !_fullscreen;
    }

    private void ExcludeFromCapture(bool exclude)
    {
        if (IsHandleCreated) SetWindowDisplayAffinity(Handle, exclude ? WdaExcludeFromCapture : 0); // before Windows 10 2004: no effect
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_smokeResult != null) WriteSmokeResult(_smokeResult);
        base.OnFormClosed(e);
    }

    /// <summary>What gui-smoke checks: frames went through the queue and the presenter, at the scaled size, at this DPI.</summary>
    private void WriteSmokeResult(string path)
    {
        var (shown, presents) = _presenter?.Counts ?? (0, 0);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            shown, presents, frameWidth = _frameSize.Width, frameHeight = _frameSize.Height,
            leadMs = _presenter?.LeadMs, leadMeasured = _presenter?.LeadMeasured, dpi = DeviceDpi,
            surfaceWidth = _surface.Width, surfaceHeight = _surface.Height, message = _message,
        }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closing)
        {
            _closing = true; _cancel.Cancel(); _tick.Dispose(); _poll.Dispose(); StopCapture();
            _presenter?.Dispose(); _queue.Dispose(); _scaler.Dispose(); _gpu.Dispose(); _http.Dispose(); _cancel.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The swap chain's window: DXGI draws it, so WinForms paints nothing here.</summary>
    private sealed class VideoSurface : Control
    {
        public VideoSurface()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }
    }

    private const uint WdaExcludeFromCapture = 0x11;
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
