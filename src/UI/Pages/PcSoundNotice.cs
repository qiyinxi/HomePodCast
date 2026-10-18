using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 首页's 「电脑也在出声」 (experimental since 1.2, with 推流时电脑静音): while streaming, the pushed sound also plays on the
/// PC's speakers or headphones (Audio.PcMute.HintShown). User report 2026-10-18: 「连接后电脑和 HomePod 都在放」, and the
/// way out was only in the README. 「只用 HomePod 播放」 turns 推流时电脑静音 on; 「不再提示」 hides it for good. Collapsed
/// otherwise; nothing changes without a click.
/// </summary>
internal sealed class PcSoundNotice
{
    /// <param name="onlyHomePod">「只用 HomePod 播放」 (TrayApp.PlayOnlyOnHomePod).</param>
    /// <param name="dismiss">「不再提示」 (TrayApp.DismissPcSoundHint).</param>
    public PcSoundNotice(Action onlyHomePod, Action dismiss)
    {
        var icon = new HomePage.GlyphLabel(Glyph.Volume, accent: true);
        var texts = Ui.Stack(2, Title, Text);
        var buttons = Ui.Row(8, null, OnlyHomePod, Dismiss);
        buttons.Wrap = true; // long button texts (German) at the window's minimum width
        Card = Ui.Card(Ui.Row(12, texts, icon, texts), buttons);
        Card.Collapsed = true;
        OnlyHomePod.Click += (_, _) => onlyHomePod();
        Dismiss.Click += (_, _) => dismiss();
    }

    public Card Card { get; }
    internal TextBlock Title { get; } = new(L.T("电脑也在出声"), TextStyle.BodyStrong, wrap: true);
    internal TextBlock Text { get; } = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    internal FluentButton OnlyHomePod { get; } = new(L.T("只用 HomePod 播放"), ButtonKind.Primary);
    internal FluentButton Dismiss { get; } = new(L.T("不再提示"), ButtonKind.Subtle);

    /// <param name="shown">TrayApp.PcSoundHintShown.</param>
    /// <param name="followsWindows">「HomePod 跟随 Windows 音量」 is chosen: why the PC can't simply be muted.</param>
    public void Show(bool shown, bool followsWindows)
    {
        if (shown && Message(followsWindows) is var text && Text.Text != text) Text.Text = text;
        if (Card.Collapsed == shown) Card.Collapsed = !shown;
    }

    internal static string Message(bool followsWindows) => followsWindows
        ? L.T("HomePod 跟随 Windows 音量，所以电脑这边也在放。")
        : L.T("推送到 HomePod 的声音，电脑这边也在放。");
}
