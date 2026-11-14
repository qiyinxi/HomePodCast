using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed record GroupAlignmentOutput(string Id, string Name, int TrimMs);

internal static class ManualGroupAlignment
{
    internal static bool TryTrim(int currentTrimMs, int lateFrames, int framesPerSecond, out int trimMs)
    {
        trimMs = currentTrimMs;
        if (currentTrimMs is < -5000 or > 5000 || framesPerSecond is < 1 or > 1000) return false;
        long difference = (long)Math.Round(lateFrames * 1000.0 / framesPerSecond, MidpointRounding.AwayFromZero);
        long adjusted = currentTrimMs - difference;
        if (adjusted is < -5000 or > 5000) return false;
        trimMs = (int)adjusted; return true;
    }
}

/// <summary>Relative alignment remains usable without a microphone; opening this dialog starts nothing.</summary>
internal sealed class GroupAlignmentForm : FluentDialog
{
    private readonly IReadOnlyList<GroupAlignmentOutput> _outputs;
    private readonly FluentComboBox _reference = new() { AccessibleName = L.T("参考输出"), MinWidth = 200 };
    private readonly FluentComboBox _target = new() { AccessibleName = L.T("待校准输出"), MinWidth = 200 };
    private readonly FluentComboBox _fps = new() { AccessibleName = L.T("录像帧率"), MinWidth = 120 };
    private readonly FluentSlider _frames = Ui.Slider(-3000, 3000, 1, 10);
    private readonly TextBlock _result = Ui.Note();
    private readonly FluentButton _apply = new(L.T("应用微调"), ButtonKind.Primary);
    private readonly FluentButton _automatic = new(L.T("麦克风自动测量（实验性）"), ButtonKind.Primary); // experimental since 1.3
    private readonly int[] _rates = [30, 60, 120, 240];
    private readonly Func<IWin32Window, CancellationToken, Task<IReadOnlyList<GroupAlignmentOutput>>>? _measureAutomatically;
    private CancellationTokenSource? _measureCancellation;
    private bool _placed;
    public GroupAlignmentOutput? Adjustment { get; private set; }
    public IReadOnlyList<GroupAlignmentOutput>? AutomaticAdjustments { get; private set; }

