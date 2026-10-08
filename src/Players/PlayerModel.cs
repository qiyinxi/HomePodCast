using System.Globalization;

namespace HomePodCast.Players;

/// <summary>
/// Delay arithmetic for local players. HomePodCast knows how late the HomePod is heard relative to the
/// picture (videoDelayMs, the same number the local API reports). A player lines its picture up by playing
/// the audio that much EARLIER: an audio delay of −videoDelayMs (mpv, VLC and MPC all treat a negative
/// delay as "audio first").
/// </summary>
internal static class PlayerDelay
{
    /// <summary>Readings within this many ms count as the same value (players store seconds or 100 ns ticks).</summary>
    public const int ToleranceMs = 1;

    public static int TargetMs(int videoDelayMs) => -Math.Max(0, videoDelayMs);

    public static double ToSeconds(int ms) => ms / 1000.0;

    public static int FromSeconds(double seconds) => (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);

    /// <summary>Seconds as the players parse them: invariant, never an exponent ("-0.236", "0", "1.5").</summary>
    public static string SecondsText(int ms) => ToSeconds(ms).ToString("0.###", CultureInfo.InvariantCulture);

    public static bool Same(int a, int b) => Math.Abs(a - b) <= ToleranceMs;

    /// <summary>A delay for display: "-236" (invariant, so the minus sign never changes with the culture).</summary>
    public static string Text(int ms) => ms.ToString(CultureInfo.InvariantCulture);
}

internal enum PlayerKind { Mpv, Vlc, PotPlayer, MpcHc, MpcBe }

/// <summary>
/// What a player reported: its audio delay (null = it cannot say right now, e.g. VLC with nothing open)
/// and, where the player has one, the item it plays (VLC starts every new item at its default delay).
/// </summary>
internal readonly record struct PlayerReading(int? DelayMs, string? Item = null);

/// <summary>One running player HomePodCast can talk to. Only reads and sets the audio delay.</summary>
internal interface IPlayerEndpoint
{
    /// <summary>Stable identity across scans: kind, process id and address.</summary>
    string Key { get; }
    PlayerKind Kind { get; }
    string Name { get; }
    Task<PlayerReading> ReadAsync(CancellationToken ct);
    Task WriteAsync(int delayMs, CancellationToken ct);
}

/// <summary>Why a running player cannot be adjusted (shown on the home page).</summary>
internal enum PlayerProblem
{
    /// <summary>mpv without input-ipc-server, VLC without the web interface.</summary>
    NoInterface,
    /// <summary>VLC's web interface has no password (VLC refuses every request then).</summary>
    NoPassword,
    /// <summary>VLC rejected the password from its own settings.</summary>
    LoginFailed,
    /// <summary>The player has no interface for the audio delay (PotPlayer, MPC-HC, MPC-BE).</summary>
    Manual,
}

/// <summary>A player's answer that says it will not take commands (VLC 401/403).</summary>
internal sealed class PlayerAccessException(PlayerProblem problem, string message) : Exception(message)
{
    public PlayerProblem Problem { get; } = problem;
}

internal readonly record struct PlayerNote(PlayerKind Kind, string Name, PlayerProblem Problem);

internal sealed record PlayerScan(IReadOnlyList<IPlayerEndpoint> Endpoints, IReadOnlyList<PlayerNote> Notes)
{
    public static readonly PlayerScan Empty = new([], []);
}

internal interface IPlayerScanner
{
    Task<PlayerScan> ScanAsync(CancellationToken ct);
}

internal enum PlayerState
{
    /// <summary>Set to the target (DelayMs).</summary>
    Applied,
    /// <summary>Nothing open yet (VLC reports no delay without an input); adjusted once it plays.</summary>
    Waiting,
    /// <summary>Someone changed the delay in the player after we set it; left alone (DelayMs = their value).</summary>
    UserChanged,
    /// <summary>Talking to the player failed.</summary>
    Failed,
    NoInterface,
    NoPassword,
    LoginFailed,
    /// <summary>Set it by hand (DelayMs = the value to use).</summary>
    Manual,
}

internal readonly record struct PlayerStatus(PlayerKind Kind, string Name, PlayerState State, int? DelayMs = null);

/// <summary>Which executables are which player (keys as ProcessTree gives them: lower case, no ".exe").</summary>
internal static class PlayerProcesses
{
    public static bool TryIdentify(string exeKey, out PlayerKind kind, out string name)
    {
        (kind, name) = exeKey switch
        {
            "mpv" => (PlayerKind.Mpv, "mpv"),
            "mpvnet" => (PlayerKind.Mpv, "mpv.net"),
            "vlc" => (PlayerKind.Vlc, "VLC"),
            "potplayermini64" or "potplayermini" or "potplayer64" or "potplayer" => (PlayerKind.PotPlayer, "PotPlayer"),
            "mpc-hc64" or "mpc-hc" => (PlayerKind.MpcHc, "MPC-HC"),
            "mpc-be64" or "mpc-be" => (PlayerKind.MpcBe, "MPC-BE"),
            _ => ((PlayerKind)(-1), ""),
        };
        return name.Length > 0;
    }
}
