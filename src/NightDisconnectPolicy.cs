using System.Globalization;

namespace HomePodCast;

/// <summary>What 夜间自动断开 does now.</summary>
public enum NightAction
{
    None,

    /// <summary>Let the speaker go (TEARDOWN): nobody used the PC or played anything for long enough, inside the window.</summary>
    Disconnect,

    /// <summary>The window ended and the stream it ended is wanted again.</summary>
    Reconnect,
}

/// <summary>
/// 设置 → 「夜间自动断开」: the screens are turned off at night but the PC keeps running, and so did the stream, holding
/// the HomePod. Turned-off screens are no usable signal: an HDMI monitor switched off at its button often stays in Windows'
/// list, and a virtual display stays on anyway (2026-10-14, the maintainer's PC). So the window is set by the clock, and
/// "nobody is there" is no keyboard or mouse input and nothing audible sent (<see cref="Audio.SoundActivity"/>) for
/// <see cref="AppConfig.NightDisconnectIdleMinutes"/>: a film watched without touching the PC keeps playing, and so does a
/// stream while someone types. When the window ends, a stream this ended connects again as the startup auto-connect does
/// (<see cref="ConnectTrigger.Automatic"/>): a speaker another device plays to is waited for, never taken over; a HomePod
/// held by its home-theater Apple TV follows 设置 → 「HomePod 当 Apple TV 家庭影院音箱时」. Inside the window it stays
/// disconnected until the user clicks 连接. Times are local wall-clock time.
/// </summary>
public static class NightDisconnectPolicy
{
    public static readonly TimeOnly DefaultFrom = new(23, 0), DefaultUntil = new(7, 0);
    public const int DefaultIdleMinutes = 15;

    /// <summary>The choices of 「闲置多久后断开」, in minutes.</summary>
    public static readonly int[] IdleChoices = [5, 10, 15, 20, 30, 45, 60, 90, 120];

    /// <summary>The choices of the window's start and end: every full hour (a list of 48 half hours is too long to pick from).</summary>
    public static readonly TimeOnly[] TimeChoices = Enumerable.Range(0, 24).Select(h => new TimeOnly(h, 0)).ToArray();

    /// <summary>How often the app checks: the disconnect comes up to this much after the idle time, the reconnect after the window.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>A hand-edited config's idle time: zero or less is the default, more than a day is a day.</summary>
    public static int ClampIdleMinutes(int minutes) => minutes <= 0 ? DefaultIdleMinutes : Math.Min(minutes, 24 * 60);

    /// <summary>
    /// <paramref name="now"/> is in the window from <paramref name="from"/> (inclusive) to <paramref name="until"/>
    /// (exclusive), which may span midnight. The same time twice is no window at all.
    /// </summary>
    public static bool InWindow(TimeOnly from, TimeOnly until, TimeOnly now) =>
        from < until ? now >= from && now < until
        : from > until && (now >= from || now < until);

    /// <summary>
    /// How long the PC has been unused: since the later of the last keyboard or mouse input and the last audible packet.
    /// Input that Windows can't report (null) counts as just now, so nothing is ended on a guess.
    /// </summary>
    public static TimeSpan Idle(TimeSpan? sinceInput, TimeSpan sinceSound) =>
        sinceInput is { } input ? (input < sinceSound ? input : sinceSound) : TimeSpan.Zero;

    /// <param name="wanted">A connection is wanted: streaming, connecting, waiting for a busy speaker or retrying.</param>
    /// <param name="endedByNight">This feature ended the stream and nothing has connected or disconnected since.</param>
    /// <param name="idle">See <see cref="Idle"/>.</param>
    public static NightAction Decide(AppConfig cfg, TimeOnly now, bool wanted, bool endedByNight, TimeSpan idle)
    {
        if (!cfg.NightDisconnect) return NightAction.None;
        if (InWindow(cfg.NightDisconnectFrom, cfg.NightDisconnectUntil, now))
            return wanted && idle >= TimeSpan.FromMinutes(cfg.NightDisconnectIdleMinutes) ? NightAction.Disconnect : NightAction.None;
        return endedByNight && !wanted ? NightAction.Reconnect : NightAction.None;
    }

    /// <summary>"23:00": the settings and the status line show 24-hour times in every language.</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
