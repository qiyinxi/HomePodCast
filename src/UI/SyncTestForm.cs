using System.Collections.Concurrent;
using HomePodCast.Audio;

namespace HomePodCast.UI;

/// <summary>
/// Audio/video sync test: a click is played through the normal Windows output (the path game audio
/// takes) while the circle flashes. The user slides the flash later until both coincide; the slider
/// value is how far the sound lags the picture — what you actually feel in a game.
/// </summary>
internal sealed class SyncTestForm : Form
{
    private readonly ClickRenderer _clicks = new();
    private readonly BlockingCollection<long> _due = new();
    private readonly Panel _circle;
    private readonly TrackBar _offset;
    private readonly Label _value;
    private readonly Thread _flasher;
    private volatile bool _closing;
    private volatile int _offsetMs;
    private bool _lit;

    public int OffsetMs => _offsetMs;

    public SyncTestForm(int initialOffsetMs)
    {
        Text = "音画同步测试";
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.Gainsboro;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 470);
        Icon = Icons.Speaker(Icons.Streaming);

        var help = new Label
        {
            Text = "HomePod 每秒「咔」一声，圆圈同时闪一下。\n" +
                   "如果先看到闪光、后听到声音，就把滑块往右拖，直到闪光和声音同时出现。\n" +
                   "最后的数值 = 打游戏时声音比画面晚多少。",
            AutoSize = false,
            Location = new Point(20, 16),
            Size = new Size(480, 72),
        };
        _circle = new DoubleBufferedPanel { Location = new Point(160, 100), Size = new Size(200, 200) };
        _circle.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_lit ? Color.White : Color.FromArgb(60, 60, 60));
            e.Graphics.FillEllipse(b, 10, 10, 180, 180);
        };
        _offset = new TrackBar
        {
            Location = new Point(20, 320),
            Size = new Size(480, 45),
            Minimum = 0,
            Maximum = 600,
            SmallChange = 5,
            LargeChange = 20,
            TickFrequency = 50,
            Value = Math.Clamp(initialOffsetMs, 0, 600),
        };
        _value = new Label { Location = new Point(20, 372), Size = new Size(480, 28), Font = new Font(Font.FontFamily, 12f, FontStyle.Bold) };
        var done = new Button
        {
            Text = "完成",
            Location = new Point(400, 412),
            Size = new Size(100, 36),
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(50, 50, 50),
            FlatStyle = FlatStyle.Flat,
        };
        AcceptButton = done;
        _offset.ValueChanged += (_, _) => UpdateValue();
        Controls.AddRange([help, _circle, _offset, _value, done]);
        UpdateValue();

        _clicks.ClickScheduled += when => _due.Add(when);
        _flasher = new Thread(FlashLoop) { IsBackground = true, Name = "Sync flasher" };
    }

    private void UpdateValue()
    {
        _offsetMs = _offset.Value;
        _value.Text = $"声音比画面晚约 {_offset.Value} ms";
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
                _lit = lit;
                _circle.Invalidate();
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

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}
