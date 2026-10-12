using System.Net.Http;
using Windows.Graphics.Capture;

namespace HomePodCast.VideoWindow;

internal sealed class ViewerForm : Form
{
    private readonly DelayQueue<Bitmap> _queue = new(128 * 1024 * 1024, 64);
    private readonly Preview _preview = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly FlowLayoutPanel _toolbar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
    private readonly CheckBox _auto = new() { AutoSize = true, Checked = true, Text = L.T("跟随 HomePod 延迟") };
    private readonly NumericUpDown _manual = new() { Minimum = 0, Maximum = 2000, Increment = 5, Width = 80 };
    private readonly NumericUpDown _offset = new() { Minimum = -500, Maximum = 500, Increment = 5, Width = 80 };
    private readonly System.Windows.Forms.Timer _render = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMilliseconds(800), MaxResponseContentBufferSize = 16 * 1024 };
    private readonly CancellationTokenSource _cancel = new();
    private readonly int _port;
    private CaptureEngine? _capture;
    private bool _polling, _picking, _closing, _fullscreen, _statusConnected;
    private int _audioDelay;
    private Rectangle _restoreBounds;
    private FormWindowState _restoreState;

    public ViewerForm(int port, int initialDelay)
    {
        _port = port; _audioDelay = initialDelay; _manual.Value = initialDelay;
        Text = L.T("同步观影窗口（实验性）"); Width = 1100; Height = 740; MinimumSize = new Size(640, 400);
        Font = new Font(L.FontName, 10); StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        var choose = Button(L.T("选择窗口…"), async () => await ChooseAsync());
        var stop = Button(L.T("停止采集"), () => StopCapture());
        var fullscreen = Button(L.T("全屏 / F11"), ToggleFullscreen);
        _manual.AccessibleName = L.T("手动画面延迟，毫秒"); _offset.AccessibleName = L.T("同步微调，毫秒");
        _toolbar.Controls.AddRange([choose, stop, _auto, Label(L.T("手动画面延迟，毫秒")), _manual,
            Label(L.T("同步微调，毫秒")), _offset,
            Button("−20 ms", () => Adjust(-20)), Button("+20 ms", () => Adjust(20)), fullscreen]);
        Controls.Add(_preview); Controls.Add(_toolbar); Controls.Add(_status);
        _status.Text = L.T("选择要观看的窗口；这里只延迟画面，音频仍由 HomePodCast 发送。");
        _auto.CheckedChanged += (_, _) => { _manual.Enabled = !_auto.Checked; UpdateDelay(); };
        _manual.ValueChanged += (_, _) => UpdateDelay(); _offset.ValueChanged += (_, _) => UpdateDelay();
        _manual.Enabled = false;
        _render.Tick += (_, _) => Render(); _render.Start();
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
    private void UpdateDelay() { if (_capture != null) _capture.DelayMs = DelayMs; }

    private async Task ChooseAsync()
    {
        if (_picking || _closing) return;
        _picking = true;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException(L.T("此 Windows 环境不支持窗口采集。"));
            var picker = new GraphicsCapturePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
            var item = await picker.PickSingleItemAsync();
            if (item == null || _closing) return;
            StopCapture();
            var capture = new CaptureEngine(item, _queue) { DelayMs = DelayMs };
            _capture = capture;
            capture.Closed += () => Post(() =>
            {
                if (_capture != capture) return;
                StopCapture(); _status.Text = L.T("源窗口已关闭，采集已停止。");
            });
            capture.Failed += error => Post(() =>
            {
                if (_capture != capture) return;
                StopCapture(); _status.Text = L.F("窗口采集失败：{0}", error);
            });
            Text = L.T("同步观影窗口（实验性）") + " · " + item.DisplayName;
        }
        catch (Exception ex) { _status.Text = L.F("窗口采集失败：{0}", ex.Message); }
        finally { _picking = false; }
    }

    private void Post(Action action) { if (!_closing && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    private void StopCapture()
    {
        var capture = _capture; _capture = null; capture?.Dispose(); _queue.Clear(); _preview.SetFrame(null);
        _status.Text = L.T("选择要观看的窗口；这里只延迟画面，音频仍由 HomePodCast 发送。");
    }
    private void Render()
    {
        if (_capture == null) return;
        var frame = _queue.TakeReady(CaptureEngine.Now, DelayMs * 10_000L);
        if (frame != null) _preview.SetFrame(frame);
        var buffered = _queue.Buffered;
        string mode = !_auto.Checked ? L.T("手动") : _statusConnected ? L.T("已跟随本机音频状态") : L.T("本机状态不可用，保持上次延迟");
        _status.Text = L.F("画面延迟 {0} ms · {1} · 缓存 {2} 帧 / {3} MiB", DelayMs, mode, buffered.Frames, buffered.Bytes / 1048576);
    }
    private async Task PollAsync()
    {
        if (_polling || _closing || _port is < 1 or > 65535) return;
        _polling = true;
        try
        {
            int delay = StatusDelay.Read(await _http.GetStringAsync($"http://127.0.0.1:{_port}/v1/status", _cancel.Token));
            if (_closing) return;
            _audioDelay = delay; _statusConnected = true; UpdateDelay();
        }
        catch (Exception) { if (!_closing) _statusConnected = false; }
        finally { _polling = false; }
    }
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreBounds = Bounds; _restoreState = WindowState; WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds;
            _toolbar.Visible = false; _status.Visible = false;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable; Bounds = _restoreBounds; WindowState = _restoreState;
            _toolbar.Visible = true; _status.Visible = true;
        }
        _fullscreen = !_fullscreen;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true; _cancel.Cancel(); _render.Dispose(); _poll.Dispose(); StopCapture(); _queue.Dispose(); _http.Dispose(); _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed class Preview : Control
    {
        private Bitmap? _frame;
        public Preview() { DoubleBuffered = true; ResizeRedraw = true; }
        public void SetFrame(Bitmap? frame) { var old = _frame; _frame = frame; old?.Dispose(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_frame == null) return;
            double scale = Math.Min(Width / (double)_frame.Width, Height / (double)_frame.Height);
            int w = (int)(_frame.Width * scale), h = (int)(_frame.Height * scale);
            e.Graphics.DrawImage(_frame, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
        }
        protected override void Dispose(bool disposing) { if (disposing) SetFrame(null); base.Dispose(disposing); }
    }
}
