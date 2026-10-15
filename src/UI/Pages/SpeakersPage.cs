using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Inspecting a device never changes the playback selection.</summary>
internal sealed class SpeakersPage : ScrollPage
{
    private readonly TrayApp _app;
    private readonly SpeakerInfoPanel _speakerInfo;
    private readonly ReceiverConnectionPanel _receiverConnection;
    private readonly PairingsPanel _pairings;
    private readonly StackPanel _list = new() { Gap = 4 };
    private List<SelectedSpeaker> _devices = [];
    private string? _selected, _signature;
    private bool _shown;

    public SpeakersPage(TrayApp app) : base(L.T("音箱信息"))
    {
        _app = app;
        _speakerInfo = new SpeakerInfoPanel(app.SpeakerInfo, Inspected,
            () => app.Controller.State == StreamState.Streaming && app.SelectedSpeakers().Any(s => s.DeviceId == _selected),
            () => app.Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(_selected ?? ""))?.ArrivalToRenderMs
                ?? (app.SelectedSpeakers().Any(s => s.DeviceId == _selected) ? app.Config.ArrivalToRenderMs : null), () => app.LastScan);
        _receiverConnection = new ReceiverConnectionPanel(app, () => _selected);
        var detail = Ui.Stack(12, _receiverConnection, _speakerInfo);
        Content.Controls.Add(new Browser(_list, detail));
        var add = new FluentButton(L.T("添加"));
        Content.Controls.Add(new SettingRow(Glyph.Devices, L.T("手动添加 AirPlay 设备（实验性）"), // experimental since 1.1
            L.T("发现不到设备时，填写 IP 地址和端口。"), add));
        add.Click += (_, _) => { app.AddManualAirPlayDevice(FindForm()); RefreshDevices(); };
        var paired = Ui.Section(L.T("已配对的电视"));
        _pairings = new PairingsPanel(app, paired);
        Content.Controls.AddRange([paired, _pairings]);
    }

    private List<SelectedSpeaker> Inspected() => _devices.Where(d => d.DeviceId == _selected).ToList();

    public void RefreshDevices()
    {
        if (!_shown) return;
        var devices = _app.SelectedSpeakers().Concat(_app.LastScan.Select(d => new SelectedSpeaker(d.DeviceId, d.Name, d.Address, d.Txt, d.Port)
            { Model = d.Model, HasAirPlay = d.HasAirPlay, RaopPort = d.RaopPort }))
            .GroupBy(d => GroupPlan.Key(d.DeviceId)).Select(g => g.First()).ToList();
        string signature = string.Join(";", devices.Select(d => $"{d.DeviceId}|{d.Name}|{d.Address}|{d.Port}"));
        if (signature != _signature)
        {
            using var layout = LayoutPanel.BatchLayout(this);
            _devices = devices;
            if (!_devices.Any(d => d.DeviceId == _selected)) _selected = _devices.FirstOrDefault()?.DeviceId;
            foreach (Control old in _list.Controls.Cast<Control>().ToList()) old.Dispose();
            foreach (var device in devices)
            {
                var choose = new FluentButton(device.Name, ButtonKind.Subtle, Glyph.Speaker)
                { AccessibleName = device.Name, AccessibleDescription = device.Address?.ToString() };
                choose.Click += (_, _) => Inspect(device.DeviceId);
                _list.Controls.Add(choose);
            }
            _signature = signature;
        }
        _receiverConnection.RefreshSelection();
        _speakerInfo.SelectionChanged();
    }

    internal void Inspect(string id)
    {
        if (_devices.All(d => d.DeviceId != id) || id == _selected) return;
        using var layout = LayoutPanel.BatchLayout(this);
        _selected = id;
        _receiverConnection.RefreshSelection();
        _speakerInfo.SelectionChanged();
    }

    public override void PageShown()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        _shown = true;
        RefreshDevices();
        _speakerInfo.PageShown();
        _pairings.PageShown();
    }
    public override void PageHidden()
    {
        _shown = false;
        _speakerInfo.PageHidden();
        _pairings.PageHidden();
    }

    private sealed class Browser : LayoutPanel
    {
        private readonly Panel _scroll = new() { AutoScroll = true };
        private readonly StackPanel _list, _detail;
        public Browser(StackPanel list, StackPanel detail)
        {
            (_list, _detail) = (list, detail);
            _scroll.Controls.Add(list);
            Controls.AddRange([_scroll, detail]);
        }
        protected override int Arrange(int width, bool apply)
        {
            bool columns = width >= Dp(720);
            int listWidth = columns ? Dp(200) : width;
            int listHeight = Math.Min(Dp(columns ? 420 : 160), HeightFor(_list, Math.Max(0, listWidth - Dp(20))));
            int detailWidth = columns ? width - listWidth - Dp(16) : width;
            int detailHeight = HeightFor(_detail, detailWidth);
            int height = columns ? Math.Max(listHeight, detailHeight) : listHeight + Dp(12) + detailHeight;
            if (apply)
            {
                _scroll.BackColor = Theme.P.Window;
                _scroll.SetBounds(0, 0, listWidth, listHeight);
                Place(_list, 0, _scroll.AutoScrollPosition.Y, Math.Max(0, listWidth - Dp(20)), HeightFor(_list, listWidth - Dp(20)));
                Place(_detail, columns ? listWidth + Dp(16) : 0, columns ? 0 : listHeight + Dp(12), detailWidth, detailHeight);
            }
            return height;
        }
    }
}
