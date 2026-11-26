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
/// do not react to "Hey Siri"; ending the session brings Siri back at once. The app used to hold the session from the
/// morning's auto-connect until the night, so Siri never worked. So the session is only held while the PC plays something:
/// after <see cref="AppConfig.AppleTvIdleReleaseSeconds"/> without an audible packet it is ended, and the first sound on
/// the captured output connects it again (<see cref="ConnectTrigger.Automatic"/>: an Apple TV another sender uses is
/// waited for, never taken over). The reconnect costs the first second or so of that sound.
/// </summary>
public static class AppleTvIdlePolicy
{
    /// <summary>Longer than the quiet stretches of a film or a game, short enough to wait for before talking to Siri.</summary>
    public const int DefaultSeconds = 60;

    /// <summary>How often the app checks, and how often it reads the output's level while released.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>A hand-edited config's time: below zero is off (0), more than a day is a day.</summary>
    public static int ClampSeconds(int seconds) => Math.Clamp(seconds, 0, 24 * 60 * 60);

    /// <param name="seconds"><see cref="AppConfig.AppleTvIdleReleaseSeconds"/>; 0: never release.</param>
    /// <param name="appleTv">The selected speaker is an Apple TV.</param>
    /// <param name="streaming">Its stream is up (not connecting, waiting for a busy speaker or retrying).</param>
    /// <param name="released">This feature ended the stream and nothing has connected or disconnected since.</param>
    /// <param name="sinceConnect">Since the last connect was asked for: a new session gets the whole time before it is judged silent.</param>
    /// <param name="sinceSound">Since the last audible packet sent (<see cref="Audio.SoundActivity"/>).</param>
    /// <param name="soundNow">The captured output plays something audible right now (read only while released).</param>
    public static AppleTvIdleAction Decide(int seconds, bool appleTv, bool streaming, bool released,
        TimeSpan sinceConnect, TimeSpan sinceSound, bool soundNow)
    {
        if (released) return soundNow || seconds <= 0 ? AppleTvIdleAction.Reconnect : AppleTvIdleAction.None;
        if (seconds <= 0 || !appleTv || !streaming) return AppleTvIdleAction.None;
        var limit = TimeSpan.FromSeconds(seconds);
        return sinceConnect >= limit && sinceSound >= limit ? AppleTvIdleAction.Release : AppleTvIdleAction.None;
    }
}
