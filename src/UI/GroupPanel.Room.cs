using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed partial class GroupPanel
{
    private readonly SpeakerRoom _room = new();
    private readonly TextBlock _roomTitle = new("", TextStyle.BodyStrong, wrap: true);
    private readonly TextBlock _roomDescription = Ui.Note();
    private readonly FluentComboBox _roomDevice = new() { AccessibleName = L.T("所选位置音箱"), MinWidth = 180 };
    private readonly FluentSlider _roomDelay = Ui.Slider(-5000, 5000, 1, 10);
    private readonly FluentSlider _roomVolume = Ui.Slider(-100, 100, 1, 5);
    private readonly TextBlock _roomDelayValue = new(""), _roomVolumeValue = new("");
    private List<RoomBinding> _roomBindings = [], _roomChoices = [];
    private readonly List<RoomBinding> _auxRoomBindings = [];
    private string? _roomSelectedId, _measuringDeviceId;
    private bool _roomLoading;
    private sealed record RoomBinding(string Id, string Name, FluentComboBox Position, SpeakerPosition[] Positions,
        FluentSlider Delay, FluentSlider Volume)
    {
        internal SpeakerPosition Current => Positions[Math.Max(0, Position.SelectedIndex)];
        internal RoomOutput Snapshot => new(Id, Name, Current, Delay.Value, Volume.Value, Position.Enabled, Positions);
    }

    private void InitializeRoom()
    {
        _room.PositionSelected += _ => { _roomSelectedId = null; ShowRoomSelection(); };
        _roomDevice.SelectedIndexChanged += (_, _) =>
        {
            if (_roomLoading || _active || _roomDevice.SelectedIndex < 0) return;
            int index = _roomDevice.SelectedIndex - 1;
            if (index < 0)
            {
                // There is no persisted "unassigned output" role. Clearing a directional slot deliberately returns
                // its devices to the existing complete-stereo routing; it never drops a live capture reference.
                if (_room.SelectedPosition == SpeakerPosition.Stereo) { ShowRoomSelection(); return; }
                foreach (var binding in _roomBindings.Where(b => b.Current == _room.SelectedPosition && b.Position.Enabled))
                    binding.Position.SelectedIndex = Array.IndexOf(binding.Positions, SpeakerPosition.Stereo);
                _roomSelectedId = null;
            }
            else if (index < _roomChoices.Count)
            {
                var binding = _roomChoices[index];
                if (!binding.Position.Enabled || !binding.Positions.Contains(_room.SelectedPosition)) return;
                if (_fileSource && _room.SelectedPosition != SpeakerPosition.Stereo && binding.Current != _room.SelectedPosition)
                    foreach (var previous in _roomBindings.Where(b => b != binding && b.Current == _room.SelectedPosition && b.Position.Enabled))
                        previous.Position.SelectedIndex = Array.IndexOf(previous.Positions, SpeakerPosition.Stereo);
                binding.Position.SelectedIndex = Array.IndexOf(binding.Positions, _room.SelectedPosition);
                _roomSelectedId = binding.Id;
            }
            ReadRows(); RefreshRoom();
        };
        _roomDelay.AccessibleName = L.T("所选位置延迟（毫秒）"); _roomDelay.FillOrigin = 0;
        _roomVolume.AccessibleName = L.T("所选位置音量偏移"); _roomVolume.FillOrigin = 0;
        _roomDelay.PreferredLength = _roomVolume.PreferredLength = 160;
        _roomDelay.Scroll += (_, _) => EditSelectedRoomOutput(delay: true);
        _roomVolume.Scroll += (_, _) => EditSelectedRoomOutput(delay: false);
        var fields = Ui.Row(12, null,
            Ui.Stack(2, Ui.Note(L.T("延迟")), Ui.Row(4, null, _roomDelay, _roomDelayValue)),
            Ui.Stack(2, Ui.Note(L.T("音量偏移")), Ui.Row(4, null, _roomVolume, _roomVolumeValue)));
        fields.Wrap = true;
        var detail = Ui.Stack(6, _roomTitle, _roomDescription, _roomDevice, fields);
        var columns = new Columns(_room, detail) { MinColumnWidth = 280, LeftShare = .52, Gap = 16 };
        Controls.Add(Ui.Card(columns));
        Controls.Add(Ui.Note(L.T("点选位置后分配已添加的音箱；方向键切换位置。修改后按应用。虚线位置显示实际混音去向。")));
    }

    private void EditSelectedRoomOutput(bool delay)
    {
        if (_roomLoading || _active) return;
        var binding = _roomBindings.FirstOrDefault(b => b.Id == _roomSelectedId && b.Current == _room.SelectedPosition);
        if (binding == null) return;
        var slider = delay ? binding.Delay : binding.Volume;
        if (!slider.Enabled) return;
        slider.UserSetValue(delay ? _roomDelay.Value : _roomVolume.Value);
        ReadRows(); RefreshRoom();
    }

    private void RefreshRoom()
    {
        if (_roomLoading || _refreshing) return;
        _roomBindings = _rows.Select(row => new RoomBinding(row.Speaker.DeviceId, row.Speaker.Name, row.Position,
            row.Positions, row.Delay, row.Volume)).ToList();
        if (!_fileSource) _roomBindings.AddRange(_auxRoomBindings);
        uint mask = _multichannel ? _fileMask == 0 ? CinemaChannels.Surround71 : _fileMask : CinemaChannels.FL | CinemaChannels.FR;
        _room.Update(mask, _roomBindings.Select(b => b.Snapshot).ToArray(), _measuringDeviceId);
        ShowRoomSelection();
    }

    private void ShowRoomSelection()
    {
        _roomLoading = true;
        try
        {
            var position = _room.SelectedPosition;
            var assigned = _roomBindings.Where(b => b.Current == position).ToArray();
            if (!assigned.Any(b => b.Id == _roomSelectedId)) _roomSelectedId = assigned.FirstOrDefault()?.Id;
            _roomTitle.Text = SpeakerRoomLayout.Name(position);
            uint mask = _multichannel ? _fileMask == 0 ? CinemaChannels.Surround71 : _fileMask : CinemaChannels.FL | CinemaChannels.FR;
            _roomDescription.Text = assigned.Length == 0 ? SpeakerRoomLayout.EmptyText(position, mask, _roomBindings.Select(b => b.Snapshot).ToArray())
                : string.Join(" / ", assigned.Select(b => L.F("{0}，延迟 {1} 毫秒", b.Name, b.Delay.Value)));
            _roomChoices = _roomBindings.Where(b => b.Current == position || b.Position.Enabled && b.Positions.Contains(position)).ToList();
            _roomDevice.Items.Clear();
            _roomDevice.Items.Add(L.T("不分配（保留完整立体声）"));
            foreach (var binding in _roomChoices) _roomDevice.Items.Add(binding.Name);
            _roomDevice.SelectedIndex = _roomSelectedId == null ? 0 : _roomChoices.FindIndex(b => b.Id == _roomSelectedId) + 1;
            _roomDevice.Enabled = !_active && _roomChoices.Any(b => b.Position.Enabled);
            var selected = assigned.FirstOrDefault(b => b.Id == _roomSelectedId);
            _roomDelay.Minimum = _fileSource ? 0 : -5000; _roomDelay.Maximum = _fileSource ? 500 : 5000;
            _roomDelay.Value = selected?.Delay.Value ?? 0;
            _roomVolume.Value = selected?.Volume.Value ?? 0;
            _roomDelayValue.Text = _roomDelay.Value.ToString();
            _roomVolumeValue.Text = _roomVolume.Value.ToString("+0;-0;0");
            _roomDelay.Enabled = selected?.Delay.Enabled == true;
            _roomVolume.Enabled = selected?.Volume.Enabled == true;
        }
        finally { _roomLoading = false; }
    }

    internal void ShowMeasuringSpeaker(string? deviceId)
    {
        if (_measuringDeviceId == deviceId) return;
        _measuringDeviceId = deviceId; RefreshRoom();
    }
}
