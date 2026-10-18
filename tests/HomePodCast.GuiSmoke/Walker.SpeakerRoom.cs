using System.Text;
using System.Windows.Automation;

namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    /// <summary>Runs in every existing 150% language group scenario. All changes are drafts and are restored;
    /// generated silent files are inspected only. Play, Apply, measurement and test tones are never invoked.</summary>
    private void CheckSpeakerRoomLive()
    {
        var left = ReachRoomNode(n => n.Type == "Button" && n.Name.StartsWith(_t.F("{0}：{1}", T("前左"), ""), StringComparison.Ordinal));
        Expect(left.Name.Contains(GroupAirPlayName, StringComparison.Ordinal) && left.Name.Contains(GroupLocalName, StringComparison.Ordinal),
            "speaker room / names", "the front-left position omits an assigned AirPlay/local output");
        var roomDevice = GroupControl(Uia.Snapshot(MainWindow()), T("所选位置音箱"), "ComboBox").Hwnd;
        Uia.Invoke(left);
        // PositionAccessible posts its click. Wait for that retained control's acknowledgement before asking Windows
        // for a whole cached tree: 2026-10-18 the cache response was malformed during this transition in every language.
        WaitUntil(() => Native.MsaaValue(roomDevice) == GroupAirPlayName,
            "front-left room selection", 5000);
        var selectedDelay = ReachRoomNode(n => n.Name == T("所选位置延迟（毫秒）") && n.Type == "Slider");
        Expect(selectedDelay.Rect.Width >= 140 * _s.Scale / 100, "speaker room / slider width",
            $"selected delay rail is only {selectedDelay.Rect.Width}px at {_s.Scale}%");
        CheckGroupValue(Uia.Snapshot(MainWindow()), T("所选位置延迟（毫秒）"), "-137", "room selected delay");
        var selectedVolume = ReachRoomNode(n => n.Name == T("所选位置音量偏移") && n.Type == "Slider");
        Expect(selectedVolume.Rect.Width >= 140 * _s.Scale / 100, "speaker room / slider width",
            $"selected volume rail is only {selectedVolume.Rect.Width}px at {_s.Scale}%");

        // The custom position button receives actual keyboard messages; selecting a position opens no audio path.
        left = ReachRoomNode(n => n.Hwnd == left.Hwnd);
        Native.SendMessageTimeout(left.Hwnd, 0x0100 /* WM_KEYDOWN */, new IntPtr(0x27 /* VK_RIGHT */), IntPtr.Zero, 0x2, 5000, out _);
        WaitUntil(() => Native.MsaaValue(roomDevice) == GroupBluetoothName,
            "arrow-key room selection", 5000);
        ReachRoomNode(n => n.Name == T("所选位置延迟（毫秒）") && n.Type == "Slider");
        CheckGroupValue(Uia.Snapshot(MainWindow()), T("所选位置延迟（毫秒）"), "29", "room right delay");
        var delay = GroupControl(Uia.Snapshot(MainWindow()), T("所选位置延迟（毫秒）"), "Slider");
        Native.Msaa(delay.Hwnd)?.set_accValue(0, "31");
        WaitUntil(() => Native.MsaaValue(GroupControl(Uia.Snapshot(MainWindow()), _t.F("{0} 延迟（毫秒）", GroupBluetoothName), "Slider").Hwnd) == "31",
            "room delay reflected in the existing output row", 5000);
        Native.Msaa(delay.Hwnd)?.set_accValue(0, "29");
        WaitUntil(() => Native.MsaaValue(GroupControl(Uia.Snapshot(MainWindow()), _t.F("{0} 延迟（毫秒）", GroupBluetoothName), "Slider").Hwnd) == "29",
            "room delay draft restored", 5000);
        ReachSpeakerRoom();
        Shoot(MainWindow(), "speaker-room-live", "top-down stereo room, keyboard selection, saved offline outputs");
        Layout(Uia.Snapshot(MainWindow()), "speaker room / stereo");
    }

    private void CheckSpeakerRoomFiles()
    {
        foreach (var (channels, mask, name, count) in new[] { (6, 0x3fu, "51", 7), (8, 0x63fu, "71", 9) })
        {
            string path = Path.Combine(_dir, "room-layout-" + name + ".wav");
            WriteRoomWave(path, channels, mask);
            OpenRoomFile(path);
            var room = ReachSpeakerRoom();
            // The page creates child windows on an idle budget. A visible group can precede its new source's buttons.
            try
            {
                WaitUntil(() => Find(Uia.Snapshot(MainWindow()), n => n.Hwnd == room.Hwnd)?.Descendants()
                        .Count(n => n.Type == "Button" && n.Shown) == count,
                    "the " + name + " room position windows", 8000);
            }
            catch (StepFailed)
            {
                Dump(Uia.Snapshot(MainWindow()), "speaker-room-" + name + "-positions-missing");
                Shoot(MainWindow(), "speaker-room-" + name + "-positions-missing", "room child window creation at failure");
                throw;
            }
            room = ReachRoomNode(n => n.Hwnd == room.Hwnd);
            var buttons = room.Descendants().Where(n => n.Type == "Button" && n.Shown).ToArray();
            Expect(buttons.Length == count, "speaker room / " + name, $"the {name} file exposes {buttons.Length} positions instead of {count}");
            var center = buttons.Single(n => n.Name.StartsWith(_t.F("{0}：{1}", T("中置"), ""), StringComparison.Ordinal));
            // The file fixture has one front-right output. Compare the complete formatted announcement: German
            // and Japanese place the destination inside the sentence rather than at its end.
            string expectedCenter = _t.F("{0}：{1}", T("中置"), _t.F("未分配：混入{0}", T("前右")));
            Expect(center.Name == expectedCenter, "speaker room / " + name,
                $"the empty center announces 「{center.Name}」 instead of the actual front-right downmix 「{expectedCenter}」");
            Uia.Invoke(ReachRoomNode(n => n.Hwnd == center.Hwnd));
            ReachRoomNode(n => n.Name == T("所选位置音箱") && n.Type == "ComboBox");
            Expect(!GroupControl(Uia.Snapshot(MainWindow()), T("所选位置音箱"), "ComboBox").Enabled,
                "speaker room / " + name, "the offline file receiver is incorrectly offered for a discrete channel");
            ReachSpeakerRoom();
            Shoot(MainWindow(), "speaker-room-" + name, "top-down " + name + " room with actual empty-slot downmix destinations");
            Layout(Uia.Snapshot(MainWindow()), "speaker room / " + name);
        }
    }

    // Danish and Polish translate the section heading and diagram name identically. The heading is a Text;
    // require the accessible Group so the whole diagram, rather than just that heading, is in each screenshot.
    private Node ReachSpeakerRoom() => ReachRoomNode(node => node.Type == "Group" && node.Name == T("音箱布局图"));

    /// <summary>A control must fit inside the page viewport. WS_VISIBLE alone also describes scrolled-away children.</summary>
    private Node ReachRoomNode(Func<Node, bool> match)
    {
        var pane = PagePane(Uia.Snapshot(MainWindow()), GroupPageName).Hwnd;
        Scroll(pane, Native.SB_TOP);
        var trace = new List<string>();
        for (int step = 0; step < 32; step++)
        {
            var tree = Uia.Snapshot(MainWindow());
            var page = Find(tree, n => n.Hwnd == pane) ?? throw new StepFailed("speaker room: page window vanished");
            var node = Find(page, n => n.Shown && n.HasSize && match(n) && page.Rect.Contains(n.Rect));
            if (node != null) return node;
            var target = Find(page, n => n.Shown && n.HasSize && match(n));
            var info = Native.VerticalScroll(pane);
            trace.Add($"step {step}: viewport {page.Rect}, target {target?.Describe() ?? "missing"}, scroll {info?.nPos}/{info?.nMax}, page {info?.nPage}");
            if (info is not { } si || si.nPos + si.nPage > si.nMax) break;
            // Leave room at both edges: a real line command can round past an exact top-alignment target.
            int next = target != null && target.Rect.Height <= page.Rect.Height
                ? Math.Clamp(si.nPos + target.Rect.Top - page.Rect.Top - (page.Rect.Height - target.Rect.Height) / 2,
                    si.nMin, si.nMax - si.nPage + 1)
                : Math.Min(si.nMax - si.nPage + 1, si.nPos + Math.Max(1, si.nPage / 2));
            if (next <= si.nPos) break;
            if (!ScrollViewportForward(pane, next - si.nPos)) break;
        }
        Dump(Uia.Snapshot(MainWindow()), "speaker-room-unreachable");
        Shoot(MainWindow(), "speaker-room-unreachable", "room target and actual scroll viewport at failure");
        throw new StepFailed("speaker room: expected control never fit inside the visible page viewport: " + string.Join("; ", trace));
    }

    private void OpenRoomFile(string path)
    {
        var main = MainWindow();
        var choose = ReachRoomNode(n => n.Type == "Button" && n.Says(T("选择影音文件")));
        Uia.Invoke(choose);
        // A common file picker is also #32770. Drive and validate it explicitly instead of letting the generic
        // unexpected-message-box handler click Cancel while it is opening or closing (2026-10-17 CI).
        var dialog = WaitWindow(() => Native.TopLevelWindows(App.Id).FirstOrDefault(hwnd => hwnd != main
            && Native.ClassName(hwnd) == "#32770" && Native.IsWindowVisible(hwnd)
            && Native.WindowRect(hwnd) is { Width: > 0, Height: > 0 }),
            "the generated room file picker", 10_000, handlePrompts: false);
        Node tree;
        try
        {
            WaitUntil(() => Find(Uia.Snapshot(dialog), IsRoomFileNameEdit) != null,
                "the native file picker filename control", 5000, handlePrompts: false);
            tree = Uia.Snapshot(dialog);
        }
        catch (StepFailed)
        {
            Dump(Uia.Snapshot(dialog), "speaker-room-file-picker-error");
            Shoot(dialog, "speaker-room-file-picker-error", "unexpected or incomplete file picker");
            throw;
        }
        var filename = Find(tree, IsRoomFileNameEdit)
            ?? throw new StepFailed("speaker room: file picker has no filename edit");
        if (!filename.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            throw new StepFailed("speaker room: filename edit has no value pattern");
        ((ValuePattern)value).SetValue(path);
        var open = Find(tree, n => n.Type == "Button" && n.AutomationId == "1")
            ?? throw new StepFailed("speaker room: file picker has no Open action");
        Uia.Invoke(open);
        try
        {
            WaitUntil(() => !Native.IsWindowVisible(dialog), "the generated room file picker to close", 10_000,
                handlePrompts: false);
        }
        catch (StepFailed)
        {
            foreach (var hwnd in Native.TopLevelWindows(App.Id).Where(hwnd => Native.IsWindowVisible(hwnd)
                && Native.ClassName(hwnd) == "#32770"))
            {
                Dump(Uia.Snapshot(hwnd), "speaker-room-file-open-error");
                Shoot(hwnd, "speaker-room-file-open-error", "file picker or native error still blocks opening the fixture");
            }
            throw;
        }
        WaitUntil(() => PagePane(Uia.Snapshot(MainWindow()), GroupPageName).Descendants().Any(n => n.Type == "Text"
            && n.Name.Contains(Path.GetFileName(path), StringComparison.Ordinal) && n.Name.Contains(channelsName(path), StringComparison.Ordinal)),
            "the generated multichannel file inspection", 20_000);
        static string channelsName(string file) => file.EndsWith("51.wav", StringComparison.Ordinal) ? "5.1" : "7.1";
    }

    private static bool IsRoomFileNameEdit(Node node) => node.Type == "Edit" && (node.AutomationId is "1148" or "1001"
        || node.Ancestors().Any(ancestor => ancestor.AutomationId is "1148" or "FileNameControlHost"));

    private static void WriteRoomWave(string path, int channels, uint mask)
    {
        const int rate = 44100, frames = 4410;
        int bytes = frames * channels * 2;
        using var writer = new BinaryWriter(File.Create(path));
        void Tag(string text) => writer.Write(Encoding.ASCII.GetBytes(text));
        Tag("RIFF"); writer.Write(60 + bytes); Tag("WAVE"); Tag("fmt "); writer.Write(40);
        writer.Write((ushort)0xfffe); writer.Write((ushort)channels); writer.Write(rate); writer.Write(rate * channels * 2);
        writer.Write((ushort)(channels * 2)); writer.Write((ushort)16); writer.Write((ushort)22);
        writer.Write((ushort)16); writer.Write(mask); writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        Tag("data"); writer.Write(bytes); writer.Write(new byte[bytes]);
    }
}
