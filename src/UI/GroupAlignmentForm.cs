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
    private readonly int[] _rates = [30, 60, 120, 240];
    public GroupAlignmentOutput? Adjustment { get; private set; }

    internal GroupAlignmentForm(IReadOnlyList<GroupAlignmentOutput> outputs, bool canPlayTest,
        Func<IWin32Window, Task>? playTest = null) : base(L.T("手动校准"))
    {
        _outputs = outputs; ContentWidth = 640;
        _frames.AccessibleName = L.T("相对偏移帧数"); _frames.PreferredLength = 240; _frames.FillOrigin = 0;
        foreach (var output in outputs) { _reference.Items.Add(output.Name); _target.Items.Add(output.Name); }
        _reference.SelectedIndex = outputs.Count > 0 ? 0 : -1;
        _target.SelectedIndex = outputs.Count > 1 ? 1 : outputs.Count - 1;
        foreach (int rate in _rates) _fps.Items.Add(rate + " fps");
        _fps.SelectedIndex = 1;
        var value = new TextBlock("0");
        var start = new FluentButton(L.T("开始测试音")) { Enabled = canPlayTest && playTest != null };
        var close = new FluentButton(L.T("关闭")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([
            Ui.Note(L.T("先应用分组并连接，再开始测试音。测试音量最多为 25%，不会开启麦克风。")),
            Ui.Note(L.T("用手机分别靠近两台音箱录像，记录声音相对闪光的偏移。待校准音箱比参考晚多少帧，就填正数；更早则填负数。")),
            new FieldRow(L.T("参考输出"), _reference), new FieldRow(L.T("待校准输出"), _target),
            new FieldRow(L.T("录像帧率"), _fps),
            new FieldRow(L.T("相对偏移帧数"), Ui.Row(6, null, _frames, value)),
            _result, Ui.Row(8, null, start), ButtonRow(close, _apply),
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
        _apply.Click += (_, _) =>
        {
            if (!TryAdjustment(out var adjustment)) return;
            Adjustment = adjustment; DialogResult = DialogResult.OK;
        };
        AcceptButton = _apply; CancelButton = close; UpdateResult();
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
