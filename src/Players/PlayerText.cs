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
            PlayerState.NoPassword => L.F("{0}：没有找到网页接口密码（见「设置」）", s.Name),
            PlayerState.LoginFailed => L.F("{0}：密码错误，请在设置里重新输入", s.Name),
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

    /// <summary>
    /// The line under 「VLC 网页接口密码」 in 设置: which password VLC accepted, or that it refused them, while
    /// the 影视 scene adjusts VLC; otherwise whether one is saved. Never the password itself.
    /// </summary>
    /// <returns>The text, and whether it asks the user to do something.</returns>
    public static (string Text, bool Problem) VlcPasswordStatus(IReadOnlyList<PlayerStatus> statuses, bool manualSaved)
    {
        var vlc = statuses.Where(s => s.Kind == PlayerKind.Vlc).ToList();
        if (vlc.Any(s => s.State == PlayerState.LoginFailed)) return (L.T("密码错误，请在设置里重新输入"), true);
        if (vlc.Any(s => s.Password == PasswordSource.Manual)) return (L.T("使用手动输入的密码"), false);
        if (vlc.Any(s => s.Password == PasswordSource.Player)) return (L.T("使用 VLC 设置里的密码"), false);
        if (vlc.Any(s => s.State == PlayerState.NoPassword)) return (L.T("没有找到密码，请在这里输入"), true);
        return manualSaved
            ? (L.T("已保存手动输入的密码（加密保存，只有当前 Windows 用户能解密）。"), false)
            : (L.T("先用 VLC 设置里的密码；读不到或不对时用这里输入的密码。密码加密保存在本机。"), false);
    }
}
