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
        Uia.Invoke(left);
        WaitUntil(() => Native.MsaaValue(GroupControl(Uia.Snapshot(MainWindow()), T("所选位置音箱"), "ComboBox").Hwnd) == GroupAirPlayName,
            "front-left room selection", 5000);
        ReachRoomNode(n => n.Name == T("所选位置延迟（毫秒）") && n.Type == "Slider");
        CheckGroupValue(Uia.Snapshot(MainWindow()), T("所选位置延迟（毫秒）"), "-137", "room selected delay");

        // The custom position button receives actual keyboard messages; selecting a position opens no audio path.
        left = ReachRoomNode(n => n.Hwnd == left.Hwnd);
        Native.SendMessageTimeout(left.Hwnd, 0x0100 /* WM_KEYDOWN */, new IntPtr(0x27 /* VK_RIGHT */), IntPtr.Zero, 0x2, 5000, out _);
        WaitUntil(() => Native.MsaaValue(GroupControl(Uia.Snapshot(MainWindow()), T("所选位置音箱"), "ComboBox").Hwnd) == GroupBluetoothName,
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
        ReachRoomNode(n => n.Name == T("音箱布局图"));
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
            var room = ReachRoomNode(n => n.Name == T("音箱布局图"));
            var buttons = room.Descendants().Where(n => n.Type == "Button" && n.Shown).ToArray();
            Expect(buttons.Length == count, "speaker room / " + name, $"the {name} file exposes {buttons.Length} positions instead of {count}");
            var center = buttons.Single(n => n.Name.StartsWith(_t.F("{0}：{1}", T("中置"), ""), StringComparison.Ordinal));
            Expect(center.Name.Contains(T("未分配：混入{0}").Replace("{0}", ""), StringComparison.Ordinal), "speaker room / " + name,
                "the empty center slot does not announce the actual downmix destination");
            Uia.Invoke(ReachRoomNode(n => n.Hwnd == center.Hwnd));
            ReachRoomNode(n => n.Name == T("所选位置音箱") && n.Type == "ComboBox");
            Expect(!GroupControl(Uia.Snapshot(MainWindow()), T("所选位置音箱"), "ComboBox").Enabled,
                "speaker room / " + name, "the offline file receiver is incorrectly offered for a discrete channel");
            ReachRoomNode(n => n.Name == T("音箱布局图"));
            Shoot(MainWindow(), "speaker-room-" + name, "top-down " + name + " room with actual empty-slot downmix destinations");
            Layout(Uia.Snapshot(MainWindow()), "speaker room / " + name);
        }
    }

    /// <summary>A control must fit inside the page viewport. WS_VISIBLE alone also describes scrolled-away children.</summary>
    private Node ReachRoomNode(Func<Node, bool> match)
    {
        var pane = PagePane(Uia.Snapshot(MainWindow()), GroupPageName).Hwnd;
        Scroll(pane, Native.SB_TOP);
        for (int step = 0; step < 32; step++)
        {
            var tree = Uia.Snapshot(MainWindow());
            var page = Find(tree, n => n.Hwnd == pane) ?? throw new StepFailed("speaker room: page window vanished");
            var node = Find(page, n => n.Shown && n.HasSize && match(n) && page.Rect.Contains(n.Rect));
            if (node != null) return node;
            var info = Native.VerticalScroll(pane);
            if (info is not { } si || si.nPos + si.nPage > si.nMax) break;
            var target = Find(page, n => n.Shown && n.HasSize && match(n));
            int next = target != null && target.Rect.Height <= page.Rect.Height
                ? Math.Clamp(si.nPos + target.Rect.Top - page.Rect.Top, si.nMin, si.nMax - si.nPage + 1)
                : Math.Min(si.nMax - si.nPage + 1, si.nPos + Math.Max(1, si.nPage / 2));
            if (next <= si.nPos) break;
            Scroll(pane, (next << 16) | 4 /* SB_THUMBPOSITION */);
        }
        Dump(Uia.Snapshot(MainWindow()), "speaker-room-unreachable");
        throw new StepFailed("speaker room: expected control never fit inside the visible page viewport");
    }

    private void OpenRoomFile(string path)
    {
        var choose = ReachRoomNode(n => n.Type == "Button" && n.Says(T("选择影音文件")));
        Uia.Invoke(choose);
        var dialog = WaitWindow(() => Native.TopLevelWindows(App.Id).FirstOrDefault(hwnd => hwnd != MainWindow()
            && Native.ClassName(hwnd) == "#32770"), "the generated room file picker", 10_000);
        var tree = Uia.Snapshot(dialog);
        var filename = Find(tree, n => n.Type == "Edit" && (n.AutomationId is "1148" or "1001"
            || n.Ancestors().Any(a => a.AutomationId is "1148" or "FileNameControlHost")))
            ?? throw new StepFailed("speaker room: file picker has no filename edit");
        if (!filename.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            throw new StepFailed("speaker room: filename edit has no value pattern");
        ((ValuePattern)value).SetValue(path);
        var open = Find(tree, n => n.Type == "Button" && n.AutomationId == "1")
            ?? throw new StepFailed("speaker room: file picker has no Open action");
        Uia.Invoke(open);
        WaitUntil(() => !Native.IsWindowVisible(dialog), "the generated room file picker to close", 10_000);
        WaitUntil(() => PagePane(Uia.Snapshot(MainWindow()), GroupPageName).Descendants().Any(n => n.Type == "Text"
            && n.Name.Contains(Path.GetFileName(path), StringComparison.Ordinal) && n.Name.Contains(channelsName(path), StringComparison.Ordinal)),
            "the generated multichannel file inspection", 20_000);
        static string channelsName(string file) => file.EndsWith("51.wav", StringComparison.Ordinal) ? "5.1" : "7.1";
    }

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
