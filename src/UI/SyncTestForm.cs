using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// Audio/video sync test: a click is played through the normal Windows output (the path game audio
/// takes) while the circle flashes. The user slides the flash later until both coincide; the slider
/// value is how far the sound lags the picture — what you actually feel in a game.
/// </summary>
internal sealed class SyncTestForm : FluentDialog
{
    private readonly ClickRenderer _clicks = new();
    private readonly BlockingCollection<long> _due = new();
    private readonly FlashCircle _circle = new();
    private readonly FluentSlider _offset;
    private readonly TextBlock _value = new("", TextStyle.Subtitle) { Align = HorizontalAlignment.Center };
    private readonly Thread _flasher;
    private volatile bool _closing;
    private volatile int _offsetMs;

    public int OffsetMs => _offsetMs;

    /// <summary>QPC time at which each click enters the Windows mix (raised on the render thread).</summary>
    public event Action<long>? ClickScheduled;

    public SyncTestForm(int initialOffsetMs) : base(L.T("音画同步测试"))
    {
        ContentWidth = 540;
        StartPosition = FormStartPosition.CenterScreen;
        var help = new TextBlock(L.T("HomePod 隔一两秒（随机）「咔」一声，圆圈同时闪一下。\n" +
                                     "如果先看到闪光、后听到声音，就把滑块往右拖，直到闪光和声音同时出现。\n" +
                                     "最后的数值 = 打游戏时声音比画面晚多少。"), wrap: true);
        _offset = new FluentSlider
        {
            Minimum = 0,
            Maximum = 600,
            SmallChange = 5,
            LargeChange = 20,
            Value = Math.Clamp(initialOffsetMs, 0, 600),
            AccessibleName = L.T("音画同步测试"),
        };
        var done = new FluentButton(L.T("完成"), ButtonKind.Primary) { DialogResult = DialogResult.OK, MinWidth = 110 };
        Body.Controls.AddRange([help, _circle, _offset, _value, ButtonRow(done)]);
        Body.GapBefore[_circle] = 16;
        AcceptButton = done;
        _offset.ValueChanged += (_, _) => UpdateValue();
        UpdateValue();
        ActiveControl = _offset;

        _clicks.ClickScheduled += when =>
        {
            ClickScheduled?.Invoke(when);
            if (!_due.IsAddingCompleted) _due.Add(when);
        };
        _flasher = new Thread(FlashLoop) { IsBackground = true, Name = "Sync flasher" };
        PerformLayout();
    }

    private void UpdateValue()
    {
        _offsetMs = _offset.Value;
        _value.Text = L.F("声音比画面晚约 {0} ms", _offset.Value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
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
        _closing = true;
        _due.CompleteAdding();
        _clicks.Dispose();
        base.OnFormClosing(e);
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

        public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(220));

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
