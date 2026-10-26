using System.Diagnostics;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Tests;

namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    private string MixerAuditPath => Path.Combine(_dir, "mixer-audit.json");
    private long _mixerCommand;

    private void RunMixerDestinations()
    {
        _inPrompts = true; // These are product confirmations exercised below, never automatically dismissed.
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--mixer-fixture"); start.ArgumentList.Add(_s.Language); start.ArgumentList.Add(MixerAuditPath);
        _app = Process.Start(start) ?? throw new StepFailed("could not start the CI mixer fixture");
        MixerWait(() => MainWindow() != IntPtr.Zero && File.Exists(MixerAuditPath), "mixer fixture window");
        CheckDpi(MainWindow(), "mixer fixture");
        MixerWait(() => MixerAudit(a => a.GetProperty("statuses").GetArrayLength() == 2 &&
            a.GetProperty("statuses").EnumerateArray().All(s => s.GetProperty("State").GetInt32() == (int)StreamState.Streaming)), "two live destinations");
        MixerWait(() => MixerAudit(a => new[] { "rtp:" + MixerScenario.GameId, "rtp:" + MixerScenario.MusicId, "local", "dlna" }
            .All(key => a.GetProperty("nonzero").TryGetProperty(key, out var count) && count.GetInt64() > 1000)), "non-silent fake RTP, local and DLNA samples");
        long gameEpoch = MixerRead(a => a.GetProperty("senders").GetProperty(MixerRoutes.SpeakerKey("airplay:" + MixerScenario.GameId)).GetProperty("epoch").GetInt64());
        ShootPage("混音器", "destinations-connected", checkEachScroll: true);
        Expect(Has(Uia.Snapshot(MainWindow()), T("多去处混音（实验性）")), "mixer", "missing 1.3 experimental heading");
        CheckMixerHelp();
        Uia.Invoke(MixerControl(T("打开 Windows 应用音量设置"), "Hyperlink"));
        MixerWait(() => MixerAudit(a => a.GetProperty("settingsUris").GetArrayLength() == 1), "intercepted Windows settings link");
        Expect(MixerAudit(a => a.GetProperty("settingsUris")[0].GetString() == "ms-settings:apps-volume"), "mixer", "wrong intercepted settings URI");

        // The exact option source is shared by default/app/all pickers. The popup and accessibility must enforce its lease result.
        var overlap = MixerChoice("defaults", "Group", MixerScenario.OverlapGroup);
        Expect(!overlap.Enabled, "mixer", "an overlapping group can be selected for the default");
        var defaultPicker = MixerControl(T("没单独设置的程序送到"), "ComboBox");
        Uia.Invoke(defaultPicker);
        IntPtr popup = IntPtr.Zero;
        MixerWait(() => (popup = MixerPopup(overlap.Label)) != IntPtr.Zero, "destination popup");
        var menu = Uia.Snapshot(popup);
        Expect(Find(menu, n => n.Type == "MenuItem" && n.Says(overlap.Label)) is { Enabled: false }, "mixer", "the disabled conflict is absent from the real menu");
        ShootMixerDialog(popup, "destinations-conflict", "overlapping destination rejected");
        Native.PostMessage(popup, 0x0100, new IntPtr(0x1B), IntPtr.Zero);
        MixerWait(() => !Native.IsWindowVisible(popup), "destination popup closed");
        string before = Native.MsaaValue(defaultPicker.Hwnd) ?? "";
        Native.Msaa(defaultPicker.Hwnd)?.set_accValue(0, overlap.Label);
        Expect(Native.MsaaValue(defaultPicker.Hwnd) == before, "mixer", "accessibility committed a disabled destination");
        Expect(MixerChoice("all", "Group", MixerScenario.OverlapGroup).Enabled, "mixer", "replace-all cannot replace the old overlapping leases");

        MixerCommand("lose-music");
        MixerWait(() => MixerAudit(a => Status(a, "group:" + MixerScenario.MusicGroup).GetProperty("State").GetInt32() == (int)StreamState.Idle), "one destination lost");
        Expect(MixerAudit(a => Status(a, MixerRoutes.SpeakerKey("airplay:" + MixerScenario.GameId)).GetProperty("State").GetInt32() == (int)StreamState.Streaming &&
            a.GetProperty("senders").GetProperty(MixerRoutes.SpeakerKey("airplay:" + MixerScenario.GameId)).GetProperty("epoch").GetInt64() == gameEpoch), "mixer", "music loss stopped or restarted the game sender");
        ShootPage("混音器", "destinations-one-lost", checkEachScroll: true);
        Uia.Invoke(MixerButtonInRow(MixerScenario.MusicName, "连接"));
        MixerWait(() => MixerAudit(a => Status(a, "group:" + MixerScenario.MusicGroup).GetProperty("State").GetInt32() == (int)StreamState.Streaming), "music reconnected alone");

        // Changing the default preserves explicit app rules; this is also the first per-app latency confirmation.
        var quiet = MixerChoice("defaults", "Group", MixerScenario.QuietGroup);
        SetMixerChoice(T("没单独设置的程序送到"), quiet.Label, confirmLatency: true);
        MixerWait(() => MixerAudit(a => a.GetProperty("routing").GetProperty("Default").GetProperty("Output").GetProperty("Id").GetString() == MixerScenario.QuietGroup &&
            a.GetProperty("routing").GetProperty("Apps").EnumerateObject().Count() == 3), "default changed without overwriting app rules");
        Expect(MixerAudit(a => a.GetProperty("routing").GetProperty("Apps").GetProperty("music").GetProperty("Output").GetProperty("Id").GetString() == MixerScenario.MusicGroup), "mixer", "default change overwrote music");

        var offline = MixerChoice("defaults", "Speaker", "airplay:" + MixerScenario.OfflineId);
        SetMixerChoice(T("没单独设置的程序送到"), offline.Label);
        MixerWait(() => MixerAudit(a => Status(a, MixerRoutes.SpeakerKey("airplay:" + MixerScenario.OfflineId)).GetProperty("State").GetInt32() == (int)StreamState.Retrying), "offline saved destination waits");
        ShootPage("混音器", "destinations-offline", checkEachScroll: true);
        SetMixerChoice(T("没单独设置的程序送到"), quiet.Label);
        MixerWait(() => MixerAudit(a => Status(a, "group:" + MixerScenario.QuietGroup).GetProperty("State").GetInt32() == (int)StreamState.Streaming), "quiet room restored");
        long quietEpoch = MixerRead(a => a.GetProperty("senders").GetProperty("group:" + MixerScenario.QuietGroup).GetProperty("epoch").GetInt64());
        SetMixerChoice(_t.F("{0} 输出到", "CI Game"), MixerChoice("game", "Group", MixerScenario.QuietGroup).Label);
        MixerWait(() => MixerAudit(a => a.GetProperty("routing").GetProperty("Apps").TryGetProperty("game", out _)), "explicit same-destination rule");
        Expect(MixerAudit(a => a.GetProperty("statuses").GetArrayLength() == 2 &&
            a.GetProperty("senders").GetProperty("group:" + MixerScenario.QuietGroup).GetProperty("epoch").GetInt64() == quietEpoch), "mixer", "same destination opened a second controller");
        ShootPage("混音器", "destinations-independent", checkEachScroll: true);

        int beforeIdentities = MixerRead(a => a.GetProperty("saves").GetInt32());
        Uia.Invoke(MixerControl(T("确认设备身份"), "Hyperlink"));
        IntPtr identities = IntPtr.Zero;
        MixerWait(() => (identities = MixerWindow(T("确认设备身份"))) != IntPtr.Zero, "device identity dialog");
        ShootMixerDialog(identities, "destination-identities", "physical device aliases");
        var identityCancel = Find(Uia.Snapshot(identities), n => n.Type == "Button" && n.Says(T("取消"))) ?? throw new StepFailed("no identity Cancel");
        Uia.Invoke(identityCancel); MixerWait(() => !Native.IsWindowVisible(identities), "identity dialog cancelled");
        Expect(MixerAudit(a => a.GetProperty("saves").GetInt32() == beforeIdentities), "mixer", "identity Cancel saved a partial mapping");

        // Open the product's per-destination latency dialog. Cancel leaves both contexts untouched.
        var latency = MixerButtonInRow(MixerScenario.QuietName, "调整延迟"); Uia.Invoke(latency);
        IntPtr delay = IntPtr.Zero;
        MixerWait(() => (delay = MixerWindow(T("调整延迟"))) != IntPtr.Zero, "independent delay dialog");
        ShootMixerDialog(delay, "destination-delay", "per-destination latency");
        Expect(Has(Uia.Snapshot(delay), _t.F("按程序采集额外延迟约 {0} ms。", RoutedCapture.RoutedExtraLatencyMs)), "mixer", "game/process capture latency hint is missing next to delay");
        var cancel = Find(Uia.Snapshot(delay), n => n.Type == "Button" && n.Says(T("取消"))) ?? throw new StepFailed("no delay Cancel");
        Uia.Invoke(cancel); MixerWait(() => !Native.IsWindowVisible(delay), "delay dialog cancelled");

        // Include inactive rules, cancel, then make a concurrent edit while the first confirmation is open.
        SetMixerChoice(T("全部切到"), MixerChoice("all", "Group", MixerScenario.MusicGroup).Label);
        int rules = MixerRead(a => a.GetProperty("routing").GetProperty("Apps").EnumerateObject().Count());
        int saves = MixerRead(a => a.GetProperty("saves").GetInt32());
        Uia.Invoke(MixerControl(T("切换"), "Button"));
        AnswerMixerConfirmation(rules, false, "replace-all-cancel");
        Expect(MixerAudit(a => a.GetProperty("saves").GetInt32() == saves &&
            a.GetProperty("routing").GetProperty("Apps").EnumerateObject().Count() == rules), "mixer", "cancel partially applied replace-all");
        Uia.Invoke(MixerControl(T("切换"), "Button"));
        var first = WaitMixerConfirmation(rules);
        MixerCommand("external-rule");
        MixerWait(() => MixerAudit(a => a.GetProperty("externalSaves").GetInt32() == 1), "concurrent saved inactive rule");
        ShootMixerDialog(first, "replace-all-stale", "confirmation before concurrent edit");
        ClickMixerDialog(first, 6);
        AnswerMixerConfirmation(rules + 1, true, "replace-all-recounted");
        MixerWait(() => MixerAudit(a => a.GetProperty("routing").GetProperty("Apps").EnumerateObject().Count() == 0 &&
            a.GetProperty("routing").GetProperty("Default").GetProperty("Output").GetProperty("Id").GetString() == MixerScenario.MusicGroup &&
            a.GetProperty("statuses").GetArrayLength() == 1 && Status(a, "group:" + MixerScenario.MusicGroup).GetProperty("State").GetInt32() == (int)StreamState.Streaming), "replace-all applied once");
        Expect(MixerAudit(a => a.GetProperty("saves").GetInt32() == saves + 2 && a.GetProperty("sendVolumes").GetProperty("game").GetInt32() == 32 &&
            a.GetProperty("nativeDefaultId").GetString() == MixerScenario.NativeId && a.GetProperty("captureDeviceId").GetString() == MixerScenario.CaptureId), "mixer", "replace-all changed volumes, Windows output, or saved more than once");
        SetMixerChoice(_t.F("{0} 输出到", "CI Voice"), MixerChoice("voice", "Local", null).Label);
        MixerWait(() => MixerAudit(a => a.GetProperty("routing").GetProperty("Apps").GetProperty("voice").GetProperty("Output").GetProperty("Kind").GetString() == "Local"), "per-app edit after replace-all");
        ShootPage("混音器", "replace-all-applied-and-adjusted", checkEachScroll: true);
        MixerWait(() => MixerAudit(a => a.GetProperty("errors").GetArrayLength() == 0), "fake payload validation");
        MixerCommand("disconnect");
        MixerWait(() => MixerAudit(a => a.GetProperty("leaseCount").GetInt32() == 0 && a.GetProperty("statuses").GetArrayLength() == 0), "all destinations released");
        Expect(MixerAudit(a => a.GetProperty("errors").GetArrayLength() == 0), "mixer", "a fake sink received audio for another destination");
        MixerCommand("quit");
        if (!_app.WaitForExit(15000)) throw new StepFailed("mixer fixture did not exit");
        Expect(_app.ExitCode == 0, "mixer", "mixer fixture exit code " + _app.ExitCode);
        _inPrompts = false;
    }

    private (string Label, bool Enabled) MixerChoice(string source, string kind, string? id, bool alsoLocal = false) => MixerRead(a =>
    {
        var option = a.GetProperty("options").GetProperty(source).EnumerateArray().First(o =>
            o.GetProperty("Route").ValueKind != JsonValueKind.Null && o.GetProperty("Route").GetProperty("Output").GetProperty("Kind").GetString() == kind &&
            (id == null || o.GetProperty("Route").GetProperty("Output").GetProperty("Id").GetString() == id) &&
            o.GetProperty("Route").GetProperty("AlsoLocal").GetBoolean() == alsoLocal);
        return (option.GetProperty("Label").GetString()!, option.GetProperty("Enabled").GetBoolean());
    });
    private T MixerRead<T>(Func<JsonElement, T> read)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var file = new FileStream(MixerAuditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(file); return read(json.RootElement);
            }
            catch (Exception ex) when (attempt < 9 && ex is IOException or JsonException or UnauthorizedAccessException) { Thread.Sleep(10); }
        }
    }
    private bool MixerAudit(Func<JsonElement, bool> read)
    { try { return MixerRead(read); } catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; } }
    private static JsonElement Status(JsonElement audit, string key) => audit.GetProperty("statuses").EnumerateArray().First(s => s.GetProperty("Key").GetString() == key);
    private void MixerWait(Func<bool> ready, string description, int timeout = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { CheckAlive(); if (watch.ElapsedMilliseconds > timeout) throw new StepFailed("timed out: " + description); Thread.Sleep(25); }
    }
    private void MixerCommand(string operation)
    {
        long id = ++_mixerCommand; string path = MixerAuditPath + ".command." + id + ".json", temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { id, op = operation })); File.Move(temp, path);
        MixerWait(() => MixerAudit(a => a.GetProperty("command").GetInt64() == id), "fixture command " + operation);
    }
    private Node MixerControl(string name, string type)
    {
        var pane = PagePane(Uia.Snapshot(MainWindow()), "混音器").Hwnd;
        Scroll(pane, Native.SB_TOP);
        for (int i = 0; i < 30; i++)
        {
            var tree = Uia.Snapshot(MainWindow());
            var control = Find(tree, n => n.Type == type && n.Name == name && n.Shown && n.Hwnd != IntPtr.Zero);
            if (control != null) { if (PositionMixerControl(pane, control.Rect)) return control; continue; }
            var before = Native.VerticalScroll(pane); Scroll(pane, Native.SB_PAGEDOWN);
            if (Native.VerticalScroll(pane) is not { } after || before?.nPos == after.nPos) break;
        }
        throw new StepFailed("unreachable mixer control: " + name);
    }
    private Node MixerButtonInRow(string title, string button)
    {
        var pane = PagePane(Uia.Snapshot(MainWindow()), "混音器").Hwnd; Scroll(pane, Native.SB_TOP);
        for (int i = 0; i < 30; i++)
        {
            var tree = Uia.Snapshot(MainWindow());
            foreach (var heading in tree.Descendants().Where(n => n.Name == title && n.Type == "Text"))
            {
                var row = heading.Ancestors().FirstOrDefault(n => n.Descendants().Any(c => c.Type == "Button" && c.Says(T(button))));
                var result = row?.Descendants().FirstOrDefault(n => n.Type == "Button" && n.Says(T(button)));
                if (result != null)
                {
                    if (PositionMixerControl(pane, result.Rect)) return result;
                    goto ReadAgain;
                }
            }
            var before = Native.VerticalScroll(pane); Scroll(pane, Native.SB_PAGEDOWN);
            if (Native.VerticalScroll(pane) is not { } after || before?.nPos == after.nPos) break;
            ReadAgain:;
        }
        throw new StepFailed("unreachable destination action " + title + ": " + button);
    }
    internal static int? MixerScrollAction(Rectangle viewport, Rectangle target) => viewport.Contains(target) ? null :
        target.Top < viewport.Top ? 0 /* SB_LINEUP */ : target.Top >= viewport.Bottom ? Native.SB_PAGEDOWN : Native.SB_LINEDOWN;
    internal static int MixerScrollOffset(Rectangle viewport, Rectangle target) => target.Top < viewport.Top
        ? target.Top - viewport.Top : Math.Max(0, target.Bottom - viewport.Bottom);
    private static bool PositionMixerControl(IntPtr pane, Rectangle target)
    {
        var viewport = Native.WindowRect(pane);
        if (target.Left < viewport.Left || target.Right > viewport.Right) throw new StepFailed("mixer control is clipped horizontally");
        if (MixerScrollAction(viewport, target) is not { } action) return true;
        int? before = Native.VerticalScroll(pane)?.nPos;
        Scroll(pane, action);
        if (action is 0 or Native.SB_LINEDOWN && before is { } previous && Native.VerticalScroll(pane) is { } after)
        {
            int step = Math.Abs(after.nPos - previous), distance = Math.Abs(MixerScrollOffset(viewport, target));
            // WinForms' line increment can be a single pixel. A help/link pair can need over 100 of those,
            // so finish the observed distance as one batch instead of exhausting the snapshot-attempt limit.
            if (step > 0)
            {
                int more = Math.Clamp((distance + step - 1) / step - 1, 0, 2000);
                for (int i = 0; i < more; i++)
                    Native.SendMessageTimeout(pane, Native.WM_VSCROLL, new IntPtr(action), IntPtr.Zero, 0x2, 5000, out _);
                if (more > 0) Thread.Sleep(100);
            }
        }
        return false;
    }
    private void SetMixerChoice(string controlName, string value, bool confirmLatency = false)
    {
        var combo = MixerControl(controlName, "ComboBox");
        var setting = Task.Run(() => Native.Msaa(combo.Hwnd)?.set_accValue(0, value));
        if (confirmLatency)
        {
            IntPtr dialog = IntPtr.Zero; MixerWait(() => (dialog = MixerWindow(T("按程序分流会增加延迟"))) != IntPtr.Zero, "process capture latency confirmation");
            Expect(Has(Uia.Snapshot(dialog), _t.F("按程序采集会让送往音箱的声音多约 {0} ms 延迟，游戏无法补偿。要继续吗？", RoutedCapture.RoutedExtraLatencyMs)), "mixer", "wrong per-app latency confirmation");
            ShootMixerDialog(dialog, "routing-latency-confirmation", "process capture latency confirmation"); ClickMixerDialog(dialog, 6);
        }
        if (!setting.Wait(15000)) throw new StepFailed("mixer picker did not return: " + controlName);
    }
    private IntPtr MixerPopup(string item) => Native.TopLevelWindows(App.Id).FirstOrDefault(h => h != MainWindow() && Native.IsWindowVisible(h) &&
        Find(Uia.Snapshot(h), n => n.Type == "MenuItem" && n.Says(item)) != null);
    private IntPtr MixerWindow(string title) => Native.TopLevelWindows(App.Id).FirstOrDefault(h => Native.IsWindowVisible(h) && Native.WindowText(h) == title);
    private IntPtr WaitMixerConfirmation(int count)
    {
        string expected = _t.F("{0} 个程序的单独去处设置会被覆盖。要继续吗？", count);
        // A stale confirmation can still be closing, and Windows may reuse its handle for the recounted dialog.
        IntPtr dialog = IntPtr.Zero; MixerWait(() => (dialog = MixerWindow(T("全部切到"))) != IntPtr.Zero &&
            Has(Uia.Snapshot(dialog), expected), "replace-all confirmation with override count " + count);
        return dialog;
    }
    private void AnswerMixerConfirmation(int count, bool yes, string shot)
    {
        var dialog = WaitMixerConfirmation(count); ShootMixerDialog(dialog, shot, "override count " + count); ClickMixerDialog(dialog, yes ? 6 : 7);
        MixerWait(() => !Native.IsWindowVisible(dialog), "replace-all confirmation closed");
    }
    private void ShootMixerDialog(IntPtr dialog, string name, string caption)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(100);
            using var probe = Native.Capture(dialog);
            if (Native.UniformClient(probe)) continue;
            Shoot(dialog, name, caption); return;
        }
        throw new StepFailed("dialog client did not paint: " + name);
    }
    private static void ClickMixerDialog(IntPtr dialog, int id)
    {
        var button = Find(Uia.Snapshot(dialog), n => n.Type == "Button" && n.AutomationId == id.ToString())
            ?? throw new StepFailed("missing confirmation button " + id);
        Native.PostMessage(button.Hwnd, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
    }
}
