using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>One row per output, shared by live capture and local-file playback.</summary>
internal sealed class GroupPanel : StackPanel
{
    private readonly AppConfig _config;
    private readonly string? _tsid;
    private readonly StackPanel _members = Ui.Stack(10);
    private readonly FluentComboBox _add = new() { MinWidth = 200, AccessibleName = L.T("添加音箱") };
    private readonly FluentButton _addButton = new(L.T("添加音箱"));
    private readonly ToggleSwitch _leaderOnly = new(L.T("只连接主音箱（实验）")); // experimental since 1.1; hardware untested
    private readonly TextBlock _state = Ui.Note();
    private readonly TextBlock _singleNote = Ui.Note();
    private readonly TextBlock _fileMemberNote = Ui.Note();
    private readonly List<GroupSpeakerSettings> _speakers;
    private readonly Dictionary<string, SpeakerPosition> _capturePositions;
    private readonly List<Row> _rows = [];
    private List<AirPlayDevice> _found = [], _choices = [];
    private string? _scanSignature;
    private bool _multichannel, _fileSource, _active, _refreshing;
    private uint _fileMask;
    private sealed record Row(GroupSpeakerSettings Speaker, FluentComboBox Position, SpeakerPosition[] Positions, FluentSlider Delay, FluentSlider Volume,
        bool RetainedPosition, bool Silent);

    internal GroupPanel(AppConfig config)
    {
        _config = config;
        _tsid = StereoPairs.TryGetTsid(config.DeviceId, out var tsid) ? tsid : null;
        GroupSettings.Normalize(config);
        _speakers = GroupSettings.Copy(config.GroupSpeakers!);
        if (config.GroupCaptureDeviceIds == null)
            foreach (var speaker in _speakers)
                speaker.VolumeOffset = config.GroupVolumeOffsets?.GetValueOrDefault(GroupPlan.Key(speaker.DeviceId)) ?? 0;
        _capturePositions = new(config.GroupCapturePositions ?? []);
        if (config.GroupCaptureDeviceIds == null && GroupPlan.FromConfig(config) is { Kind: GroupKind.MultiRoom } legacy)
            for (int i = 0; i < legacy.Members.Count; i++)
                _capturePositions[GroupPlan.Key(legacy.Members[i].DeviceId)] = PositionFor(legacy.ChannelsFor(i));
        Gap = 10;
        Controls.Add(new TextBlock(L.T("实验性功能：立体声对和多房间同步还没有在真实设备上验证过，可能不同步、没有声音或连不上。"),
            TextStyle.BodyStrong, TextRole.Caution, wrap: true)); // experimental since 1.1; hardware untested
        Controls.Add(Ui.Note(L.T("实时采集会先混成立体声，只能选择完整立体声、左或右。5.1 / 7.1 文件可以分配中置、低音和环绕位置；未分配的声道混入相邻音箱。")));
        Controls.Add(Ui.Note(L.T("离散声道只支持单独的 HomePod。AirPlay 1 可加入实时分组，校准后基本同步；本地文件仅支持 HomePod，第三方 AirPlay 2 暂不能加入分组。")));
        Controls.Add(_members);
        Controls.Add(_fileMemberNote);
        Controls.Add(Ui.Row(8, _add, _add, _addButton));
        _addButton.Click += (_, _) =>
        {
            if (_active || _add.SelectedIndex < 0 || _add.SelectedIndex >= _choices.Count || _speakers.Count >= 8) return;
            ReadRows();
            var d = _choices[_add.SelectedIndex];
            _speakers.Add(new() { DeviceId = d.DeviceId, Name = d.Name, Host = d.Address.ToString() });
            ShowMembers();
        };
        if (_tsid != null)
        {
            _leaderOnly.Checked = config.GroupPairLeaderOnly;
            Controls.Add(_leaderOnly);
            Controls.Add(Ui.Note(L.T("默认两只音箱各连一路，都收到完整立体声，由这对音箱自己分左右声道，一般不需要电脑端分声道。打开后只连接主音箱，用来实测它会不会转发给另一只。")));
            _leaderOnly.CheckedChanged += (_, _) => { ReadRows(); ShowMembers(); };
        }
        else _leaderOnly.Dispose();
        Controls.Add(Ui.Note(L.T("延迟是这台音箱额外等待的毫秒数，数值越大播放越晚；音量偏移加在共用音量上。")));
        Controls.Add(_singleNote);
        Controls.Add(_state);
        ShowMembers();
    }

