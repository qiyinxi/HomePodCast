using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 首页's notice after a .NET update replaced the runtime this copy started on (TrayApp.Runtime.cs): what happened, and
/// 「立即重启」 next to it, in the network hint's style; collapsed otherwise. While streaming it also says that the sound
/// stops for a few seconds. Nothing restarts without the click.
/// </summary>
internal sealed class RuntimeRestartNotice
{
    /// <param name="restart">「立即重启」 (TrayApp.RestartForRuntime).</param>
    public RuntimeRestartNotice(Action restart)
    {
        var icon = new HomePage.GlyphLabel(Glyph.Warning, caution: true);
        Card = Ui.Card(Ui.Row(12, Text, icon, Text, Restart));
        Card.Collapsed = true;
        Restart.Click += (_, _) => restart();
    }

    public Card Card { get; }
    internal TextBlock Text { get; } = new("", TextStyle.Body, TextRole.Caution, wrap: true);
    internal FluentButton Restart { get; } = new(L.T("立即重启"), ButtonKind.Primary) { MinWidth = 72 };

    /// <param name="removed">The runtime is gone (TrayApp.RuntimeRemoved).</param>
    /// <param name="streaming">Sound is being sent now: the restart interrupts it.</param>
    public void Show(bool removed, bool streaming)
    {
        if (removed && Message(streaming) is var text && Text.Text != text) Text.Text = text;
        Card.Collapsed = !removed;
    }

    internal static string Message(bool streaming) => streaming
        ? L.T(".NET 已更新，HomePodCast 需要重新启动才能继续正常工作。重启时声音会中断几秒，然后自动重新连接。")
        : L.T(".NET 已更新，HomePodCast 需要重新启动才能继续正常工作。");
}
