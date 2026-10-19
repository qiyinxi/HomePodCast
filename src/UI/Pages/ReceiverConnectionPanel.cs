using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// Explicit per-device connection override; separate from HomePod controls. Only the choice: the receiver's kind, the
/// protocol it is connected with and its processing time are in the speaker info card below it (2026-10-15: a note here
/// repeated the card's 设备类型 above it).
/// </summary>
internal sealed class ReceiverConnectionPanel : StackPanel
{
    private static readonly ReceiverProtocol[] Modes = [ReceiverProtocol.Auto, ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.AirPlay2Buffered, ReceiverProtocol.Raop];
    private readonly TrayApp _app;
    private readonly Func<string?> _deviceId;
    private readonly FluentComboBox _mode = new();

    public ReceiverConnectionPanel(TrayApp app, Func<string?>? deviceId = null)
    {
        _app = app;
        _deviceId = deviceId ?? (() => app.Config.DeviceId);
        _mode.Items.AddRange(Modes.Select(ReceiverProtocols.Label));
        var row = new SettingRow(Glyph.Devices, L.T("连接方式（实验性）"), // experimental since 1.1
            L.T("自动尝试接收端支持的方式；第三方 AirPlay 2 尚未在实机上测试。"), _mode);
        _mode.AccessibleName = L.T("连接方式（实验性）"); // experimental since 1.1
        Controls.Add(row);
        _mode.SelectionChangeCommitted += (_, _) =>
        {
            if (_mode.SelectedIndex < 0 || _deviceId() is not { } id) return;
            _app.SetReceiverProtocol(id, Modes[_mode.SelectedIndex]);
        };
    }

    public void RefreshSelection()
    {
        var id = _deviceId();
        var device = id == null ? null : _app.Controller.ReceiverLookup?.Invoke(id)
            ?? _app.LastScan.FirstOrDefault(d => d.DeviceId == id)
            ?? _app.Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.Device(id);
        // HomePods and Apple TVs connect one way only (ReceiverProtocols.Candidates): nothing to choose.
        Collapsed = device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv;
        if (Collapsed) return;
        var settings = _app.Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id!));
        _mode.SelectedIndex = Array.IndexOf(Modes, settings?.ProtocolOverride ?? ReceiverProtocol.Auto);
    }
}
