using System.Diagnostics;
using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Short mode summaries stay in sight; the original setup and recovery instructions are optional reading.</summary>
internal sealed class MixerModeHelp : Card
{
    internal const string AppVolumeSettings = "ms-settings:apps-volume";

    // Tests replace the launcher so activating the real link never changes Windows settings.
    internal Action<string> LaunchSettings { get; set; } = uri =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    public MixerModeHelp()
    {
        var appVolume = new FluentButton(L.T("打开 Windows 应用音量设置"), ButtonKind.Link, Glyph.OpenInNew);
        var more = new FluentButton(L.T("了解更多"), ButtonKind.Link)
        {
            AlternateTexts = [L.T("了解更多"), L.T("收起详细说明")],
        };
        var details = Ui.Stack(10,
            Ui.Stack(4,
                new TextBlock(L.T("全部推送（默认，推荐打游戏）"), TextStyle.BodyStrong),
                Ui.Note(L.T("直接抓取整个声音输出，没有额外延迟。所有程序都送到 HomePod；本机出不出声由 Windows 音量决定，想只在音箱播放，就打开「首页 → 仅 HomePod 输出」。"))),
            Ui.Stack(4,
                new TextBlock(L.F("按程序分流（额外延迟约 {0} ms）", RoutedCapture.RoutedExtraLatencyMs), TextStyle.BodyStrong),
                Ui.Note(L.F("单独设置程序去处或 HomePod 音量，或默认去处是「本机」，就会启用。Windows 只能逐个抓取程序的声音，这条路更慢：推到 HomePod 的声音会多约 {0} ms。浏览器插件会把这部分自动算进画面延迟，游戏没法补偿。设为「HomePod」的程序在本机静音，Windows 音量合成器里显示为 0%；系统提示音只在本机播放。全部改回「默认」、关闭 HomePod 独立音量，并把默认设为 HomePod，就回到全部推送。", RoutedCapture.RoutedExtraLatencyMs))),
            Ui.Stack(4,
                new TextBlock(L.T("按声卡分流（没有额外延迟）"), TextStyle.BodyStrong),
                Ui.Note(L.F("在 Windows 的应用音量设置（或程序自己的设置）里，把要推送的程序的输出设为一个你不用来听的设备，再在上面的「采集设备」里选它；语音聊天等其他程序照常在耳机里播放。整个设备一次抓取，不像按程序分流那样多约 {0} ms。需要一个空闲的或虚拟的声卡，比如 VB-CABLE、网易 UU 加速器的虚拟声卡、Steam Streaming Speakers，或不带喇叭的 HDMI 显示器；HomePodCast 没法自己装虚拟声卡（那是要微软签名的内核驱动）。有些游戏只在启动时选择输出设备，改完要重启游戏。", RoutedCapture.RoutedExtraLatencyMs))));
        details.Collapsed = true;
        Controls.AddRange([
            Ui.Header(L.T("全部推送与按程序分流")),
            Ui.Stack(4,
                new TextBlock(L.T("全部推送（默认，推荐打游戏）"), TextStyle.BodyStrong),
                Ui.Note(L.T("一次采集整个输出设备，延迟最低。所有程序送到当前音箱或音箱组。"))),
            Ui.Stack(4,
                new TextBlock(L.F("按程序分流（额外延迟约 {0} ms）", RoutedCapture.RoutedExtraLatencyMs), TextStyle.BodyStrong),
                Ui.Note(L.F("为程序单独选择 HomePod、本机或两者，推送声音额外延迟约 {0} ms。游戏最在意延迟时，建议使用全部推送。", RoutedCapture.RoutedExtraLatencyMs))),
            Ui.Stack(4,
                new TextBlock(L.T("按声卡分流（没有额外延迟）"), TextStyle.BodyStrong),
                Ui.Note(L.T("在 Windows 中把要推送的程序放到同一块空闲声卡，再选择它作为采集设备。仍使用全部推送的采集路径。"))),
            Ui.Row(0, null, appVolume),
            Ui.Row(0, null, more),
            details,
        ]);
        more.Click += (_, _) =>
        {
            using (LayoutPanel.BatchRelayout(this))
            {
                details.Collapsed = !details.Collapsed;
                more.Text = details.Collapsed ? L.T("了解更多") : L.T("收起详细说明");
            }
        };
        appVolume.Click += (_, _) =>
        {
            try { LaunchSettings(AppVolumeSettings); }
            catch (Exception ex) { Log.Warn($"open {AppVolumeSettings}: {ex.Message}"); }
        };
    }
}
