using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Independent controls for receivers whose latency is not a HomePod scene.</summary>
internal sealed class ReceiverLatencyPanel : StackPanel
{
    private readonly TrayApp _app;
    private readonly TextBlock _value = new("", TextStyle.Display);
    private readonly TextBlock _protocol = Ui.Note(), _hint = Ui.Note(), _warning = Ui.Note(), _lag = Ui.Note();
    private readonly FluentComboBox _compatibility = new() { MinWidth = 160 };
    private readonly FluentSlider _realtime = Ui.Slider(100, 500, 1, 10);
    private readonly SettingRow _choice;
    private readonly TimerDebounce _wait = new(1000);
    private string? _device, _pendingDevice;
    private bool _loading;
    public event Action? LatencyChanged;

    public ReceiverLatencyPanel(TrayApp app)
    {
        _app = app;
        Gap = 10;
        _compatibility.Items.AddRange(ReceiverLatencies.CompatibilityChoices.Select(ms => L.F("{0:F1} 秒", ms / 1000.0)));
        _compatibility.AccessibleName = L.T("兼容模式延迟（实验性）"); // experimental since 1.1
        _choice = new SettingRow(Glyph.Sync, L.T("兼容模式延迟（实验性）"), "", _compatibility); // experimental since 1.1
        _realtime.AccessibleName = L.T("设备延迟（实验性）"); // experimental since 1.1
        Controls.AddRange([_protocol, _value, _choice, _realtime, _hint, _warning, _lag]);
        _compatibility.SelectionChangeCommitted += (_, _) =>
        {
            if (!_loading && _compatibility.SelectedIndex >= 0)
            {
                _app.SetReceiverLatency(ReceiverLatencies.CompatibilityChoices[_compatibility.SelectedIndex]);
                LatencyChanged?.Invoke();
            }
        };
        _realtime.Scroll += (_, _) =>
        {
            if (_loading) return;
            _pendingDevice = _device; _wait.Restart();
            _value.Text = $"{_realtime.Value} ms";
            ShowWarning(_realtime.Value);
        };
        _realtime.Released += (_, _) => { if (_pendingDevice != null) _wait.Restart(); };
        _wait.Tick += () =>
        {
            if (_realtime.IsDragging) return;
            _wait.Stop();
            if (_pendingDevice == _app.Config.DeviceId)
            {
                _app.SetReceiverLatency(_realtime.Value); LatencyChanged?.Invoke();
            }
            _pendingDevice = null;
        };
    }

    public void Refresh(ReceiverLatencySelection? selection, string lag)
    {
        Collapsed = selection == null;
        if (selection == null) { _wait.Stop(); _pendingDevice = null; return; }
        _loading = true;
        if (_device != selection.Device.DeviceId) { _wait.Stop(); _pendingDevice = null; }
        _device = selection.Device.DeviceId;
        _protocol.Text = ReceiverKinds.Label(selection.Device.Kind) + " · " + ReceiverProtocols.Label(selection.Protocol);
        if (_pendingDevice == null || selection.Compatibility)
        {
            _value.Text = $"{selection.Value} ms";
            _realtime.Floor = Math.Min(500, selection.Policy.MinimumMs);
            _realtime.Value = Math.Clamp(selection.Value, 100, 500);
        }
        _choice.Collapsed = !selection.Compatibility;
        _realtime.Collapsed = selection.Compatibility;
        _realtime.Enabled = selection.Policy.MinimumMs <= selection.Policy.MaximumMs;
        _compatibility.SelectedIndex = Array.IndexOf(ReceiverLatencies.CompatibilityChoices, selection.Value);
        _hint.Text = selection.Compatibility
            ? L.T("兼容模式：适合听音乐，不适合打游戏；看视频时画面可能对不上。画面延迟最多为 2000 ms。")
            : selection.Policy.MinimumMs > selection.Policy.MaximumMs
                ? L.T("这台设备的处理时间超过低延迟范围，请使用兼容或缓冲连接。")
                : L.F("建议最低 {0} ms；默认 {1} ms", selection.Policy.RecommendedMinimumMs, selection.Policy.DefaultMs);
        _lag.Text = lag;
        ShowWarning(_pendingDevice != null ? _realtime.Value : selection.Value);
        _loading = false;
    }

    private void ShowWarning(int milliseconds)
    {
        bool warning = _app.ReceiverSelection?.Policy.Warns(milliseconds) == true;
        _warning.Collapsed = !warning;
        _warning.Role = TextRole.Caution;
        _warning.Text = warning ? L.T("低于推荐最低延迟，可能断音") : "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _wait.Dispose();
        base.Dispose(disposing);
    }
}