    internal static string PositionName(SpeakerPosition position) => position switch
    {
        SpeakerPosition.Stereo => L.T("完整立体声"), SpeakerPosition.Left => L.T("左"), SpeakerPosition.Right => L.T("右"),
        SpeakerPosition.Center => L.T("中置"), SpeakerPosition.Subwoofer => L.T("低音"), SpeakerPosition.RearLeft => L.T("后左"),
        SpeakerPosition.RearRight => L.T("后右"), SpeakerPosition.SideLeft => L.T("侧左"), _ => L.T("侧右"),
    };

    internal void SetFound(List<AirPlayDevice> found)
    {
        if (_active) return;
        string signature = string.Join("|", found.Select(d => $"{d.DeviceId};{d.Name};{d.Address};{d.Kind};{StereoPairs.Tsid(d)};{StereoPairs.GidIndex(d)};{StereoPairs.Flags(d)}"));
        if (signature == _scanSignature) return;
        _scanSignature = signature;
        ReadRows();
        _found = found;
        if (_tsid != null)
        {
            // Legacy split/swap needs the pair's stable mDNS member order; never guess it from a partial scan.
            GroupSettings.Normalize(_config, found);
            foreach (var (key, position) in _config.GroupCapturePositions ?? []) _capturePositions.TryAdd(key, position);
            var pairMembers = StereoPairs.Members(found, _tsid);
            if (_config.GroupCaptureDeviceIds == null && pairMembers.Count == 2 && GroupPlan.FromConfig(_config) is { } legacy)
                foreach (var (member, index) in pairMembers.Select((m, i) => (m, i)))
                    _capturePositions[GroupPlan.Key(member.DeviceId)] = PositionFor(legacy.ChannelsFor(index, member.DeviceId));
            foreach (var saved in _config.GroupSpeakers ?? [])
                if (_speakers.All(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(saved.DeviceId)))
                    _speakers.Add(GroupSettings.Copy([saved])[0]);
        }
        ShowMembers();
    }

    internal void SetSource(bool multichannel, bool fileSource = false, uint mask = 0)
    {
        fileSource |= multichannel;
        if (_multichannel == multichannel && _fileSource == fileSource && _fileMask == mask) return;
        ReadRows(); _multichannel = multichannel; _fileSource = fileSource; _fileMask = mask; ShowMembers();
    }

    internal void SetPlaybackActive(bool active)
    {
        _active = active;
        foreach (Control control in _members.Controls) control.Enabled = !active;
        bool singleCapture = !_fileSource && _speakers.Count <= 1;
        foreach (var row in _rows)
        {
            row.Position.Enabled = !active && !singleCapture && !row.RetainedPosition && !(_tsid != null && _leaderOnly.Checked);
            row.Delay.Enabled = row.Volume.Enabled = !active && !singleCapture && !row.Silent;
        }
        _singleNote.Text = singleCapture ? L.T("实时只推送到一台音箱时使用原来的低延迟模式；位置、额外延迟和音量偏移只在多输出组中生效。") : "";
        _fileMemberNote.Text = _fileSource && _speakers.Any(s => IsLiveMember(s.DeviceId))
            ? L.T("实时组仍在使用的音箱，请在电脑实时声音下移除并应用。") : "";
        _add.Enabled = !active && _tsid == null && _choices.Count > 0;
        _addButton.Enabled = _add.Enabled && _speakers.Count < 8;
        if (_tsid != null) _leaderOnly.Enabled = !active;
    }

    private bool DiscreteAllowed(GroupSpeakerSettings speaker) => _found.Any(d =>
        GroupPlan.Key(d.DeviceId) == GroupPlan.Key(speaker.DeviceId) && d.Kind == ReceiverKind.HomePod && StereoPairs.Tsid(d) == null);

    private string? DiscreteBlockReason(GroupSpeakerSettings speaker)
    {
        if (!_fileSource || !_multichannel || speaker.Position == SpeakerPosition.Stereo || DiscreteAllowed(speaker)) return null;
        return _found.All(d => GroupPlan.Key(d.DeviceId) != GroupPlan.Key(speaker.DeviceId))
            ? L.F("未发现「{0}」，无法确认它是否支持已保存的离散位置。", speaker.Name)
            : L.F("「{0}」当前不是独立的 HomePod，不能使用已保存的离散位置。", speaker.Name);
    }

