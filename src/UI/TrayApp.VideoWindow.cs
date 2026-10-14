using System.Diagnostics;
using HomePodCast.Players;

namespace HomePodCast.UI;

// 同步观影窗口 (experimental since 1.1): starts the separately downloaded module (VideoWindowModule), never in the
// Microsoft Store version. Opening it starts neither streaming nor a capture; quitting stops it.
internal sealed partial class TrayApp
{
    private Process? _videoWindow;

    public void ChooseVideoWindowModule(IWin32Window? owner)
    {
        if (!VideoWindowModule.Offered) return;
        using var picker = new FolderBrowserDialog { Description = L.T("选择已解压的同步窗口模块文件夹") };
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        if (!VideoWindowModule.Exists(picker.SelectedPath))
        { MessageBox.Show(owner, L.T("这个文件夹不包含完整的同步窗口模块。"), "HomePodCast"); return; }
        Config.VideoWindowModuleDirectory = picker.SelectedPath; Config.Save();
    }

    public void OpenVideoWindow(IWin32Window? owner)
    {
        if (!VideoWindowModule.Offered) return;
        try
        {
            if (_videoWindow is { HasExited: false })
            { MessageBox.Show(owner, L.T("同步窗口已打开。"), "HomePodCast"); return; }
            _videoWindow?.Dispose(); _videoWindow = null;
            if (!VideoWindowModule.Exists(Config.VideoWindowModuleDirectory)) ChooseVideoWindowModule(owner);
            if (!VideoWindowModule.Exists(Config.VideoWindowModuleDirectory)) return;
            string directory = Config.VideoWindowModuleDirectory!;
            _videoWindow = Process.Start(VideoWindowModule.StartInfo(directory, _api?.Port ?? 0, VideoDelayMs, L.Language));
            Log.Info($"video window: started module {VideoWindowModule.Version(directory) ?? "?"} (pid {_videoWindow?.Id})");
        }
        catch (Exception ex)
        {
            Log.Warn($"video window: not started: {ex.Message}");
            MessageBox.Show(owner, L.F("同步窗口无法启动：{0}", ex.Message), "HomePodCast");
        }
    }

    private void StopVideoWindow()
    {
        try { if (_videoWindow is { HasExited: false }) _videoWindow.Kill(true); } catch { }
        _videoWindow?.Dispose(); _videoWindow = null;
    }
}
