using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal enum SyncTestAction { None, Apply, Reset }

/// <summary>
/// Whether the sync test's slider value may be used: clicks were played and none failed, the slider shows a value the
/// user chose (not a prediction cut off at its end), and the test path never changed. A changed path stays changed
/// (latched), even if it comes back: the measurement was taken across the change.
/// </summary>
/// <param name="isPathCurrent">The connection, capture path, latency and FIFO target are still the ones the test began with.</param>
/// <param name="startClamped">The prediction lay outside the slider's range: only a value the user moved to counts.</param>
internal sealed class SyncMeasurementGate(Func<bool> isPathCurrent, bool startClamped = false)
{
    private volatile bool _hasClicks, _renderFailed, _pathChanged, _startClamped = startClamped;

    public bool HasClicks => _hasClicks;
    public bool RenderFailed => _renderFailed;
    public bool PathChanged => _pathChanged;
    public bool StartClamped => _startClamped;

    /// <summary>A click reached the Windows mix (render thread).</summary>
    public void ClickPlayed() => _hasClicks = true;

    /// <summary>The test sound could not be played (render thread).</summary>
    public void Failed() => _renderFailed = true;

    /// <summary>The user moved the slider.</summary>
    public void SliderMoved() => _startClamped = false;

    /// <summary>Checks the path now (a change is kept) and says whether the slider's value can be used.</summary>
    public bool CanUse
    {
        get
        {
            if (!isPathCurrent()) _pathChanged = true;
            return _hasClicks && !_renderFailed && !_pathChanged && !_startClamped;
        }
    }

    /// <summary>The line under the slider: why the value can't be used yet, or nothing.</summary>
    public string Status => _renderFailed ? L.T("无法播放测试音，请检查所选音源后重新测试。")
        : _pathChanged ? L.T("音源、连接或延迟已变化，请关闭后重新测试。")
        : !_hasClicks ? L.T("正在准备测试音…")
        : _startClamped ? L.T("估算值超出了滑块的范围，请先拖动滑块对准，再应用。")
        : "";
}

/// <summary>
/// Audio/video sync test: a click is played through the normal Windows output (the path game audio
/// takes) while the circle flashes. The user slides the flash later until both coincide; the slider
/// value is how far the sound lags the picture — what you actually feel in a game.
/// </summary>
internal sealed class SyncTestForm : FluentDialog
{
    private readonly ClickRenderer _clicks;
    private readonly SyncMeasurementGate _gate;
    private readonly System.Windows.Forms.Timer _pathWatch = new() { Interval = 250 };
    private readonly FluentButton _apply, _done;
    private readonly TextBlock _status = new("", TextStyle.Caption) { Wrap = true };
    private readonly BlockingCollection<long> _due = new();
    private readonly FlashCircle _circle = new();
    private readonly FluentSlider _offset;
    private readonly TextBlock _value = new("", TextStyle.Subtitle) { Align = HorizontalAlignment.Center };
    private readonly Thread _flasher;
    private volatile bool _closing;
    private volatile int _offsetMs;

    public int OffsetMs => _offsetMs;
    public SyncTestAction RequestedAction { get; private set; }
    public bool CanUseMeasurement => _gate.CanUse;

    /// <summary>QPC time at which each click enters the Windows mix (raised on the render thread).</summary>
    public event Action<long>? ClickScheduled;

    /// <summary>
    /// The slider's end: 600 ms, or further so the prediction it starts at is never cut off (Enter = 应用 would save the
    /// cut-off value), up to the largest measurement that can be saved.
    /// </summary>
    internal static int SliderMaximum(int predictedMs) =>
        Math.Clamp((Math.Clamp(predictedMs, 0, AvCalibrations.MaxMeasuredMs) + 299) / 100 * 100, 600, AvCalibrations.MaxMeasuredMs);

