using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed partial class GroupPanel
{
    private readonly StackPanel _auxiliaryMembers = Ui.Stack(10);
    private readonly StackPanel _timingControls = Ui.Stack(4);
    private readonly TextBlock _alignmentTier = Ui.Note();
    private readonly TextBlock _outputHint = Ui.Note();
    private readonly TextBlock _actualGroupDelay = Ui.Note();
    private readonly FluentComboBox _outputType = new() { AccessibleName = L.T("输出类型"), MinWidth = 120 };
    private readonly FluentComboBox _delayMode = new() { AccessibleName = L.T("整组延迟"), MinWidth = 120 };
    private readonly FluentSlider _groupDelay = Ui.Slider(100, 30000, 10, 100);
    private readonly FluentButton _refreshOutputs = new(L.T("刷新输出"));
    private readonly FluentButton _clearOutputs = new(L.T("清空选择"), ButtonKind.Subtle);
    private readonly List<Action> _readAuxiliaryRows = [];
    private readonly List<Action> _addActions = [];
    private readonly Dictionary<string, (TextBlock Text, bool Found)> _statusRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _outputStop = new();
    private List<LocalOutputOptions> _localBank = [];
    private List<DlnaOutputSettings> _dlnaBank = [];
    private HashSet<string> _localSelection = new(StringComparer.OrdinalIgnoreCase), _dlnaSelection = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _captureTrims = [];
    private Dictionary<string, bool> _requiredOutputs = [];
    private IReadOnlyList<AudioEndpoint> _localFound = [];
    private IReadOnlyList<DlnaDevice> _dlnaFound = [];
    private bool _scanningOutputs;
    internal Func<IReadOnlyList<AudioEndpoint>> ListLocalOutputs { get; set; } = AudioEndpoints.Outputs;
    internal Func<CancellationToken, Task<IReadOnlyList<DlnaDevice>>> BrowseDlnaOutputs { get; set; } =
        ct => DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), ct);

    private IEnumerable<GroupSpeakerSettings> LiveAirPlayMembers()
    {
        if (_tsid == null || !_leaderOnly.Checked) return _speakers;
        var pair = StereoPairs.Members(_found, _tsid);
        var leader = StereoPairs.Leader(pair) ?? pair.FirstOrDefault();
        return leader == null ? _speakers.Take(1)
            : _speakers.Where(s => GroupPlan.Key(s.DeviceId) == GroupPlan.Key(leader.DeviceId));
    }

    internal int LiveOutputCount => LiveAirPlayMembers().Count() + _localBank.Count(o => _localSelection.Contains(LocalGroupSettings.Key(o.DeviceId)))
        + _dlnaBank.Count(o => _dlnaSelection.Contains(DlnaGroupSettings.Key(o.Udn)));

    private void InitializeOutputs()
    {
        _localBank = LocalGroupSettings.Copy(_config.GroupLocalOutputs);
        _dlnaBank = DlnaGroupSettings.Copy(_config.GroupDlnaOutputs);
        _localSelection = new((_config.GroupCaptureLocalDeviceIds ?? []).Select(LocalGroupSettings.Key), StringComparer.OrdinalIgnoreCase);
        _dlnaSelection = new((_config.GroupCaptureDlnaUdns ?? []).Select(DlnaGroupSettings.Key), StringComparer.OrdinalIgnoreCase);
        _captureTrims = new(_config.GroupCaptureDelayTrims);
        _requiredOutputs = new(_config.GroupRequiredOutputs);
        _outputType.Items.AddRange(["AirPlay", L.T("蓝牙／本机输出"), "DLNA"]);
        _outputType.SelectedIndex = 0;
        _outputType.SelectedIndexChanged += (_, _) => { ReadRows(); RefreshOutputChoices(); UpdateOutputControls(); };
        _refreshOutputs.AccessibleName = L.T("刷新输出");
        _refreshOutputs.Click += async (_, _) => await RefreshOutputsAsync();
        _clearOutputs.Click += (_, _) =>
        {
            if (_active || _fileSource) return;
            ReadRows(); _speakers.Clear(); _tsid = null; _leaderOnly.Collapsed = true;
            _localSelection.Clear(); _dlnaSelection.Clear(); ShowMembers();
        };
        _delayMode.Items.AddRange([L.T("自动"), L.T("手动")]);
        _delayMode.SelectedIndex = _config.GroupDelayMs == null ? 0 : 1;
        _groupDelay.Value = _config.GroupDelayMs ?? 2000;
        _groupDelay.AccessibleName = L.T("整组延迟（毫秒）");
        _groupDelay.PreferredLength = 200;
        var value = new TextBlock(_groupDelay.Value.ToString());
        _groupDelay.ValueChanged += (_, _) => value.Text = _groupDelay.Value.ToString();
        _delayMode.SelectedIndexChanged += (_, _) => UpdateOutputControls();
        _timingControls.Controls.Add(new FieldRow(L.T("整组延迟"), Ui.Row(6, null, _delayMode, _groupDelay, value)));
        _timingControls.Controls.Add(_actualGroupDelay);
        _actualGroupDelay.Text = L.T("自动按最慢的输出确定；手动值低于设备下限时仍使用下限。");
        _alignmentTier.AccessibleName = L.T("同步程度");
    }

    internal void SetAvailableOutputs(IReadOnlyList<AudioEndpoint> local, IReadOnlyList<DlnaDevice> dlna)
    {
        if (_active) return;
        ReadRows(); _localFound = local; _dlnaFound = dlna;
        ShowMembers();
    }

    internal async Task RefreshOutputsAsync()
    {
        if (_active || _scanningOutputs || _fileSource) return;
        _scanningOutputs = true; UpdateOutputControls();
        try
        {
            // Discovery is an explicit action, never part of page construction or its refresh timer.
            var local = await Task.Run(ListLocalOutputs, _outputStop.Token);
            var dlna = await BrowseDlnaOutputs(_outputStop.Token);
            if (!IsDisposed) SetAvailableOutputs(local, dlna);
        }
        catch (OperationCanceledException) when (_outputStop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"group output discovery: {ex.Message}");
            if (!IsDisposed) _outputHint.Text = L.T("搜索输出失败，请稍后重试。");
        }
        finally { _scanningOutputs = false; if (!IsDisposed) UpdateOutputControls(); }
    }

    private void RefreshOutputChoices()
    {
        _addActions.Clear(); _add.Items.Clear();
        int type = _fileSource ? 0 : _outputType.SelectedIndex;
        if (type == 0)
        {
            _choices = StereoPairs.Merge(_found).Where(d => !StereoPairs.IsPairId(d.DeviceId)
                && _speakers.All(s => GroupPlan.Key(s.DeviceId) != GroupPlan.Key(d.DeviceId))).ToList();
            foreach (var device in _choices)
            {
                _add.Items.Add(device.Name);
                _addActions.Add(() =>
                {
                    _fileOnlyImports.Remove(GroupPlan.Key(device.DeviceId));
                    _speakers.Add(new() { DeviceId = device.DeviceId, Name = device.Name, Host = device.Address.ToString() });
                });
            }
        }
        else if (type == 1)
        {
            foreach (var endpoint in _localFound.Where(e => !_localSelection.Contains(LocalGroupSettings.Key(e.Id))))
            {
                _add.Items.Add(endpoint.Name);
                _addActions.Add(() =>
                {
                    if (_localBank.All(o => LocalGroupSettings.Key(o.DeviceId) != LocalGroupSettings.Key(endpoint.Id)))
                        _localBank.Add(new(endpoint.Id, endpoint.Name));
                    _localSelection.Add(LocalGroupSettings.Key(endpoint.Id));
                });
            }
        }
        else
        {
            foreach (var device in _dlnaFound.Where(d => !_dlnaSelection.Contains(DlnaGroupSettings.Key(d.Udn))))
            {
                _add.Items.Add(device.Name);
                _addActions.Add(() =>
                {
                    if (_dlnaBank.All(o => DlnaGroupSettings.Key(o.Udn) != DlnaGroupSettings.Key(device.Udn)))
                        _dlnaBank.Add(new(device.Udn, device.Name));
                    _dlnaSelection.Add(DlnaGroupSettings.Key(device.Udn));
                });
            }
        }
        _add.SelectedIndex = _add.Items.Count > 0 ? 0 : -1;
        _outputHint.Text = _fileSource ? L.T("本机和 DLNA 输出只参与电脑实时声音，文件布局不会改变它们的选择。")
            : type == 1 ? L.T("先在 Windows 配对蓝牙音箱，再刷新并选择对应设备；本机输出不能与采集设备相同。")
            : type == 2 ? L.T("DLNA 仅在专用网络播放；未验证的设备需要手动微调。")
            : "";
    }

    private void AddChosenOutput()
    {
        if (_active || LiveOutputCount >= 8 || _add.SelectedIndex < 0 || _add.SelectedIndex >= _addActions.Count) return;
        ReadRows(); _addActions[_add.SelectedIndex](); ShowMembers();
    }

    private void UpdateOutputControls()
    {
        _outputType.Enabled = !_active && !_fileSource;
        _refreshOutputs.Enabled = !_active && !_fileSource && !_scanningOutputs;
        _clearOutputs.Enabled = !_active && !_fileSource && LiveOutputCount > 0;
        _timingControls.Collapsed = _fileSource;
        _delayMode.Enabled = !_active;
        _groupDelay.Enabled = !_active && _delayMode.SelectedIndex == 1;
        _auxiliaryMembers.Collapsed = _fileSource;
        _add.Enabled = !_active && _addActions.Count > 0 && (_tsid == null || _outputType.SelectedIndex != 0 && !_fileSource);
        _addButton.Enabled = _add.Enabled && LiveOutputCount < 8;
        _alignmentTier.Text = AlignmentText();
        foreach (Control control in _auxiliaryMembers.Controls) control.Enabled = !_active;
    }

    private string AlignmentText()
    {
        if (LiveOutputCount == 0) return "";
        if (!_fileSource && _dlnaBank.Any(o => _dlnaSelection.Contains(DlnaGroupSettings.Key(o.Udn))))
            return L.T("大致对齐，适合不同房间；DLNA 的实际延迟需要校准。");
        if (!_fileSource && (_localBank.Any(o => _localSelection.Contains(LocalGroupSettings.Key(o.DeviceId)))
            || _speakers.Any(s => !AirPlayRequiredByDefault(s.DeviceId)
                || _config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(s.DeviceId)) is { ProtocolOverride: ReceiverProtocol.Auto, LastSuccessfulProtocol: ReceiverProtocol.Raop })))
            return L.T("校准后基本同步；蓝牙链路或 AirPlay 1 会增加整组延迟。");
        return L.T("AirPlay 2 使用共同时间线，目标严格同步；第三方设备需实机验证。");
    }

    private bool AirPlayRequiredByDefault(string id)
    {
        var device = _found.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id))
            ?? _config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.Device(id);
        var protocol = _config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.ProtocolOverride;
        return protocol != ReceiverProtocol.Raop && device?.Kind is not (ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1);
    }

    private ToggleSwitch RequiredControl(string id, string name, bool defaultValue) => new(L.T("必需输出"))
    {
        AccessibleName = L.F("{0} 必需输出", name), Checked = _requiredOutputs.GetValueOrDefault(OutputGroups.RequiredKey(id), defaultValue),
    };

    private static void AddDetails(StackPanel entry, string name, params Control[] controls)
    {
        var body = Ui.Stack(4, controls); body.Collapsed = true;
        var toggle = new FluentButton(L.T("更多设置"), ButtonKind.Subtle) { AccessibleName = L.F("{0} 更多设置", name) };
        bool expanded = false;
        toggle.Click += (_, _) =>
        {
            expanded = !expanded; body.Collapsed = !expanded;
            toggle.Text = expanded ? L.T("收起设置") : L.T("更多设置");
        };
        entry.Controls.Add(toggle); entry.Controls.Add(body);
    }

    private static string RowKey(OutputKind kind, string id) => kind switch
    {
        OutputKind.Local => "local:" + LocalGroupSettings.Key(id),
        OutputKind.Dlna => "dlna:" + DlnaGroupSettings.Key(id.StartsWith("dlna:", StringComparison.OrdinalIgnoreCase) ? id[5..] : id),
        _ => "airplay:" + GroupPlan.Key(id),
    };

    private TextBlock StatusRow(OutputKind kind, string id, string name, bool found)
    {
        var text = Ui.Note(found ? L.T("未连接") : L.T("未发现")); text.AccessibleName = L.F("{0} 状态", name);
        _statusRows[RowKey(kind, id)] = (text, found); return text;
    }

    internal void ShowOutputStatus(IReadOnlyList<GroupOutputStatus> statuses, int? actualDelayMs)
    {
        _alignmentTier.Text = statuses.Any(s => s.Kind == OutputKind.Dlna) ? L.T("大致对齐，适合不同房间；DLNA 的实际延迟需要校准。")
            : statuses.Any(s => s.Kind == OutputKind.Local || s.Protocol == ReceiverProtocol.Raop) ? L.T("校准后基本同步；蓝牙链路或 AirPlay 1 会增加整组延迟。")
            : AlignmentText();
        foreach (var (key, row) in _statusRows)
        {
            var status = statuses.FirstOrDefault(s => RowKey(s.Kind, s.Id) == key);
            string text = status == null ? row.Found ? L.T("未连接") : L.T("未发现")
                : status.State == OutputState.Streaming ? L.T("已连接")
                : status.NeedsUserAction ? L.T("等待操作") : status.Retrying ? L.T("等待重连")
                : status.State == OutputState.Preparing ? L.T("正在连接…") : L.T("未连接");
            row.Text.Text = status?.Error is { Length: > 0 } error
                ? L.F("{0}：{1}", text, error is EventChannel.ClosedBySpeaker or RaopClient.ClosedByReceiver ? L.T(StreamController.TakenOverText) : error) : text;
        }
        _actualGroupDelay.Text = actualDelayMs is { } delay ? L.F("当前整组延迟：{0} ms", delay)
            : L.T("自动按最慢的输出确定；手动值低于设备下限时仍使用下限。");
    }

    private void ShowAuxiliaryOutputs()
    {
        foreach (var control in _auxiliaryMembers.Controls.Cast<Control>().ToArray()) control.Dispose();
        _readAuxiliaryRows.Clear();
        _auxRoomBindings.Clear();
        if (_fileSource) return;
        foreach (var output in _localBank.Where(o => _localSelection.Contains(LocalGroupSettings.Key(o.DeviceId))).ToArray())
        {
            var endpoint = _localFound.FirstOrDefault(e => LocalGroupSettings.Key(e.Id) == LocalGroupSettings.Key(output.DeviceId));
            string name = endpoint?.Name ?? output.Name;
            var channels = ChannelControl(name, output.Channels);
            var trim = OutputSlider(name, output.TrimMs, -5000, 5000, L.F("{0} 延迟（毫秒）", name));
            var volume = OutputSlider(name, output.VolumeOffset, -100, 100, L.F("{0} 音量偏移", name));
            var extra = OutputSlider(name, output.ExtraLatencyMs, 0, 10000, L.F("{0} 附加延迟（毫秒）", name));
            var bluetooth = new ToggleSwitch(L.T("蓝牙输出")) { AccessibleName = L.F("{0} 蓝牙输出", name), Checked = output.IsBluetooth };
            bluetooth.CheckedChanged += (_, _) => { if (bluetooth.Checked && extra.Value == 0) extra.Value = 200; };
            var required = RequiredControl("local:" + output.DeviceId, name, output.Required);
            var entry = AuxiliaryRow("local:" + output.DeviceId, name, output.IsBluetooth ? L.T("蓝牙") : L.T("本机输出"), channels, trim, volume,
                () => { _localSelection.Remove(LocalGroupSettings.Key(output.DeviceId)); ShowMembers(); });
            entry.Controls.Add(StatusRow(OutputKind.Local, output.DeviceId, name, endpoint != null));
            AddDetails(entry, name, required, bluetooth, SliderField(L.T("附加延迟"), extra),
                Ui.Note(L.T("Windows 不报告蓝牙链路延迟，普通蓝牙可先填 200 ms，再用手机录像微调。")));
            _auxiliaryMembers.Controls.Add(entry);
            _readAuxiliaryRows.Add(() =>
            {
                int index = _localBank.FindIndex(o => LocalGroupSettings.Key(o.DeviceId) == LocalGroupSettings.Key(output.DeviceId));
                if (index >= 0) _localBank[index] = output with { Name = name, Channels = (ChannelMode)channels.SelectedIndex,
                    TrimMs = trim.Value, VolumeOffset = volume.Value, ExtraLatencyMs = extra.Value, IsBluetooth = bluetooth.Checked, Required = required.Checked };
                _requiredOutputs[OutputGroups.RequiredKey("local:" + output.DeviceId)] = required.Checked;
            });
        }
        foreach (var output in _dlnaBank.Where(o => _dlnaSelection.Contains(DlnaGroupSettings.Key(o.Udn))).ToArray())
        {
            var device = _dlnaFound.FirstOrDefault(d => DlnaGroupSettings.Key(d.Udn) == DlnaGroupSettings.Key(output.Udn));
            string name = device?.Name ?? output.Name;
            var channels = ChannelControl(name, output.Channels);
            var trim = OutputSlider(name, output.TrimMs, -5000, 5000, L.F("{0} 延迟（毫秒）", name));
            var volume = OutputSlider(name, output.VolumeOffset, -100, 100, L.F("{0} 音量偏移", name));
            var estimate = OutputSlider(name, output.EstimatedLatencyMs, 0, 20000, L.F("{0} 估计延迟（毫秒）", name));
            var required = RequiredControl(output.Id, name, output.Required);
            var format = new FluentComboBox { AccessibleName = L.F("{0} 首选格式", name), MinWidth = 120 };
            string?[] formats = [null, "l16", "wav", "mp3", "aac"];
            format.Items.AddRange([L.T("自动"), "LPCM", "WAV", "MP3", "AAC"]);
            format.SelectedIndex = Math.Max(0, Array.IndexOf(formats, output.PreferredFormat));
            var framing = new FluentComboBox { AccessibleName = L.F("{0} HTTP 传输", name), MinWidth = 160 };
            framing.Items.AddRange([L.T("分块传输"), L.T("固定长度")]); framing.SelectedIndex = output.Framing == "length" ? 1 : 0;
            var entry = AuxiliaryRow(output.Id, name, "DLNA", channels, trim, volume,
                () => { _dlnaSelection.Remove(DlnaGroupSettings.Key(output.Udn)); ShowMembers(); });
            entry.Controls.Add(StatusRow(OutputKind.Dlna, output.Id, name, device != null));
            AddDetails(entry, name, required, SliderField(L.T("估计延迟"), estimate), new FieldRow(L.T("首选格式"), format),
                new FieldRow(L.T("HTTP 传输"), framing));
            _auxiliaryMembers.Controls.Add(entry);
            _readAuxiliaryRows.Add(() =>
            {
                int index = _dlnaBank.FindIndex(o => DlnaGroupSettings.Key(o.Udn) == DlnaGroupSettings.Key(output.Udn));
                if (index >= 0) _dlnaBank[index] = output with { Name = name, Channels = (ChannelMode)channels.SelectedIndex,
                    TrimMs = trim.Value, VolumeOffset = volume.Value, EstimatedLatencyMs = estimate.Value, Required = required.Checked,
                    PreferredFormat = formats[format.SelectedIndex], Framing = framing.SelectedIndex == 1 ? "length" : "chunked" };
                _requiredOutputs[OutputGroups.RequiredKey(output.Id)] = required.Checked;
            });
        }
    }

    private static FluentSlider OutputSlider(string name, int value, int min, int max, string accessible)
    {
        var slider = Ui.Slider(min, max, 1, 10); slider.Value = value; slider.FillOrigin = Math.Clamp(0, min, max);
        slider.PreferredLength = 120; slider.AccessibleName = accessible; return slider;
    }
    private static Control SliderField(string label, FluentSlider slider)
    {
        var value = new TextBlock(slider.Value.ToString()); slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString();
        return Ui.Stack(2, new TextBlock(label, TextStyle.Caption, TextRole.Secondary, wrap: true), Ui.Row(4, null, slider, value));
    }
    private static FluentComboBox ChannelControl(string name, ChannelMode selected)
    {
        var control = new FluentComboBox { AccessibleName = L.F("{0} 位置", name), MinWidth = 120 };
        control.Items.AddRange([PositionName(SpeakerPosition.Stereo), PositionName(SpeakerPosition.Left), PositionName(SpeakerPosition.Right)]);
        control.SelectedIndex = (int)selected; return control;
    }
    private StackPanel AuxiliaryRow(string id, string name, string type, FluentComboBox channels, FluentSlider trim, FluentSlider volume, Action remove)
    {
        _auxRoomBindings.Add(new(id, name, channels, [SpeakerPosition.Stereo, SpeakerPosition.Left, SpeakerPosition.Right], trim, volume));
        channels.SelectedIndexChanged += (_, _) => RefreshRoom();
        trim.ValueChanged += (_, _) => RefreshRoom();
        volume.ValueChanged += (_, _) => RefreshRoom();
        var button = new FluentButton(L.T("移除")) { AccessibleName = L.F("移除 {0}", name) };
        button.Click += (_, _) => { if (_active) return; ReadRows(); remove(); };
        var fields = Ui.Row(8, null, Ui.Stack(2, new TextBlock(L.T("音箱位置"), TextStyle.Caption, TextRole.Secondary, wrap: true), channels),
            SliderField(L.T("延迟"), trim), SliderField(L.T("音量偏移"), volume), button); fields.Wrap = true;
        return Ui.Stack(4, new TextBlock(name + " · " + type, TextStyle.BodyStrong, wrap: true), fields);
    }

    internal OutputGroupSettings CaptureSettings()
    {
        ReadRows();
        var result = OutputGroups.Capture(_config);
        if (_tsid == null)
        {
            var first = _speakers.FirstOrDefault();
            result.DeviceId = first?.DeviceId; result.DeviceName = first?.Name; result.Host = first?.Host;
        }
        result.GroupSpeakers = GroupSettings.Copy(_speakers);
        result.GroupVolumeOffsets = _speakers.ToDictionary(s => GroupPlan.Key(s.DeviceId), s => s.VolumeOffset);
        result.GroupCaptureDeviceIds = _speakers.Select(s => s.DeviceId).ToList();
        result.GroupCapturePositions = new(_capturePositions);
        result.GroupCaptureDelayTrims = new(_captureTrims);
        result.GroupRequiredOutputs = new(_requiredOutputs);
        result.GroupLocalOutputs = LocalGroupSettings.Copy(_localBank);
        result.GroupCaptureLocalDeviceIds = _localBank.Where(o => _localSelection.Contains(LocalGroupSettings.Key(o.DeviceId))).Select(o => o.DeviceId).ToList();
        result.GroupDlnaOutputs = DlnaGroupSettings.Copy(_dlnaBank);
        result.GroupCaptureDlnaUdns = _dlnaBank.Where(o => _dlnaSelection.Contains(DlnaGroupSettings.Key(o.Udn))).Select(o => o.Udn).ToList();
        result.GroupDelayMs = _delayMode.SelectedIndex == 0 ? null : _groupDelay.Value;
        result.GroupSplitChannels = result.GroupSwapChannels = false;
        result.GroupPairLeaderOnly = _tsid != null && _leaderOnly.Checked;
        return result;
    }

    internal IReadOnlyList<GroupAlignmentOutput> AlignmentOutputs()
    {
        ReadRows();
        return LiveAirPlayMembers().Select(s => new GroupAlignmentOutput(RowKey(OutputKind.AirPlay2, s.DeviceId), s.Name,
                _captureTrims.GetValueOrDefault(GroupPlan.Key(s.DeviceId), s.DelayMs)))
            .Concat(_localBank.Where(o => _localSelection.Contains(LocalGroupSettings.Key(o.DeviceId)))
                .Select(o => new GroupAlignmentOutput(RowKey(OutputKind.Local, o.DeviceId), o.Name, o.TrimMs)))
            .Concat(_dlnaBank.Where(o => _dlnaSelection.Contains(DlnaGroupSettings.Key(o.Udn)))
                .Select(o => new GroupAlignmentOutput(RowKey(OutputKind.Dlna, o.Id), o.Name, o.TrimMs))).ToArray();
    }

    internal bool HasAppliedSelection()
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(_config))!;
        OutputGroups.Apply(copy, CaptureSettings());
        return PlayLayout(copy) == PlayLayout(_config);
    }

    internal void ApplyManualTrim(GroupAlignmentOutput adjustment)
    {
        if (_fileSource || adjustment.TrimMs is < -5000 or > 5000) return;
        ReadRows();
        foreach (var speaker in _speakers)
            if (RowKey(OutputKind.AirPlay2, speaker.DeviceId) == adjustment.Id)
                _captureTrims[GroupPlan.Key(speaker.DeviceId)] = adjustment.TrimMs;
        for (int i = 0; i < _localBank.Count; i++)
            if (RowKey(OutputKind.Local, _localBank[i].DeviceId) == adjustment.Id) _localBank[i] = _localBank[i] with { TrimMs = adjustment.TrimMs };
        for (int i = 0; i < _dlnaBank.Count; i++)
            if (RowKey(OutputKind.Dlna, _dlnaBank[i].Id) == adjustment.Id) _dlnaBank[i] = _dlnaBank[i] with { TrimMs = adjustment.TrimMs };
        ShowMembers();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _outputStop.Cancel(); _outputStop.Dispose(); }
        base.Dispose(disposing);
    }
}
