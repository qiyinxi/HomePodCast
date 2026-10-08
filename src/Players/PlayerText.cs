namespace HomePodCast.Players;

/// <summary>What the home page says about each player.</summary>
internal static class PlayerText
{
    public static string Line(PlayerStatus s)
    {
        string ms = s.DelayMs is { } d ? PlayerDelay.Text(d) : "?";
        return s.State switch
        {
            PlayerState.Applied => L.F("{0}：已设为 {1} ms", s.Name, ms),
            PlayerState.Waiting => L.F("{0}：开始播放后自动调整", s.Name),
            PlayerState.UserChanged => L.F("{0}：已被手动改为 {1} ms", s.Name, ms),
            PlayerState.NoInterface when s.Kind == PlayerKind.Vlc => L.F("{0}：未开启网页接口（见「设置」）", s.Name),
            PlayerState.NoInterface => L.F("{0}：未开启 IPC（见「设置」）", s.Name),
            PlayerState.NoPassword => L.F("{0}：网页接口没有设置密码", s.Name),
            PlayerState.LoginFailed => L.F("{0}：网页接口密码不对", s.Name),
            PlayerState.Manual => L.F("{0}：请手动设为 {1} ms", s.Name, ms),
            _ => L.F("{0}：调整失败", s.Name),
        };
    }

    /// <summary>
    /// The 影视 hint under the scene card: the old manual hint when the setting is off; otherwise one line per
    /// running player (while streaming) and the value for every other player.
    /// </summary>
    public static string MovieHint(bool enabled, bool streaming, IReadOnlyList<PlayerStatus> statuses, int lagMs)
    {
        if (!enabled) return L.F("本地播放器：把音频延迟设为 -{0} ms；网页视频：用浏览器插件自动对齐。", lagMs);
        var lines = new List<string>();
        if (!streaming)
        {
            lines.Add(L.T("推流时自动调整 mpv、VLC 的音频延迟。"));
        }
        else
        {
            lines.AddRange(statuses.Distinct().Select(Line));
            if (!statuses.Any(s => s.Kind is PlayerKind.Mpv or PlayerKind.Vlc)) lines.Add(L.T("没有发现开着的 mpv、VLC。"));
        }
        lines.Add(L.F("其他播放器：把音频延迟设为 -{0} ms；网页视频：用浏览器插件自动对齐。", lagMs));
        return string.Join("\n", lines);
    }
}