    public SyncTestForm(int initialOffsetMs, string captureEndpointId, bool hasCalibration = false,
        Func<bool>? isPathCurrent = null) : base(L.T("音画同步测试"))
    {
        _clicks = new ClickRenderer(captureEndpointId);
        int maximum = SliderMaximum(initialOffsetMs), start = Math.Clamp(initialOffsetMs, 0, maximum);
        _gate = new SyncMeasurementGate(isPathCurrent ?? (() => true), startClamped: start != initialOffsetMs);
        ContentWidth = 600;
        StartPosition = FormStartPosition.CenterScreen;
        var help = new TextBlock(L.T("HomePod 隔一两秒（随机）「咔」一声，圆圈同时闪一下。\n" +
                                     "如果先看到闪光、后听到声音，就把滑块往右拖，直到闪光和声音同时出现。\n" +
                                     "最后的数值 = 打游戏时声音比画面晚多少。"), wrap: true);
        _offset = new FluentSlider
        {
            Minimum = 0,
            Maximum = maximum,
            SmallChange = 5,
            LargeChange = 20,
            Value = start,
            AccessibleName = L.T("音画同步测试"),
        };
        _apply = new FluentButton(L.T("应用本次校准"), ButtonKind.Primary) { MinWidth = 110, Enabled = false };
        _done = new FluentButton(L.T("完成")) { MinWidth = 72, Enabled = false };
        var reset = new FluentButton(L.T("恢复默认补偿")) { Enabled = hasCalibration };
        _apply.Click += (_, _) =>
        {
            RefreshPathStatus();
            if (!CanUseMeasurement) return;
            RequestedAction = SyncTestAction.Apply;
            DialogResult = DialogResult.OK;
        };
        _done.Click += (_, _) =>
        {
            RefreshPathStatus();
            if (CanUseMeasurement) DialogResult = DialogResult.OK;
        };
        reset.Click += (_, _) =>
        {
            RequestedAction = SyncTestAction.Reset;
            DialogResult = DialogResult.Retry;
        };
        var note = new TextBlock(L.T("测试期间请暂停其他音频并关闭麦克风，以便检测测试音。请以 HomePod 发出的声音为准。应用后，这只音箱和当前音源、分流模式的补偿会自动用于首页、浏览器和播放器。"), TextStyle.Caption, wrap: true);
        Body.Controls.AddRange([help, _circle, _offset, _value, note, _status, ButtonRow(reset, _done, _apply)]);
        Body.GapBefore[_circle] = 16;
        AcceptButton = _apply;
        _offset.ValueChanged += (_, _) =>
        {
            UpdateValue();
            if (!_gate.StartClamped) return;
            _gate.SliderMoved(); // now a value the user chose
            RefreshPathStatus();
        };
        UpdateValue();
        ActiveControl = _offset;

        _clicks.ClickScheduled += when =>
        {
            _gate.ClickPlayed();
            ClickScheduled?.Invoke(when);
            if (_closing) return;
            try { _due.Add(when); } catch (InvalidOperationException) { }
        };
        _clicks.Failed += _ => _gate.Failed();
        _pathWatch.Tick += (_, _) => RefreshPathStatus();
        RefreshPathStatus();
        _flasher = new Thread(FlashLoop) { IsBackground = true, Name = "Sync flasher" };
        PerformLayout();
    }

    private void RefreshPathStatus()
    {
        _apply.Enabled = _done.Enabled = CanUseMeasurement;
        _status.Text = _gate.Status;
    }

    private void UpdateValue()
    {
        _offsetMs = _offset.Value;
        _value.Text = L.F("声音比画面晚约 {0} ms", _offset.Value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _pathWatch.Start();
        _flasher.Start();
        _clicks.Start();
    }

    private void FlashLoop()
    {
        using var timer = new Native.PreciseTimer();
        foreach (var when in _due.GetConsumingEnumerable())
        {
            if (_closing) return;
            long target = when + Net.MediaClock.FromMs(_offsetMs);
            timer.Sleep(target - Net.MediaClock.Now);
            SetLit(true);
            timer.Sleep(Net.MediaClock.FromMs(90));
            SetLit(false);
        }
    }

    private void SetLit(bool lit)
    {
        if (_closing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                _circle.Lit = lit;
                _circle.Update();
            });
        }
        catch (InvalidOperationException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopTest();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Ends the clicks and the flashes without waiting for them on the UI thread (closing used to join both, up to 2 s).
    /// The click thread stops within one 10 ms buffer and is joined on the thread pool. The flasher needs no join: it
    /// sees <see cref="_closing"/> when its current wait ends (at most the slider's value) and draws nothing more.
    /// </summary>
    private void StopTest()
    {
        if (_closing) return;
        _closing = true;
        _pathWatch.Stop();
        _due.CompleteAdding();
        var clicks = _clicks;
        _ = Task.Run(clicks.Dispose);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopTest(); // never shown, or closed without OnFormClosing
            _pathWatch.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The flashing circle on a dark stage (high contrast in both themes).</summary>
    private sealed class FlashCircle : FluentControl
    {
        private bool _lit;

        public bool Lit
        {
            get => _lit;
            set { _lit = value; Invalidate(); }
        }

        public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(180));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Shapes.FillRound(g, Color.FromArgb(24, 24, 24), new Rectangle(0, 0, Width, Height), Dp(8));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Height - Dp(40);
            using var b = new SolidBrush(_lit ? Color.White : Color.FromArgb(60, 60, 60));
            g.FillEllipse(b, (Width - d) / 2f, Dp(20), d, d);
        }
    }
}
