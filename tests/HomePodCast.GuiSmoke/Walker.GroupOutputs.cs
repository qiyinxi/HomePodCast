using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    private const string GroupPageName = "多音箱（实验性）";
    private const string GroupAirPlayId = "02:47:52:4F:55:50", GroupAirPlayName = "CI saved offline HomePod";
    private const string GroupManualId = "02:41:44:44:43:49", GroupManualName = "CI unselected manual HomePod";
    private const string GroupLocalId = "{0.0.0.00000000}.{c1c10000-0000-4000-8000-000000000001}";
    private const string GroupBluetoothId = "{0.0.0.00000000}.{c1c10000-0000-4000-8000-000000000002}";
    private const string GroupLocalName = "CI saved local speaker", GroupBluetoothName = "CI saved Bluetooth headphones";
    private const string GroupDlnaUdn = "uuid:c1c10000-0000-4000-8000-000000000003", GroupDlnaName = "CI saved offline DLNA";
    private const string GroupUnusedDlnaUdn = "uuid:c1c10000-0000-4000-8000-000000000004", GroupUnusedDlnaName = "CI unselected DLNA bank";
    private const string GroupPresetName = "CI remembered output group";

    private static readonly string[] GroupPreferenceKeys =
    [
        "AutoConnect", "DeviceId", "DeviceName", "Host", "ManualDevices", "GroupSpeakers", "GroupCaptureDeviceIds",
        "GroupCapturePositions", "GroupVolumeOffsets", "GroupCaptureDelayTrims", "GroupRequiredOutputs", "GroupDelayMs",
        "GroupLocalOutputs", "GroupCaptureLocalDeviceIds", "GroupDlnaOutputs", "GroupCaptureDlnaUdns",
        "SavedOutputGroups", "SelectedOutputGroupId",
    ];

    /// <summary>
    /// Saved output identities render offline without a fake-provider switch or a real output. Startup's ordinary
    /// AirPlay refresh contributes one unselected loopback manual entry; the page itself must start nothing.
    /// </summary>
    private Dictionary<string, object?> GroupOutputsConfig()
    {
        var fixture = new AppConfig
        {
            AutoConnect = false, DeviceId = GroupAirPlayId, DeviceName = GroupAirPlayName, Host = "127.0.0.1",
            GroupSpeakers = [new() { DeviceId = GroupAirPlayId, Name = GroupAirPlayName, Host = "127.0.0.1",
                Position = SpeakerPosition.Right, DelayMs = 83, VolumeOffset = -4 }],
            GroupCaptureDeviceIds = [GroupAirPlayId],
            GroupCapturePositions = new() { [GroupPlan.Key(GroupAirPlayId)] = SpeakerPosition.Left },
            GroupVolumeOffsets = new() { [GroupPlan.Key(GroupAirPlayId)] = -4 },
            GroupCaptureDelayTrims = new() { [GroupPlan.Key(GroupAirPlayId)] = -137 },
            GroupRequiredOutputs = new() { [GroupPlan.Key(GroupAirPlayId)] = false },
            GroupDelayMs = 2740,
            GroupLocalOutputs =
            [
                new(GroupLocalId, GroupLocalName) { ExtraLatencyMs = 41, TrimMs = -23, VolumeOffset = -7,
                    Required = true, Channels = ChannelMode.LeftOnly },
                new(GroupBluetoothId, GroupBluetoothName) { IsBluetooth = true, ExtraLatencyMs = 317, TrimMs = 29,
                    VolumeOffset = 5, Required = false, Channels = ChannelMode.RightOnly },
            ],
            GroupCaptureLocalDeviceIds = [GroupLocalId, GroupBluetoothId],
            GroupDlnaOutputs =
            [
                new(GroupDlnaUdn, GroupDlnaName) { EstimatedLatencyMs = 2460, TrimMs = 61, VolumeOffset = -11,
                    Required = true, Channels = ChannelMode.Stereo, PreferredFormat = "wav", Framing = "length" },
                new(GroupUnusedDlnaUdn, GroupUnusedDlnaName) { EstimatedLatencyMs = 1830, TrimMs = -47, VolumeOffset = 3,
                    Required = false, Channels = ChannelMode.RightOnly, PreferredFormat = "mp3" },
            ],
            GroupCaptureDlnaUdns = [GroupDlnaUdn],
        };
        GroupSettings.Normalize(fixture);
        OutputGroups.Normalize(fixture);
        var preset = OutputGroups.Save(fixture, GroupPresetName, OutputGroups.Capture(fixture), "ci-output-group");
        fixture.SelectedOutputGroupId = preset.Id;

        var config = BaseConfig();
        foreach (var property in JsonSerializer.SerializeToElement(OutputGroups.Capture(fixture)).EnumerateObject())
            config[property.Name] = property.Value.Clone();
        config["ManualDevices"] = new[] { new ManualAirPlayDevice(GroupManualName, GroupManualId, "127.0.0.1", 7002,
            "AudioAccessory6,1", new() { ["model"] = "AudioAccessory6,1" }) };
        config["SavedOutputGroups"] = fixture.SavedOutputGroups;
        config["SelectedOutputGroupId"] = fixture.SelectedOutputGroupId;
        _userData.AddRange([GroupAirPlayName, GroupManualName, GroupLocalName, GroupBluetoothName, GroupDlnaName,
            GroupUnusedDlnaName, GroupPresetName]);
        return config;
    }

    private void RunGroupOutputs()
    {
        var config = GroupOutputsConfig();
        var preferences = JsonNode.Parse(JsonSerializer.Serialize(config))!.AsObject();
        Start(config); // Start also requires this window to report the scenario's 144 DPI.
        WaitUntil(() => Find(PagePane(Uia.Snapshot(MainWindow()), "首页"), n => n.Type == "Button" && n.Shown && n.Says(T("刷新")))
                is { Enabled: true } && ReadLog().Any(line => line.Contains("receiver:", StringComparison.Ordinal)
                    && line.Contains(GroupManualName, StringComparison.Ordinal) && line.Contains("127.0.0.1:7002", StringComparison.Ordinal)),
            "startup discovery to finish with the unselected loopback manual receiver", 15_000);
        CheckGroupHomeIdle(Uia.Snapshot(MainWindow()));
        var baseline = ReadLog();
        File.WriteAllLines(Path.Combine(_dir, "group-idle-baseline.log"), baseline);
        CheckGroupIdle(preferences, 0, "startup");

        Navigate(GroupPageName);
        Thread.Sleep(1100); // More than four page timer ticks; opening and idle refresh must remain inert.
        CheckGroupIdle(preferences, baseline.Count, "opened");
        CheckGroupOutputChoices();
        CheckGroupOutputRows(fileSource: false); // Includes all saved values after adding/removing an independent draft.
        CheckGroupDelayModes();
        CheckGroupManualCalibration();
        ShootPage(GroupPageName, "saved-live-outputs", checkEachScroll: true);
        CheckGroupIdle(preferences, baseline.Count, "live");

        SetGroupSource(fileSource: true);
        CheckGroupOutputRows(fileSource: true);
        ShootPage(GroupPageName, "saved-file-outputs", checkEachScroll: true);
        CheckGroupIdle(preferences, baseline.Count, "file");
        SetGroupSource(fileSource: false);

        Navigate("首页");
        CheckGroupHomeIdle(Uia.Snapshot(MainWindow()));
        CheckGroupIdle(preferences, baseline.Count, "left-page");
        Navigate(GroupPageName);
        Thread.Sleep(1100);
        CheckGroupOutputRows(fileSource: false);
        if (CaptureGroupTransitions) ShootPage(GroupPageName, "reopened-live-outputs", checkEachScroll: true);
        else Layout(Uia.Snapshot(MainWindow()), "group outputs / reopened live outputs");
        CheckGroupIdle(preferences, baseline.Count, "reopened");
        Navigate("首页");
        var home = Uia.Snapshot(MainWindow());
        CheckGroupHomeIdle(home);
        Layout(home, "group outputs / home remains idle");
        Shoot(MainWindow(), "group-home-idle", "首页 remains disconnected after group editing");
        CheckGroupIdle(preferences, baseline.Count, "home-after");
        Quit();
        CheckSavedGroupPreferences(preferences, "quit");
        CheckGroupActivityLog(baseline.Count, "quit");
        CheckLog(idle: true);
    }

    private void CheckGroupIdle(JsonObject preferences, int baselineLines, string step)
    {
        int port = ApiPort() ?? throw new StepFailed("no local API port for the group-output idle check");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        string status = http.GetStringAsync($"http://127.0.0.1:{port}/v1/status").GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(_dir, $"group-api-{step}.json"), status);
        using var document = JsonDocument.Parse(status);
        Expect(document.RootElement.TryGetProperty("streaming", out var streaming) && streaming.ValueKind == JsonValueKind.False,
            "group outputs / " + step, $"the read-only local API does not report streaming=false: {status}");
        CheckGroupActivityLog(baselineLines, step);
        CheckSavedGroupPreferences(preferences, step);
    }

    private void CheckGroupHomeIdle(Node tree)
    {
        CheckHome(tree, connected: false);
        var page = PagePane(tree, "首页");
        Expect(Find(page, n => n.Type == "Button" && n.Shown && n.Says(T("连接"))) is { Enabled: true },
            "group outputs / idle home", "首页 does not offer an enabled Connect button after leaving the group page");
        Expect(!page.Descendants().Any(n => n.Type == "Button" && n.Shown && n.Says(T("断开"))),
            "group outputs / idle home", "首页 offers Disconnect after opening the group page");
        var saved = Find(page, n => n.Type == "ComboBox" && n.Shown && n.Name == T("已保存的分组") && n.Hwnd != IntPtr.Zero);
        Expect(saved != null && Native.MsaaValue(saved.Hwnd) == GroupPresetName, "group outputs / saved group",
            "首页 does not show the saved output-group selection");
    }

    private void CheckGroupActivityLog(int baselineLines, string step)
    {
        foreach (var line in ReadLog().Skip(baselineLines).Where(line =>
                     line.Contains("capturing \"", StringComparison.Ordinal) || line.Contains("capture:", StringComparison.Ordinal)
                     || line.Contains("cinema: start", StringComparison.Ordinal) || line.Contains("cinema calibration:", StringComparison.Ordinal)
                     || line.Contains("stream:", StringComparison.Ordinal)))
            _report.Fail(Where("group outputs / " + step), "idle page started audio or attempted a stream: " + line);
    }

    private void CheckSavedGroupPreferences(JsonObject expected, string step)
    {
        var actual = JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDir, "config.json")))!.AsObject();
        foreach (string key in GroupPreferenceKeys)
            Expect(JsonNode.DeepEquals(expected[key], actual[key]), "group outputs / " + step,
                $"opening or editing the page without Apply changed saved {key}: expected {expected[key]?.ToJsonString() ?? "null"}, " +
                $"got {actual[key]?.ToJsonString() ?? "null"}");
    }

    private Node GroupControl(Node tree, string name, string? type = null) =>
        Find(PagePane(tree, GroupPageName), n => n.Shown && n.Hwnd != IntPtr.Zero && n.Name == name
            && (type == null ? n.Type != "Text" : n.Type == type))
        ?? throw new StepFailed($"group outputs: no {type ?? "control"} named 「{name}」");

    private void CheckGroupValue(Node tree, string name, string expected, string step)
    {
        // A FieldRow can share its caption with its child. Read the value-bearing control rather
        // than its named Pane, whose MSAA Value is correctly empty.
        var control = Find(PagePane(tree, GroupPageName), n => n.Shown && n.Hwnd != IntPtr.Zero && n.Name == name
            && n.Type is "ComboBox" or "Slider") ?? throw new StepFailed($"group outputs: no value control named 「{name}」");
        string? actual = Native.MsaaValue(control.Hwnd);
        Expect(actual == expected, "group outputs / " + step, $"「{name}」 is 「{actual ?? "(none)"}」, expected 「{expected}」");
    }

    private void CheckGroupChecked(Node tree, string name, bool expected)
    {
        var control = GroupControl(tree, name);
        int? state = Native.MsaaState(control.Hwnd);
        Expect(state is { } value && ((value & Native.STATE_SYSTEM_CHECKED) != 0) == expected, "group outputs / saved values",
            $"「{name}」 is not {(expected ? "checked" : "unchecked")} (state {state?.ToString("X") ?? "not found"})");
    }

    private void SetGroupSource(bool fileSource)
    {
        SetGroupCombo("音源", fileSource ? "本地影音文件" : "电脑实时声音");
        var tree = Uia.Snapshot(MainWindow());
        CheckGroupNoPlayback(tree, fileSource);
        var choose = Find(PagePane(tree, GroupPageName), n => n.Type == "Button" && n.Says(T("选择影音文件")) && n.Shown);
        Expect(fileSource ? choose is { Enabled: true } : choose is not { Enabled: true }, "group outputs / source",
            $"the file picker is not {(fileSource ? "enabled in File" : "inactive in Live")}");
    }

    private void SetGroupCombo(string name, string value)
    {
        var combo = GroupControl(Uia.Snapshot(MainWindow()), T(name), "ComboBox");
        Native.Msaa(combo.Hwnd)?.set_accValue(0, T(value));
        WaitUntil(() => Native.MsaaValue(combo.Hwnd) == T(value),
            $"group selector 「{T(name)}」 = 「{T(value)}」", 5000);
    }

    private void CheckGroupNoPlayback(Node tree, bool fileSource)
    {
        var page = PagePane(tree, GroupPageName);
        foreach (string key in new[] { "播放", "暂停", "停止", "测量音箱延迟" })
        {
            var button = Find(page, n => n.Type == "Button" && n.Says(T(key)) && n.Shown);
            Expect(fileSource ? button is { Enabled: false } : button is not { Enabled: true }, "group outputs / idle controls",
                button == null ? $"File source has no 「{T(key)}」 button" : $"「{T(key)}」 is enabled before a file plays");
        }
    }

    private void CheckGroupDelayModes()
    {
        var tree = Uia.Snapshot(MainWindow());
        CheckGroupValue(tree, T("整组延迟"), T("手动"), "manual group delay");
        CheckGroupValue(tree, T("整组延迟（毫秒）"), "2740", "manual group delay");
        SetGroupCombo("整组延迟", "自动");
        tree = Uia.Snapshot(MainWindow());
        var manual = Find(PagePane(tree, GroupPageName), n => n.Name == T("整组延迟（毫秒）") && n.Hwnd != IntPtr.Zero && n.Type != "Text" && n.Shown);
        Expect(manual is not { Enabled: true }, "group outputs / automatic group delay", "the manual group delay remains enabled in Automatic mode");
        if (CaptureGroupTransitions) ShootPage(GroupPageName, "automatic-delay", checkEachScroll: true);
        SetGroupCombo("整组延迟", "手动");
        CheckGroupValue(Uia.Snapshot(MainWindow()), T("整组延迟（毫秒）"), "2740", "manual delay preserved after Automatic");
    }

    private void CheckGroupOutputChoices()
    {
        SetGroupCombo("输出类型", "AirPlay");
        var tree = Uia.Snapshot(MainWindow());
        CheckGroupValue(tree, T("选择输出"), GroupManualName, "unselected AirPlay choice");
        var add = GroupControl(tree, T("添加输出"), "Button");
        Expect(add.Enabled, "group outputs / add", "the loopback manual AirPlay choice cannot be added");
        Uia.Invoke(add);
        WaitUntil(() => Find(PagePane(Uia.Snapshot(MainWindow()), GroupPageName), n => n.Name == _t.F("{0} 位置", GroupManualName)
            && n.Type == "ComboBox") != null, "the manual AirPlay draft output row", 5000);
        if (CaptureGroupTransitions) ShootPage(GroupPageName, "added-airplay-draft", checkEachScroll: true);
        var remove = GroupControl(Uia.Snapshot(MainWindow()), _t.F("移除 {0}", GroupManualName), "Button");
        Expect(remove.Enabled, "group outputs / remove", "the extra AirPlay draft output cannot be removed");
        Uia.Invoke(remove);
        WaitUntil(() => Find(PagePane(Uia.Snapshot(MainWindow()), GroupPageName), n => n.Name == _t.F("{0} 位置", GroupManualName)
            && n.Type == "ComboBox") == null, "the manual AirPlay draft output to be removed", 5000);

        // No refresh is invoked. Preference-bank identities are offline, so neither protocol has a discovered choice.
        foreach (string type in new[] { "蓝牙／本机输出", "DLNA" })
        {
            SetGroupCombo("输出类型", type);
            tree = Uia.Snapshot(MainWindow());
            var choices = GroupControl(tree, T("选择输出"), "ComboBox");
            Expect(string.IsNullOrEmpty(Native.MsaaValue(choices.Hwnd)), "group outputs / " + type,
                "an offline saved identity was presented as a discovered output choice");
            Expect(!GroupControl(tree, T("添加输出"), "Button").Enabled, "group outputs / " + type,
                "Add is enabled although no output of this type was discovered");
            if (CaptureGroupTransitions)
                ShootPage(GroupPageName, type == "DLNA" ? "empty-dlna-choices" : "empty-local-choices", checkEachScroll: true);
        }
        SetGroupCombo("输出类型", "AirPlay");
    }

    private void CheckGroupOutputRows(bool fileSource)
    {
        var tree = Uia.Snapshot(MainWindow());
        var page = PagePane(tree, GroupPageName);
        CheckGroupValue(tree, T("音源"), T(fileSource ? "本地影音文件" : "电脑实时声音"), "source mode");
        CheckGroupNoPlayback(tree, fileSource);
        Expect(Find(page, n => n.Shown && n.Type == "Button" && n.Says(T("保存分组"))) != null,
            "group outputs / saved group", "the group page has no Save Group action");
        Expect(page.Descendants().Any(n => n.Type == "Text" && n.Shown && n.Says(_t.F("{0}（未发现）", GroupAirPlayName))),
            "group outputs / saved AirPlay", "the saved AirPlay output is not identified as offline");
        CheckGroupValue(tree, _t.F("{0} 位置", GroupAirPlayName), T(fileSource ? "右" : "左"), "AirPlay channels");
        CheckGroupValue(tree, _t.F("{0} 延迟（毫秒）", GroupAirPlayName), fileSource ? "83" : "-137", "AirPlay delay");
        CheckGroupValue(tree, _t.F("{0} 音量偏移", GroupAirPlayName), "-4", "AirPlay volume");

        string[] auxiliary = [GroupLocalName, GroupBluetoothName, GroupDlnaName];
        if (fileSource)
        {
            foreach (string name in auxiliary)
                Expect(!page.Descendants().Any(n => n.Shown && n.Name == _t.F("{0} 位置", name)), "group outputs / File",
                    $"the live-only output 「{name}」 remains in the file layout");
            Expect(!GroupControl(tree, T("输出类型"), "ComboBox").Enabled, "group outputs / File", "output type can change in File source");
            Expect(!GroupControl(tree, T("刷新输出"), "Button").Enabled, "group outputs / File", "output discovery is enabled in File source");
            return;
        }

        ExpandGroupDetails(new[] { GroupAirPlayName }.Concat(auxiliary).ToArray());
        tree = Uia.Snapshot(MainWindow());
        page = PagePane(tree, GroupPageName);
        foreach (var (name, tag, channel, trim, volume, required) in new[]
                 {
                     (GroupLocalName, T("本机输出"), T("左"), "-23", "-7", true),
                     (GroupBluetoothName, T("蓝牙"), T("右"), "29", "5", false),
                     (GroupDlnaName, "DLNA", T("完整立体声"), "61", "-11", true),
                 })
        {
            Expect(page.Descendants().Any(n => n.Type == "Text" && n.Shown && n.Says(name + " · " + tag)),
                "group outputs / type tags", $"「{name}」 has no saved type tag 「{tag}」");
            var status = Find(page, n => n.Name == _t.F("{0} 状态", name) && n.Shown);
            Expect(status != null && status.Says(T("未发现")), "group outputs / offline rows", $"「{name}」 is not marked offline");
            CheckGroupValue(tree, _t.F("{0} 位置", name), channel, "saved channels");
            CheckGroupValue(tree, _t.F("{0} 延迟（毫秒）", name), trim, "saved trim");
            CheckGroupValue(tree, _t.F("{0} 音量偏移", name), volume, "saved volume");
            CheckGroupChecked(tree, _t.F("{0} 必需输出", name), required);
        }
        CheckGroupChecked(tree, _t.F("{0} 必需输出", GroupAirPlayName), false);
        CheckGroupChecked(tree, _t.F("{0} 蓝牙输出", GroupLocalName), false);
        CheckGroupChecked(tree, _t.F("{0} 蓝牙输出", GroupBluetoothName), true);
        CheckGroupValue(tree, _t.F("{0} 附加延迟（毫秒）", GroupLocalName), "41", "local link latency");
        CheckGroupValue(tree, _t.F("{0} 附加延迟（毫秒）", GroupBluetoothName), "317", "Bluetooth link latency");
        CheckGroupValue(tree, _t.F("{0} 估计延迟（毫秒）", GroupDlnaName), "2460", "DLNA estimated latency");
        CheckGroupValue(tree, _t.F("{0} 首选格式", GroupDlnaName), "WAV", "DLNA preferred format");
        CheckGroupValue(tree, _t.F("{0} HTTP 传输", GroupDlnaName), T("固定长度"), "DLNA framing");
        Expect(!page.Descendants().Any(n => n.Shown && n.Name == _t.F("{0} 位置", GroupUnusedDlnaName)),
            "group outputs / selection", "the unselected DLNA preference-bank entry became a selected output");
        var positionName = _t.Pattern("{0} 位置");
        int positions = page.Descendants().Count(n => n.Shown && n.Type == "ComboBox" && positionName.IsMatch(n.Name));
        Expect(positions == 4, "group outputs / selection", $"there are {positions} saved output rows instead of four");
    }

    // Every language still asserts all transitions and captures Live, File, Home and the manual helper.
    // Additional identical full-page captures belong to one language, keeping the required 15-language
    // matrix inside the CI budget without dropping any state/value/idle assertions.
    private bool CaptureGroupTransitions => _s.Language == "zh-CN";

    private void ExpandGroupDetails(string[] names)
    {
        var tree = Uia.Snapshot(MainWindow());
        foreach (string name in names)
            if (Find(PagePane(tree, GroupPageName), n => n.Shown && n.Name == _t.F("{0} 必需输出", name)) == null)
                Uia.Invoke(GroupControl(tree, _t.F("{0} 更多设置", name), "Button"));
        WaitUntil(() =>
        {
            var current = PagePane(Uia.Snapshot(MainWindow()), GroupPageName);
            return names.All(name => Find(current, n => n.Shown && n.Name == _t.F("{0} 必需输出", name)) != null);
        }, "all saved output details", 5000);
    }

    private void CheckGroupManualCalibration()
    {
        // Opening the phone-video helper is inert. The explicit tone and Apply actions are never invoked here.
        Dialog("group-manual-calibration", Button("手动校准"), "手动校准", "关闭", (tree, where) =>
        {
            CheckDpi(tree.Hwnd, where);
            foreach (string key in new[] { "参考输出", "待校准输出", "录像帧率" })
            {
                var combo = Find(tree, n => n.Shown && n.Type == "ComboBox" && n.Name == T(key) && n.Hwnd != IntPtr.Zero);
                Expect(combo != null, where, $"the manual helper has no accessible 「{T(key)}」 selector");
                if (combo != null)
                    Expect(!string.IsNullOrEmpty(Native.MsaaValue(combo.Hwnd)), where, $"「{T(key)}」 has no initial selection");
            }
            Expect(Find(tree, n => n.Shown && n.Type == "Slider" && n.Name == T("相对偏移帧数")) != null,
                where, "the manual helper has no accessible relative-frame offset");
            Expect(Find(tree, n => n.Shown && n.Type == "Button" && n.Says(T("开始测试音"))) is { Enabled: false },
                where, "the manual helper enables its test tone while no group is streaming");
            Expect(Find(tree, n => n.Shown && n.Type == "Button" && n.Says(T("应用微调"))) != null,
                where, "the manual helper has no Apply Trim action");
        }, ready: tree => Find(tree, n => n.Shown && n.Type == "ComboBox" && n.Name == T("待校准输出")) != null);
    }
}
