using HomePodCast.Audio;

namespace HomePodCast.UI;

// 音乐分轨 (experimental, Audio.LiveStemMixer; the switches are on 混音器, Pages/StemPanel.cs): what the app does around
// it. Separation is never saved as on: it ends with the connection, and when its module fails.
internal sealed partial class TrayApp
{
    /// <summary>Leaving 已连接 ends separation: a new session must never play the previous session's delayed music.</summary>
    internal static bool EndsStems(StreamState last, StreamState now) =>
        last == StreamState.Streaming && now != StreamState.Streaming;

    /// <summary>
    /// The connection ended while separation was on (UI thread). Said in a tray notice: after a reconnect the HomePod
    /// plays the original again, vocals included, and that must not come as a surprise.
    /// </summary>
    private void EndStemsWithConnection()
    {
        if (!Stems.Enabled) return;
        Stems.SetEnabled(false, "with the connection");
        ShowBalloon(L.T("连接已断开，音乐分轨已关闭。重新连接后，可以在混音器里再次开启。"), ToolTipIcon.Info, ShowMixer);
    }

    /// <summary>
    /// The module failed (UI thread). Separation is turned off rather than leaving the HomePod silent until somebody
    /// opens 混音器, and a tray notice says why; 混音器 keeps saying it until separation is turned on again.
    /// </summary>
    private void OnStemsFailed()
    {
        if (_quitting || !Stems.Enabled || Stems.Error == StemError.None) return;
        Stems.SetEnabled(false, "after the module failed");
        ShowBalloon(L.F("音乐分轨已关闭。{0}", StemErrorText(Stems.Error)), ToolTipIcon.Warning, ShowMixer);
    }

    /// <summary>Why the module stopped, in the user's language (the log has the module's own words).</summary>
    internal static string StemErrorText(StemError error) => error switch
    {
        StemError.StartFailed => L.T("分轨模块无法启动，请按安装说明重新安装。"),
        StemError.ModelRejected => L.T("分轨模块的模型缺失或版本不对，请重新安装与本程序同一版本的模块。"),
        StemError.Incompatible => L.T("分轨模块与本程序的版本不配套，请安装同一版本的模块。"),
        StemError.TimedOut => L.T("分轨模块没有及时响应。"),
        _ => L.T("分轨模块意外退出。"),
    };

    /// <summary>
    /// Why the A/V sync test can't run now, or null. Separation comes first: its fixed wait is added to the video delay
    /// by itself, and a calibration made with it on would count it twice.
    /// </summary>
    internal static string? SyncTestRefusal(bool stemsOn, StreamState state) =>
        stemsOn ? L.T("请先关闭音乐分轨再做同步校准；分轨的额外延迟会自动加入视频补偿。")
        : state != StreamState.Streaming ? L.T("请先连接音箱，再做同步测试。")
        : null;
}
