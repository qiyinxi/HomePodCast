using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// 连接 while another device plays to the speaker: connecting cuts that device off, so ask. 取消 is the default (Enter and
/// Esc); 仍然连接 takes the speaker over.
/// </summary>
internal sealed class TakeoverDialog : FluentDialog
{
    /// <param name="owner">The main window when it is visible; null from the tray (the dialog then stays on top).</param>
    /// <param name="speakers">The busy speaker's name (several: joined with " / ").</param>
    /// <returns>True: connect anyway.</returns>
    public static bool Confirm(IWin32Window? owner, string speakers)
    {
        using var dialog = new TakeoverDialog(speakers);
        if (owner != null) return dialog.ShowDialog(owner) == DialogResult.OK;
        dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.TopMost = true;
        return dialog.ShowDialog() == DialogResult.OK;
    }

    private TakeoverDialog(string speakers) : base(L.T("连接音箱"))
    {
        ContentWidth = 440;
        var text = new TextBlock(L.F("{0}正在播放其他设备推送的声音（比如 iPhone 或 Mac）。连接后会换成这台电脑的声音。", speakers), wrap: true);
        var connect = new FluentButton(L.T("仍然连接")) { DialogResult = DialogResult.OK, MinWidth = 96 };
        var cancel = new FluentButton(L.T("取消"), ButtonKind.Primary) { DialogResult = DialogResult.Cancel, MinWidth = 96 };
        var buttons = ButtonRow(connect, cancel);
        Body.Controls.AddRange([text, buttons]);
        Body.GapBefore[buttons] = 20;
        AcceptButton = cancel;
        CancelButton = cancel;
        ActiveControl = cancel;
        PerformLayout();
    }
}
