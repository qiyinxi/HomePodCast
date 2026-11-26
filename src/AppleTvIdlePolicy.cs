namespace HomePodCast;

/// <summary>What the Apple TV idle release does now.</summary>
public enum AppleTvIdleAction
{
    None,

    /// <summary>Nothing audible was sent for long enough: end the session, so the home-theater HomePods hear "Hey Siri" again.</summary>
    Release,

    /// <summary>The PC plays something again (or the release was turned off): connect as the startup auto-connect does.</summary>
    Reconnect,
}

/// <summary>
/// An Apple TV takes this app's stream as a screen-mirroring session (AirPlayClient.AppleTvPtp.cs), the only setup found
/// that plays through its home-theater HomePods with low latency. 2026-11-26, the maintainer's Apple TV 4K with a HomePod
/// pair (tvOS / HomePod OS 24K157): for as long as that session is up, silent or not, the HomePods keep their light on and
/// do not react to "Hey Siri"; ending the session brings Siri back at once. The app holds the session from the morning's
/// auto-connect until the night, so Siri never works. With 设置's 「Apple TV 无声时断开」 on
/// (<see cref="AppConfig.AppleTvIdleRelease"/>) the session is only held while the PC plays something: after
/// <see cref="AppConfig.AppleTvIdleReleaseSeconds"/> without an audible packet it is ended, and the first sound on the
/// captured output connects it again (<see cref="ConnectTrigger.Automatic"/>: an Apple TV another sender uses is waited
/// for, never taken over). The reconnect costs the first second or so of that sound.
/// 2026-11-26, 1.5.0 beta 4 had it on for everyone: the maintainer found the connecting odd and asked for a switch that is
/// off unless turned on.
/// </summary>
public static class AppleTvIdlePolicy
{
    /// <summary>Longer than the quiet stretches of a film or a game, short enough to wait for before talking to Siri.</summary>
    public const int DefaultSeconds = 60;

    /// <summary>How often the app checks, and how often it reads the output's level while released.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>A hand-edited config's time: below zero is off (0), more than a day is a day.</summary>
    public static int ClampSeconds(int seconds) => Math.Clamp(seconds, 0, 24 * 60 * 60);

    /// <summary>The time in use: 0 (never) unless the setting is on.</summary>
    public static int SecondsFor(AppConfig config) => config.AppleTvIdleRelease ? ClampSeconds(config.AppleTvIdleReleaseSeconds) : 0;

    /// <param name="seconds"><see cref="SecondsFor"/>; 0: never release.</param>
    /// <param name="appleTv">The selected speaker is an Apple TV.</param>
    /// <param name="streaming">Its stream is up (not connecting, waiting for a busy speaker or retrying).</param>
    /// <param name="released">This feature ended the stream and nothing has connected or disconnected since.</param>
    /// <param name="streamingFor">How long the stream has been up (<see cref="AppleTvIdleWatch.Observe"/>): a new session gets the whole time before it is judged silent.</param>
    /// <param name="sinceSound">Since the last audible packet sent (<see cref="Audio.SoundActivity"/>).</param>
    /// <param name="soundNow">The captured output plays something audible right now (read only while released).</param>
    public static AppleTvIdleAction Decide(int seconds, bool appleTv, bool streaming, bool released,
        TimeSpan streamingFor, TimeSpan sinceSound, bool soundNow)
    {
        if (released) return soundNow || seconds <= 0 ? AppleTvIdleAction.Reconnect : AppleTvIdleAction.None;
        if (seconds <= 0 || !appleTv || !streaming) return AppleTvIdleAction.None;
        var limit = TimeSpan.FromSeconds(seconds);
        return streamingFor >= limit && sinceSound >= limit ? AppleTvIdleAction.Release : AppleTvIdleAction.None;
    }
}

/// <summary>
/// What the idle release has seen of the stream, check by check: how long it has been up, and whether a stream it ended
/// is still its own to connect again. Both come from the stream's state and not from the places that connect.
/// 2026-11-26, 1.5.0 beta 4 counted from "the last connect asked for" and missed a way to connect (混音器's reconnect of
/// one destination): a session the maintainer had just started by hand was ended 60 ms after it came up, because the
/// connect before it was a minute old; and a stream connected that way while released would have been connected once
/// more at the next sound.
/// A struct, kept in a field that is not readonly: the tests build TrayApp without running its constructor
/// (TestApp.Create), where a class's field would be null at the first connect.
/// </summary>
public struct AppleTvIdleWatch
{
    private long? _notStreamingMs; // the last check at which the stream was not up
    private bool _stopped;         // the stream the release ended has been seen idle

    /// <summary>The idle release ended the stream, and connects it at the next sound.</summary>
    public bool Released { get; private set; }

    /// <summary>One check. Returns how long the stream has been up without a break (zero when it is not).</summary>
    /// <param name="streaming">The stream is up.</param>
    /// <param name="idle">Nothing is connected or connecting.</param>
    public TimeSpan Observe(long nowMs, bool streaming, bool idle)
    {
        if (!streaming || _notStreamingMs == null) _notStreamingMs = nowMs;
        if (Released)
        {
            if (idle) _stopped = true;
            else if (_stopped) Released = false; // connected some other way since: that connection is not ours to redo
        }
        return TimeSpan.FromMilliseconds(nowMs - _notStreamingMs.Value);
    }

    /// <summary>The release ends the stream now (it takes a moment to stop).</summary>
    public void Release()
    {
        Released = true;
        _stopped = false;
    }

    /// <summary>A connect or disconnect that is not the release's own: it no longer connects again.</summary>
    public void Reset() => Released = false;
}