    private string? ReceiverBlockReason(GroupSpeakerSettings speaker)
    {
        if (!_fileSource && _speakers.Count <= 1) return null; // single-receiver playback keeps its existing protocol path
        var device = _found.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(speaker.DeviceId))
            ?? _config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(speaker.DeviceId))?.Device(speaker.DeviceId);
        if (!_fileSource && device != null && device.Kind is not (ReceiverKind.HomePod or ReceiverKind.AppleTv))
        {
            var forced = _config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(speaker.DeviceId))?.ProtocolOverride ?? ReceiverProtocol.Auto;
            if (forced == ReceiverProtocol.Raop || forced == ReceiverProtocol.Auto
                && device.Kind is ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1) return null;
        }
        return device?.Kind switch
        {
            ReceiverKind.AppleTv when _fileSource => L.T("本地文件分组播放暂不支持 Apple TV 的配对连接；请使用 HomePod。"),
            ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1 when _fileSource => L.T("本地影音文件暂不支持 AirPlay 1 或本机输出分组。"),
            ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1 => L.T("第三方 AirPlay 设备暂不支持多音箱分组。"),
            ReceiverKind.ThirdPartyAirPlay2 or ReceiverKind.Unknown => L.T("第三方 AirPlay 设备暂不支持多音箱分组。"),
            _ => null,
        };
    }

    private bool IsLiveMember(string deviceId)
    {
        string key = GroupPlan.Key(deviceId);
        if (_config.GroupCaptureDeviceIds is { } explicitIds) return explicitIds.Any(id => GroupPlan.Key(id) == key);
        if (_tsid != null) return StereoPairs.Members(_found, _tsid).Any(d => GroupPlan.Key(d.DeviceId) == key);
        return new[] { _config.DeviceId, _config.MultiRoomDeviceId }.Any(id => id != null && GroupPlan.Key(id) == key);
    }

    internal string? BlockReason
    {
        get
        {
            if (_tsid != null)
            {
                int count = StereoPairs.Members(_found, _tsid).Count;
                if (count == 0) return L.T("找不到音箱：请确认它已通电、和电脑在同一网络");
                if (count != 2 && !_leaderOnly.Checked)
                    return L.F("没有同时找到这对音箱的两只（找到 {0} 只）。两只都在线才能连接。", count);
            }
            return _speakers.Select(s => DiscreteBlockReason(s) ?? ReceiverBlockReason(s)).FirstOrDefault(reason => reason != null);
        }
    }

    private void ReadRows()
    {
        if (_refreshing) return;
        foreach (var row in _rows)
        {
            row.Speaker.DelayMs = row.Delay.Value; row.Speaker.VolumeOffset = row.Volume.Value;
        }
    }

    private void ShowMembers()
    {
        if (_refreshing) return;
        _refreshing = true;
        using var layout = LayoutPanel.BatchRelayout(this);
        foreach (Control old in _members.Controls.Cast<Control>().ToArray()) old.Dispose();
        _rows.Clear();
        foreach (var speaker in _speakers)
        {
            var device = _found.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(speaker.DeviceId));
            if (device != null) { speaker.Name = device.Name; speaker.Host = device.Address.ToString(); }
            bool retainedPosition = _fileSource && (DiscreteBlockReason(speaker) != null || !_multichannel && !GroupSettings.IsStereoPosition(speaker.Position));
            bool silent = retainedPosition && !_multichannel && _fileMask != 0 && !(speaker.Position == SpeakerPosition.Center && (_fileMask & CinemaChannels.FC) != 0);
            var positions = retainedPosition ? new[] { speaker.Position } : _multichannel && DiscreteAllowed(speaker) ? Enum.GetValues<SpeakerPosition>()
                : _multichannel ? [SpeakerPosition.Stereo] : new[] { SpeakerPosition.Stereo, SpeakerPosition.Left, SpeakerPosition.Right };
            var position = new FluentComboBox { AccessibleName = L.F("{0} 位置", speaker.Name), MinWidth = 120 };
            foreach (var p in positions) position.Items.Add(PositionName(p));
            var selectedPosition = _fileSource ? speaker.Position : _capturePositions.GetValueOrDefault(GroupPlan.Key(speaker.DeviceId));
            position.SelectedIndex = Math.Max(0, Array.IndexOf(positions, selectedPosition));
            position.SelectedIndexChanged += (_, _) =>
            {
                if (position.SelectedIndex < 0) return;
                if (_fileSource) speaker.Position = positions[position.SelectedIndex];
                else _capturePositions[GroupPlan.Key(speaker.DeviceId)] = positions[position.SelectedIndex];
            };
            var delay = Ui.Slider(0, 500, 1, 10);
            delay.PreferredLength = 120; delay.Value = speaker.DelayMs; delay.AccessibleName = L.F("{0} 延迟（毫秒）", speaker.Name);
            var delayValue = new TextBlock(delay.Value.ToString());
            delay.ValueChanged += (_, _) => delayValue.Text = delay.Value.ToString();
            var volume = Ui.Slider(-30, 30, 1, 5);
            volume.PreferredLength = 120; volume.FillOrigin = 0; volume.Value = speaker.VolumeOffset;
            volume.AccessibleName = L.F("{0} 音量偏移", speaker.Name);
            var volumeValue = new TextBlock(volume.Value.ToString("+0;-0;0"));
            volume.ValueChanged += (_, _) => volumeValue.Text = volume.Value.ToString("+0;-0;0");
            var remove = new FluentButton(L.T("移除")) { AccessibleName = L.F("移除 {0}", speaker.Name) };
            bool liveReference = _fileSource && IsLiveMember(speaker.DeviceId);
            remove.Enabled = _tsid == null && !liveReference && GroupPlan.Key(speaker.DeviceId) != GroupPlan.Key(_config.DeviceId ?? "");
            remove.Click += (_, _) =>
            {
                if (_active || _fileSource && IsLiveMember(speaker.DeviceId)) return;
                ReadRows(); _speakers.Remove(speaker); ShowMembers();
            };
            var title = new TextBlock(device == null ? L.F("{0}（未发现）", speaker.Name) : speaker.Name, TextStyle.BodyStrong, wrap: true);
            var positionField = Ui.Stack(2, new TextBlock(L.T("音箱位置"), TextStyle.Caption, TextRole.Secondary), position);
            var delayField = Ui.Stack(2, new TextBlock(L.T("延迟"), TextStyle.Caption, TextRole.Secondary), Ui.Row(4, null, delay, delayValue));
            var volumeField = Ui.Stack(2, new TextBlock(L.T("音量偏移"), TextStyle.Caption, TextRole.Secondary), Ui.Row(4, null, volume, volumeValue));
            var fields = Ui.Row(8, null, positionField, delayField, volumeField, remove);
            fields.Wrap = true;
            var entry = Ui.Stack(4, title, fields);
            if (silent) entry.Controls.Add(Ui.Note(L.T("此音源没有这个声道，此音箱保持静音。切换回多声道文件会恢复原位置。")));
            if ((DiscreteBlockReason(speaker) ?? ReceiverBlockReason(speaker)) is { } blocked) entry.Controls.Add(Ui.Note(blocked));
            if (_multichannel && !DiscreteAllowed(speaker)) entry.Controls.Add(Ui.Note(L.T("此接收端只能播放完整立体声；离散声道需要未组成立体声对的 HomePod。")));
            _members.Controls.Add(entry);
            _rows.Add(new(speaker, position, positions, delay, volume, retainedPosition, silent));
        }
        _choices = StereoPairs.Merge(_found).Where(d => !StereoPairs.IsPairId(d.DeviceId) &&
            _speakers.All(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(d.DeviceId))).ToList();
        _add.Items.Clear(); foreach (var d in _choices) _add.Items.Add(d.Name);
        _add.SelectedIndex = _choices.Count > 0 ? 0 : -1;
        _state.Text = _tsid != null && _leaderOnly.Checked ? LeaderOnlyState()
            : _tsid != null && StereoPairs.Members(_found, _tsid).Count != 2 ? L.F("没有同时找到这对音箱的两只（找到 {0} 只）。两只都在线才能连接。", StereoPairs.Members(_found, _tsid).Count)
            : _speakers.Count == 0 ? L.T("请先在主界面选择音箱。") : "";
        _refreshing = false;
        SetPlaybackActive(_active);
    }

    private string LeaderOnlyState()
    {
        var pair = StereoPairs.Members(_found, _tsid!);
        if (pair.Count == 0) return L.T("找不到音箱：请确认它已通电、和电脑在同一网络");
        return StereoPairs.Leader(pair) is { } leader ? L.F("只连接主音箱「{0}」。另一只会不会一起响，需要实测。", leader.Name)
            : L.F("这对音箱都没有标出主音箱，将只连接「{0}」。", pair[0].Name);
    }

    internal CinemaSpeaker[] SelectedLayout()
    {
        ReadRows();
        var rows = _rows.AsEnumerable();
        if (_tsid != null && _leaderOnly.Checked)
        {
            var pair = StereoPairs.Members(_found, _tsid);
            var leader = StereoPairs.Leader(pair) ?? pair.FirstOrDefault();
            rows = rows.Where(r => leader != null && GroupPlan.Key(r.Speaker.DeviceId) == GroupPlan.Key(leader.DeviceId));
        }
        return rows.Select(r => new CinemaSpeaker { DeviceId = r.Speaker.DeviceId, Name = r.Speaker.Name, Host = r.Speaker.Host,
            Role = GroupSettings.ToCinema(r.Positions[r.Position.SelectedIndex]), DelayMs = r.Speaker.DelayMs, VolumeOffset = r.Speaker.VolumeOffset }).ToArray();
    }

    internal void ApplyDelays(IReadOnlyList<CinemaSpeaker> speakers)
    {
        foreach (var row in _rows)
            if (speakers.FirstOrDefault(s => GroupPlan.Key(s.DeviceId) == GroupPlan.Key(row.Speaker.DeviceId)) is { } measured)
                row.Delay.Value = measured.DelayMs;
        ReadRows(); Apply(activateCapture: false);
    }

    internal void Apply(bool activateCapture = true)
    {
        ReadRows();
        _config.GroupSpeakers = GroupSettings.Copy(_speakers);
        _config.GroupVolumeOffsets = _speakers.Where(s => s.VolumeOffset != 0).ToDictionary(s => GroupPlan.Key(s.DeviceId), s => s.VolumeOffset);
        if (!activateCapture) return;
        _config.GroupSplitChannels = _config.GroupSwapChannels = false;
        _config.GroupCaptureDeviceIds = _speakers.Select(s => s.DeviceId).ToList();
        _config.GroupCapturePositions = new(_capturePositions);
        if (_tsid != null) _config.GroupPairLeaderOnly = _leaderOnly.Checked;
        else
        {
            var extra = _speakers.FirstOrDefault(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(_config.DeviceId ?? ""));
            _config.MultiRoomDeviceId = extra?.DeviceId; _config.MultiRoomDeviceName = extra?.Name; _config.MultiRoomHost = extra?.Host;
        }
    }

    private static SpeakerPosition PositionFor(ChannelMode mode) => mode switch
    {
        ChannelMode.LeftOnly => SpeakerPosition.Left, ChannelMode.RightOnly => SpeakerPosition.Right, _ => SpeakerPosition.Stereo,
    };

    internal static string SettingsSignature(AppConfig cfg) => System.Text.Json.JsonSerializer.Serialize(new
    {
        cfg.DeviceId, cfg.MultiRoomDeviceId, cfg.GroupPairLeaderOnly, cfg.GroupSplitChannels, cfg.GroupSwapChannels,
        cfg.GroupVolumeOffsets, cfg.GroupSpeakers, cfg.GroupCaptureDeviceIds, cfg.GroupCapturePositions,
    });

    internal static string PlayLayout(AppConfig cfg)
    {
        if (GroupPlan.FromConfig(cfg) is not { } plan) return "single";
        var members = plan.Members.Select((m, i) => $"{GroupPlan.Key(m.DeviceId)}:{plan.ChannelsFor(i, m.DeviceId)}:{plan.DelayTrimFor(m.DeviceId)}:{plan.VolumeOffsetFor(m.DeviceId)}");
        string pairSettings = plan.Kind != GroupKind.StereoPair ? "" : string.Join(",", plan.SpeakerSettings.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{kv.Value.Position}:{kv.Value.DelayMs}:{plan.VolumeOffsetFor(kv.Key)}"));
        return $"{plan.Kind}|{plan.PairTsid}|{plan.PairLeaderOnly}|{plan.SplitChannels}|{plan.SwapChannels}|{string.Join(",", members)}|{pairSettings}";
    }
}

