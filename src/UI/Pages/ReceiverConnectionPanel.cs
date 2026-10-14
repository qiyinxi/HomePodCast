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

    public ReceiverConnectionPanel(TrayApp app)
    {
        _app = app;
        _mode.Items.AddRange(Modes.Select(ReceiverProtocols.Label));
        _row = new SettingRow(Glyph.Devices, L.T("连接方式（实验性）"), // experimental since 1.1
            L.T("自动尝试接收端支持的方式；第三方 AirPlay 2 尚未在实机上测试。"), _mode);
        _mode.AccessibleName = L.T("连接方式（实验性）");
        Controls.Add(_row);
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
        Collapsed = device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv;
        if (Collapsed) return;
        var settings = _app.Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id!));
        _mode.SelectedIndex = Array.IndexOf(Modes, settings?.ProtocolOverride ?? ReceiverProtocol.Auto);
    }
}
