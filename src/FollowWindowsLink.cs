using HomePodCast.Audio;

namespace HomePodCast;

/// <summary>The HomePod side as <see cref="FollowWindowsLink"/> sees it (TrayApp in the app, a fake in tests).</summary>
internal interface IFollowHomePod
{
    /// <summary>The HomePod volume in percent (kept while muted); null while unknown.</summary>
    double? Volume { get; }

    bool Muted { get; }

    /// <summary>The volume cap, percent (Windows 100 % stands for this).</summary>
    double Cap { get; }

    /// <summary>
    /// Send a volume that came from the Windows side. Never reported back as <see cref="FollowWindowsLink.HomePodChanged"/>.
    /// <paramref name="unmute"/>: a Windows volume change ends a HomePod mute; lowering at alignment keeps it.
    /// </summary>
    void SetVolume(double percent, bool unmute);

    void SetMuted(bool muted);
}

/// <summary>
/// <see cref="VolumeKeyMode.FollowWindows"/>: the Windows volume of the watched endpoint and the HomePod volume are one
/// volume, HomePod = Windows × cap / 100, and mute follows mute (Windows → HomePod).
/// <list type="bullet">
/// <item><see cref="Start"/> (connect, reconnect, mode switched on while streaming, another endpoint) and
/// <see cref="CapChanged"/> align both sides, only ever lowering one of them (<see cref="VolumeKeyRules.Align"/>);</item>
/// <item><see cref="WindowsChanged"/> (keys, Windows flyout): the HomePod follows;</item>
/// <item><see cref="HomePodChanged"/> (our sliders, the tray flyout, hotkeys): Windows follows.</item>
/// </list>
/// No echo: Windows is written through <c>writeWindowsLevel</c> with our own event context, which the endpoint watcher
/// drops (<see cref="WindowsVolumeFollower"/>), and volumes that came from Windows go to <see cref="IFollowHomePod.SetVolume"/>,
/// which is never reported back here. Inactive (nothing read or written) until <see cref="Start"/> and after
/// <see cref="Stop"/>, so nothing is written without a watched endpoint. UI thread only.
/// </summary>
internal sealed class FollowWindowsLink(IFollowHomePod homePod, Action<float> writeWindowsLevel)
{
    private EndpointState? _windows; // the watched endpoint as we last knew it; null = not following

    public bool Active => _windows != null;

    /// <summary>A fresh Windows baseline (see the class summary): align both sides, lowering only.</summary>
    public void Start(EndpointState windows)
    {
        _windows = windows;
        Align("start");
    }

    public void Stop() => _windows = null;

    /// <summary>Windows changed, not by us: the HomePod follows within the cap.</summary>
    public void WindowsChanged(EndpointState from, EndpointState to)
    {
        if (_windows == null) return;
        _windows = to;
        var action = VolumeKeyRules.Follow(from, to, homePod.Cap);
        if (action.Percent is { } percent) homePod.SetVolume(percent, unmute: true);
        else if (action.Mute is { } mute && mute != homePod.Muted) homePod.SetMuted(mute);
    }

    /// <summary>The HomePod volume was changed in our app: Windows goes to HomePod × 100 / cap (up or down).</summary>
    public void HomePodChanged()
    {
        if (_windows is not { } windows || homePod.Volume is not { } volume) return;
        float level = VolumeKeyRules.WindowsLevelFor(volume, homePod.Cap);
        if (MathF.Abs(level - windows.Level) < 0.0005f) return;
        Write(windows, level);
    }

    /// <summary>The cap changed: the mapping again, lowering only.</summary>
    public void CapChanged() => Align("cap");

    private void Align(string why)
    {
        if (_windows is not { } windows || homePod.Volume is not { } volume) return;
        var action = VolumeKeyRules.Align(windows.Level, volume, homePod.Cap);
        if (action.IsNone) return;
        Log.Info($"follow Windows ({why}): Windows {windows.Level:P0}, HomePod {volume:0.#}% at cap {homePod.Cap:0}% → " +
                 (action.HomePod is { } hp ? $"HomePod down to {hp:0.#}%" : $"Windows down to {action.WindowsLevel:P0}"));
        if (action.HomePod is { } lower) homePod.SetVolume(lower, unmute: false);
        if (action.WindowsLevel is { } level) Write(windows, level);
    }

    private void Write(EndpointState windows, float level)
    {
        _windows = windows with { Level = level };
        writeWindowsLevel(level);
    }
}