/// <summary>Existing callers share the same speaker editor.</summary>
internal sealed class GroupForm : FluentDialog
{
    private readonly GroupPanel _editor;
    internal GroupForm(AppConfig config) : base(L.T("多音箱（实验性）")) // experimental since 1.1; hardware untested
    {
        ContentWidth = 760;
        _editor = new GroupPanel(config);
        var ok = new FluentButton(L.T("确定"), ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new FluentButton(L.T("取消")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([_editor, ButtonRow(ok, cancel)]);
        ok.Click += (_, _) => Apply(); AcceptButton = ok; CancelButton = cancel;
    }
    internal void SetFound(List<AirPlayDevice> found) => _editor.SetFound(found);
    internal void Apply() => _editor.Apply();
    internal static string PlayLayout(AppConfig config) => GroupPanel.PlayLayout(config);
    public static void ShowFor(TrayApp app, IWin32Window owner)
    {
        using var form = new GroupForm(app.Config);
        form.SetFound(app.LastScan.ToList()); string before = PlayLayout(app.Config);
        if (form.ShowDialog(owner) == DialogResult.OK) Save(app, before);
    }
    internal static void Save(TrayApp app, string before)
    {
        app.Config.Save();
        if (PlayLayout(app.Config) != before && (app.Controller.State != StreamState.Idle || app.Controller.WaitingForSpeaker))
            app.Connect(ConnectTrigger.Reconnect);
    }
}
