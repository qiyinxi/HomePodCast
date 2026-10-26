using HomePodCast.Audio;

namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    private const string MixerDetails = "一次采集所选输出设备，所有程序送到同一个远端去处。本机是否出声由 Windows 音量和静音设置决定。";

    /// <summary>Runs on CI only, on the basic scenarios and the existing all-language, required-150% matrix.</summary>
    private void CheckMixerHelp()
    {
        const string page = "混音器";
        var pane = PagePane(Uia.Snapshot(MainWindow()), page).Hwnd;
        Expect(!Has(Uia.Snapshot(MainWindow()), T(MixerDetails)), page, "detailed mode help is not collapsed initially");
        string[] summaries =
        [
            T("一次采集整个输出设备，延迟最低。所有程序送到同一个音箱或音箱组。"),
            _t.F("为程序单独选择音箱、音箱组、本机或音箱加本机，推送声音额外延迟约 {0} ms。游戏最在意延迟时，建议使用「{1}」。", RoutedCapture.RoutedExtraLatencyMs, T("全部推送")),
            _t.F("在 Windows 中把要推送的程序放到同一块空闲声卡，再选择它作为采集设备。仍使用「{0}」的采集路径。", T("全部推送")),
        ];
        var seen = new HashSet<string>();
        var more = HelpAction("了解更多");
        foreach (var summary in summaries) Expect(seen.Contains(summary), page, "short mode summary was never visible: " + summary);
        CheckSettingsLink();
        ShootPage(page, "help-collapsed", checkEachScroll: true);
        more = HelpAction("了解更多");
        more.Element.SetFocus();
        HelpKey(more, 0x20); // Space, through the native input rather than a direct click handler.
        WaitUntil(() => Find(Uia.Snapshot(MainWindow()), n => n.Shown && n.Says(T("收起详细说明"))) != null,
            "expanded mixer help", 5000);
        Expect(more.Element.Current.HasKeyboardFocus, page, "expanding help lost keyboard focus");
        Expect(Has(Uia.Snapshot(MainWindow()), T(MixerDetails)), page, "expanded help lost the original instructions");
        CheckSettingsLink();
        ShootPage(page, "help-expanded", checkEachScroll: true);
        var less = HelpAction("收起详细说明");
        less.Element.SetFocus();
        HelpKey(less, 0x0D); // Enter also folds it again, with the same focused control.
        WaitUntil(() => Find(Uia.Snapshot(MainWindow()), n => n.Shown && n.Says(T("了解更多"))) != null,
            "collapsed mixer help", 5000);
        Expect(less.Element.Current.HasKeyboardFocus, page, "collapsing help lost keyboard focus");
        Expect(!Has(Uia.Snapshot(MainWindow()), T(MixerDetails)), page, "collapsed instructions remain exposed to accessibility");
        CheckSettingsLink();
        Scroll(pane, Native.SB_TOP);

        Node HelpAction(string text)
        {
            Scroll(pane, Native.SB_TOP);
            for (int i = 0; i < 12; i++)
            {
                var tree = Uia.Snapshot(MainWindow());
                foreach (var summary in summaries)
                    if (Find(tree, n => n.Shown && n.Says(summary) && n.Rect.IntersectsWith(Native.WindowRect(pane))) != null)
                        seen.Add(summary);
                var action = Find(tree, n => n.Shown && n.Hwnd != IntPtr.Zero && n.Says(T(text)));
                if (action != null)
                {
                    var link = Find(tree, n => n.Shown && n.Hwnd != IntPtr.Zero && n.Says(T("打开 Windows 应用音量设置")));
                    var target = link == null ? action.Rect : Rectangle.Union(action.Rect, link.Rect);
                    if (PositionMixerControl(pane, target)) return action;
                    continue;
                }
                var before = Native.VerticalScroll(pane);
                Scroll(pane, Native.SB_PAGEDOWN);
                if (Native.VerticalScroll(pane) is not { } after || before?.nPos == after.nPos) break;
            }
            throw new StepFailed("mixer help action could not be reached by scrolling: " + text);
        }

        void CheckSettingsLink() => Expect(Find(Uia.Snapshot(MainWindow()), n => n.Shown && n.Hwnd != IntPtr.Zero
                && n.Says(T("打开 Windows 应用音量设置")) && Native.WindowRect(pane).Contains(n.Rect)) != null,
            page, "Windows app-volume settings link is hidden with the help toggle in view");

        static void HelpKey(Node action, int key)
        {
            if (Native.SendMessageTimeout(action.Hwnd, 0x0100 /* WM_KEYDOWN */, new IntPtr(key), IntPtr.Zero,
                    0x2, 5000, out _) == IntPtr.Zero) throw new StepFailed("mixer help button did not answer a keyboard action");
        }
    }
}
