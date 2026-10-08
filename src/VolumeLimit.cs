using HomePodCast.Net;

namespace HomePodCast;

/// <summary>
/// The user's ceiling for the speaker's own volume (AppConfig.VolumeCapPercent; 100 = no limit). Applied
/// where volumes are decided (StreamController) and again where they are sent (AirPlayClient.SetVolumeDb),
/// so nothing — connect, restore, reconnect, hotkeys, forwarded volume keys — can go above it.
/// </summary>
public static class VolumeLimit
{
    public const int MinCap = 10;

    /// <summary>A usable cap: 10..100 (a lower one would make the speaker look broken).</summary>
    public static int NormalizeCap(int cap) => Math.Clamp(cap, MinCap, 100);

    public static double Clamp(double percent, double cap) =>
        double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, Math.Clamp(cap, 0, 100));

    /// <summary>Same in the speaker's dB scale (AirPlayClient.PercentToDb); mute (-144 dB) always passes.</summary>
    public static double ClampDb(double db, double cap) =>
        double.IsNaN(db) ? AirPlayClient.PercentToDb(0) : Math.Min(db, AirPlayClient.PercentToDb(Math.Clamp(cap, 0, 100)));
}
