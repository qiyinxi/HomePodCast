using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Explicit per-device connection override; separate from HomePod controls.</summary>
internal sealed class ReceiverConnectionPanel : StackPanel
{
    private static readonly ReceiverProtocol[] Modes = [ReceiverProtocol.Auto, ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.AirPlay2Buffered, ReceiverProtocol.Raop];
    private readonly TrayApp _app;
    private readonly FluentComboBox _mode = new();
    private readonly SettingRow _row;
    private readonly TextBlock _details = Ui.Note();

    public ReceiverConnectionPanel(TrayApp app)
    {
        _app = app;
        _mode.Items.AddRange(Modes.Select(ReceiverProtocols.Label));
        _row = new SettingRow(Glyph.Devices, L.T("连接方式（实验性）"), // experimental since 1.1
            L.T("自动尝试接收端支持的方式；第三方 AirPlay 2 尚未在实机上测试。"), _mode);
        _mode.AccessibleName = L.T("连接方式（实验性）"); // experimental since 1.1
        Controls.AddRange([_row, _details]);
        _mode.SelectionChangeCommitted += (_, _) =>
        {
            if (_mode.SelectedIndex < 0 || _app.Config.DeviceId is not { } id) return;
            _app.SetReceiverProtocol(id, Modes[_mode.SelectedIndex]);
        };
    }

    public void RefreshSelection()
    {
        var id = _app.Config.DeviceId;
        var device = id == null ? null : _app.Controller.ReceiverLookup?.Invoke(id);
        Collapsed = device == null || device.Kind == ReceiverKind.HomePod;
        if (Collapsed) return;
        _row.Collapsed = device!.Kind == ReceiverKind.AppleTv;
        var settings = _app.Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id!));
        var actual = _app.Controller.ActiveProtocol;
        string connection = _app.Controller.State == StreamState.Streaming && actual != null
            ? L.F("实际连接：{0}", ReceiverProtocols.Label(actual.Value))
            : L.T("未连接；连接后显示实际协议。");
        int? processing = _app.Controller.ReceiverSession?.ArrivalToRenderMs ?? _app.Controller.Client?.ArrivalToRenderMs ?? settings?.ArrivalToRenderMs;
        _details.Text = ReceiverKinds.Label(device.Kind) + "\n" + connection
            + (processing is { } ms ? "\n" + L.F("接收端处理时间：{0} ms", ms) : "");
        _mode.SelectedIndex = Array.IndexOf(Modes, settings?.ProtocolOverride ?? ReceiverProtocol.Auto);
    }
}
