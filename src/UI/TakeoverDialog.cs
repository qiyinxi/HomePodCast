using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// 连接 while another device plays to the speaker: connecting cuts that device off, so ask. 取消 is the default (Enter and
/// Esc); 仍然连接 takes the speaker over. For a HomePod held by its home-theater Apple TV (asked only with 先询问) the text
/// names the Apple TV; the buttons are the same (nothing here leads to the Apple TV's PIN pairing).
/// </summary>
internal sealed class TakeoverDialog : FluentDialog
{
    /// <param name="owner">The main window when it is visible; null from the tray (the dialog then stays on top).</param>
    /// <param name="speakers">The busy speaker's name (several: joined with " / ").</param>
    /// <param name="appleTv">The home-theater Apple TV holding them; null: another device.</param>
    /// <returns>True: connect anyway.</returns>
    public static bool Confirm(IWin32Window? owner, string speakers, string? appleTv = null)
    {
        using var dialog = new TakeoverDialog(speakers, appleTv);
        if (owner != null) return dialog.ShowDialog(owner) == DialogResult.OK;
        dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.TopMost = true;
        return dialog.ShowDialog() == DialogResult.OK;
    }

    /// <summary>What the dialog says: another device's sound, or the home theater's Apple TV.</summary>
    internal static string Message(string speakers, string? appleTv) => appleTv == null
        ? L.F("{0}正在播放其他设备推送的声音（比如 iPhone 或 Mac）。连接后会换成这台电脑的声音。", speakers)
        : L.F("{0}现在是 Apple TV「{1}」的家庭影院音箱。连接后 Apple TV 的声音会暂时断开；这台电脑断开连接后，Apple TV 下次播放时会重新连上它。",
            speakers, appleTv);

    private TakeoverDialog(string speakers, string? appleTv) : base(L.T("连接音箱"))
    {
        ContentWidth = 440;
        var text = new TextBlock(Message(speakers, appleTv), wrap: true);
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
