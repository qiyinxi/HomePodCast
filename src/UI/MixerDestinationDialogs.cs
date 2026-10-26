using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed class MixerDestinationDelayDialog : FluentDialog
{
    private readonly FluentComboBox _scene = new() { MinWidth = 200, AccessibleName = L.T("场景") };
    private readonly NumericUpDown _latency = new() { Minimum = 0, Maximum = 30000, AccessibleName = L.T("延迟"), Width = 180 };
    private readonly MixerDestinationSettings _previous;
    private readonly Net.ReceiverLatencySelection? _receiver;
    internal MixerDestinationSettings Settings => _previous with { Scene = Scenes.All[_scene.SelectedIndex], LatencyMs =
        _receiver?.Compatibility == true ? Net.ReceiverLatencies.CompatibilityChoices.First(ms => ms >= (int)_latency.Value) : (int)_latency.Value };
    internal MixerDestinationDelayDialog(MixerDestinationStatus status, MixerDestinationSettings? previous = null,
        Net.ReceiverLatencySelection? receiver = null) : base(L.T("调整延迟"))
    {
        ContentWidth = 590;
        _previous = previous ?? new(status.Scene, status.RequestedLatencyMs);
        _receiver = receiver;
        foreach (var scene in Scenes.All) _scene.Items.Add(Scenes.Name(scene));
        _scene.SelectedIndex = Array.IndexOf(Scenes.All, status.Scene); _latency.Value = Math.Clamp(status.RequestedLatencyMs, 0, 30000);
        _latency.BackColor = Theme.P.Control; _latency.ForeColor = Theme.P.Text;
        if (receiver != null)
        {
            _latency.Maximum = receiver.Policy.MaximumMs; _latency.Minimum = Math.Min(receiver.Policy.MinimumMs, receiver.Policy.MaximumMs);
            _latency.Value = Math.Clamp(receiver.Value, (int)_latency.Minimum, (int)_latency.Maximum);
            if (receiver.Compatibility) _latency.Increment = 500;
        }
        var note = Ui.Note(L.F("请求 {0} ms · 实际 {1} ms", status.RequestedLatencyMs, status.EffectiveLatencyMs));
        var routing = Ui.Note(status.CaptureExtraMs > 0 ? L.F("按程序采集额外延迟约 {0} ms。", Audio.RoutedCapture.RoutedExtraLatencyMs) : "");
        var ok = new FluentButton(L.T("确定"), ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new FluentButton(L.T("取消")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([new TextBlock(status.Name, TextStyle.BodyStrong, wrap: true), new FieldRow(L.T("场景"), _scene),
            new FieldRow(L.T("延迟"), _latency), note, routing, ButtonRow(cancel, ok)]);
        _scene.SelectionChangeCommitted += (_, _) => _latency.Value = Math.Clamp(Scenes.LatencyMs(Scenes.All[_scene.SelectedIndex], (int)_latency.Value), (int)_latency.Minimum, (int)_latency.Maximum);
        if (receiver?.Policy.MinimumMs > receiver?.Policy.MaximumMs) ok.Enabled = false;
        AcceptButton = ok; CancelButton = cancel;
    }
}

internal sealed class MixerDeviceIdentityDialog : FluentDialog
{
    private readonly List<(string Id, FluentComboBox Choice, int Initial, string? Previous)> _rows = [];
    private readonly AppConfig _config;
    internal Dictionary<string, string> Identities { get; private set; }
    internal MixerDeviceIdentityDialog(AppConfig config) : base(L.T("确认设备身份"))
    {
        _config = config; Identities = new(config.MixerDeviceIdentities); ContentWidth = 650;
        var scroll = new IdentityList();
        var outputs = new List<(string Id, string Name)>();
        if (config.DeviceId != null && !Net.StereoPairs.IsPairId(config.DeviceId)) outputs.Add(("airplay:" + config.DeviceId, config.DeviceName ?? config.DeviceId));
        outputs.AddRange((config.GroupSpeakers ?? []).Select(s => ("airplay:" + s.DeviceId, s.Name)));
        outputs.AddRange(config.ManualDevices.Select(s => ("airplay:" + s.DeviceId, s.Name)));
        outputs.AddRange(config.GroupDlnaOutputs.Select(s => ("dlna:" + s.Udn, s.Name + " · DLNA")));
        outputs.AddRange(config.GroupLocalOutputs.Select(s => ("local:" + s.DeviceId, s.Name + " · " + L.T("本机输出"))));
        outputs = outputs.DistinctBy(o => MixerDeviceIdentities.Canonical(o.Id)).ToList();
        for (int i = 0; i < outputs.Count; i++)
        {
            var output = outputs[i]; string id = MixerDeviceIdentities.Canonical(output.Id);
            string? previous = config.MixerDeviceIdentities.GetValueOrDefault(id);
            var combo = new FluentComboBox { PreferredWidth = 260, AccessibleName = output.Name, Placeholder = L.T("选择身份…") };
            combo.Items.Add(L.T("独立设备"));
            for (int p = 0; p < i; p++) combo.Items.Add(L.F("与「{0}」是同一台设备", outputs[p].Name));
            int same = previous == null ? -1 : _rows.FindIndex(r => string.Equals(r.Previous, previous, StringComparison.OrdinalIgnoreCase));
            int initial = previous == null ? -1 : same < 0 ? 0 : same + 1;
            combo.SelectedIndex = initial;
            int own = i;
            combo.IsItemEnabled = value => value == 0 || value - 1 < own && _rows[value - 1].Choice.SelectedIndex >= 0;
            _rows.Add((id, combo, initial, previous));
            scroll.Content.Controls.Add(new SettingRow(Glyph.Speaker, output.Name, id, combo));
        }
        var save = new FluentButton(L.T("保存"), ButtonKind.Primary);
        var cancel = new FluentButton(L.T("取消")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([Ui.Note(L.T("同一台音箱的不同协议条目应选同一个身份；无法确认时请取消。")), scroll, ButtonRow(cancel, save)]);
        save.Click += (_, _) =>
        {
            var next = new Dictionary<string, string>(_config.MixerDeviceIdentities);
            foreach (var row in _rows)
            {
                int selected = row.Choice.SelectedIndex;
                if (selected < 0) continue;
                if (selected == 0) next[row.Id] = row.Initial == 0 && row.Previous != null ? row.Previous : Guid.NewGuid().ToString("N");
                else if (next.TryGetValue(_rows[selected - 1].Id, out var identity)) next[row.Id] = identity;
            }
            Identities = next; DialogResult = DialogResult.OK;
        };
        AcceptButton = save; CancelButton = cancel;
    }
    private sealed class IdentityList : ScrollPage
    { public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Theme.Dp(280, DeviceDpi)); }
}