    internal GroupAlignmentForm(IReadOnlyList<GroupAlignmentOutput> outputs, bool canPlayTest,
        Func<IWin32Window, Task>? playTest = null, bool canMeasureMicrophone = false,
        Func<IWin32Window, CancellationToken, Task<IReadOnlyList<GroupAlignmentOutput>>>? measureAutomatically = null) : base(L.T("手动校准"))
    {
        _outputs = outputs; _measureAutomatically = measureAutomatically; ContentWidth = 640;
        _frames.AccessibleName = L.T("相对偏移帧数"); _frames.PreferredLength = 240; _frames.FillOrigin = 0;
        foreach (var output in outputs) { _reference.Items.Add(output.Name); _target.Items.Add(output.Name); }
        _reference.SelectedIndex = outputs.Count > 0 ? 0 : -1;
        _target.SelectedIndex = outputs.Count > 1 ? 1 : outputs.Count - 1;
        foreach (int rate in _rates) _fps.Items.Add(rate + " fps");
        _fps.SelectedIndex = 1;
        var value = new TextBlock("0");
        var start = new FluentButton(L.T("开始测试音")) { Enabled = canPlayTest && playTest != null };
        _automatic.Enabled = canMeasureMicrophone && measureAutomatically != null;
        var close = new FluentButton(L.T("关闭")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([
            Ui.Note(L.T("先应用分组并连接，再开始自动测量。测量时节目声音暂时静音，每只音箱以最大音量单独播放测试音。")),
            Ui.Note(L.T("麦克风测量支持实时分组中的 AirPlay 2 和本机输出。录音只在内存中分析，不保存、不上传；完成后恢复原音量。")),
            Ui.Note(L.T("测量期间组内节目声音会暂时静音。")),
            new FieldRow(L.T("参考输出"), _reference), new FieldRow(L.T("待校准输出"), _target),
            new FieldRow(L.T("录像帧率"), _fps),
            new FieldRow(L.T("相对偏移帧数"), Ui.Row(6, null, _frames, value)),
            _result, Ui.Row(8, null, start, _automatic), ButtonRow(close, _apply),
        ]);
        _reference.SelectedIndexChanged += (_, _) => UpdateResult();
        _target.SelectedIndexChanged += (_, _) => UpdateResult();
        _fps.SelectedIndexChanged += (_, _) => UpdateResult();
        _frames.ValueChanged += (_, _) => { value.Text = _frames.Value.ToString(); UpdateResult(); };
        start.Click += async (_, _) =>
        {
            if (!start.Enabled || playTest == null) return;
            start.Enabled = false;
            try { await playTest(this); }
            catch (OperationCanceledException) { if (!IsDisposed) _result.Text = L.T("音源、连接或延迟已变化，请关闭后重新测试。"); }
            catch (Exception) { if (!IsDisposed) _result.Text = L.T("无法播放测试音，请检查所选音源后重新测试。"); }
            finally { if (!IsDisposed) start.Enabled = canPlayTest; }
        };
        _automatic.Click += async (_, _) =>
        {
            if (!_automatic.Enabled || _measureAutomatically == null) return;
            _automatic.Enabled = false; start.Enabled = false;
            using var cancellation = new CancellationTokenSource();
            _measureCancellation = cancellation;
            try
            {
                var adjustments = await _measureAutomatically(this, cancellation.Token);
                if (adjustments.Count != _outputs.Count) throw new InvalidOperationException("The measurement did not cover every output");
                AutomaticAdjustments = adjustments;
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) { if (!IsDisposed) _result.Text = L.T("测量已取消，原来的延迟没有改。"); }
            catch (Exception ex) { if (!IsDisposed) _result.Text = ex.Message; }
            finally
            {
                _measureCancellation = null;
                if (!IsDisposed) { _automatic.Enabled = canMeasureMicrophone; start.Enabled = canPlayTest && playTest != null; }
            }
        };
        _apply.Click += (_, _) =>
        {
            if (!TryAdjustment(out var adjustment)) return;
            Adjustment = adjustment; DialogResult = DialogResult.OK;
        };
        AcceptButton = _apply; CancelButton = close; UpdateResult();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PerformLayout();
        // FluentDialog fits its translated content after the initial CenterParent placement.
        // Centre the final DPI-sized window again, then keep its buttons above the taskbar.
        PlaceWithinWorkingArea(center: true);
        _placed = true;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_placed) PlaceWithinWorkingArea(center: false);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_placed) PlaceWithinWorkingArea(center: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _measureCancellation?.Cancel();
        base.OnFormClosing(e);
    }

    private void PlaceWithinWorkingArea(bool center)
    {
        var work = Screen.FromControl(Owner ?? this).WorkingArea;
        var anchor = Owner?.Bounds ?? work;
        int x = center ? anchor.Left + (anchor.Width - Width) / 2 : Left;
        int y = center ? anchor.Top + (anchor.Height - Height) / 2 : Top;
        Location = new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - Height)));
    }

    private bool TryAdjustment(out GroupAlignmentOutput? result)
    {
        result = null;
        if (_reference.SelectedIndex < 0 || _target.SelectedIndex < 0 || _reference.SelectedIndex == _target.SelectedIndex || _fps.SelectedIndex < 0) return false;
        var target = _outputs[_target.SelectedIndex];
        if (!ManualGroupAlignment.TryTrim(target.TrimMs, _frames.Value, _rates[_fps.SelectedIndex], out int trim)) return false;
        result = target with { TrimMs = trim }; return true;
    }
    private void UpdateResult()
    {
        _apply.Enabled = TryAdjustment(out var result);
        _result.Text = result != null ? L.F("将「{0}」的微调设为 {1} ms；只更新编辑器，应用分组后生效。", result.Name, result.TrimMs)
            : L.T("请选择两个不同的输出；微调必须在 -5000 到 5000 ms 之间。");
    }
}
