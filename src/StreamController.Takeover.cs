using HomePodCast.Net;

namespace HomePodCast;

// Before every connect attempt (single speaker and group): is a speaker busy with another sender's session? Decided by
// TakeoverPolicy; only the user's 连接 click ever takes another sender's session over, and it asks first (UI/TrayApp.Takeover.cs).
public sealed partial class StreamController
{
    /// <summary>Reads the speakers' active-session flag. Null: no check at all (the command line, tests about other things).</summary>
    internal ISpeakerBusyReader? BusyReader { get; set; }

    /// <summary>How often a waiting connect reads the flag again.</summary>
    internal TimeSpan BusyPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Wall-clock time for <see cref="OwnSession"/> (tests move it).</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Whether a set flag may be our own session's (saved across restarts by the UI).</summary>
    public OwnSession OwnSession { get; } = new();

    private volatile bool _waitingForSpeaker;

    /// <summary>
    /// A connect waits for a busy speaker to be free: <see cref="State"/> is Idle meanwhile (连接 takes over, after asking),
    /// <see cref="StatusText"/> says which speaker and why, and nothing is captured.
    /// </summary>
    public bool WaitingForSpeaker => _waitingForSpeaker;

    /// <summary>
    /// Returns once the connection may go ahead; waits (reading again every <see cref="BusyPollInterval"/>) while
    /// <see cref="TakeoverPolicy"/> says so. An Ask (a click whose speaker turned busy after the click's own check) waits
    /// too: only the UI can ask.
    /// </summary>
    private async Task WaitUntilFreeAsync(ConnectTrigger trigger, string key, IReadOnlyList<BusyTarget> targets, CancellationToken ct)
    {
        if (BusyReader is not { } reader) return;
        DateTime? waitingSince = null;
        string? logged = null;
        try
        {
            while (true)
            {
                var remaining = OwnSession.Remaining(key, UtcNow());
                bool ours = remaining > TimeSpan.Zero;
                if (TakeoverPolicy.SkipsCheck(trigger, ours))
                {
                    if (trigger == ConnectTrigger.TakeOver) Log.Info("takeover check skipped: the user chose to take the speaker over");
                    else Log.Debug("takeover check skipped: reconnecting our own session");
                    return;
                }
                var reading = await reader.ReadAsync(targets, ct);
                var action = TakeoverPolicy.Decide(trigger, reading.State, ours);
                if (action == TakeoverAction.Connect)
                {
                    Log.Info(waitingSince is { } since
                        ? $"takeover check ({trigger}): {reading.Detail}; free after waiting {(UtcNow() - since).TotalSeconds:F0} s: connecting"
                        : $"takeover check ({trigger}): {reading.Detail}: connecting");
                    return;
                }

                // Wait (or an Ask nobody can answer here).
                waitingSince ??= UtcNow();
                bool own = TakeoverPolicy.WaitsForOwnSession(trigger, ours);
                string names = string.Join(" / ", reading.BusyNames);
                if (logged != (own ? "own" : "other"))
                {
                    logged = own ? "own" : "other";
                    Log.Info(own
                        ? $"takeover check ({trigger}): {reading.Detail}; this PC's last session may still be active there " +
                          $"(up to {remaining.TotalSeconds:F0} s more): waiting for it to end"
                        : $"takeover check ({trigger}): {reading.Detail}; another sender's session: waiting until the speaker is free " +
                          $"(reading every {BusyPollInterval.TotalSeconds:F0} s)");
                }
                if (!_waitingForSpeaker)
                {
                    _waitingForSpeaker = true;
                    DisposeCapture(); // nothing to send meanwhile; routed apps play on this PC again
                }
                Set(StreamState.Idle, own
                    ? L.F("{0}可能还保留着这台电脑上次的连接，结束后自动连接（最多再等 {1} 秒）", names, (int)Math.Ceiling(remaining.TotalSeconds))
                    : L.F("{0}正在被其他设备使用，空闲后自动连接", names));
                await Task.Delay(own && remaining < BusyPollInterval ? remaining : BusyPollInterval, ct);
            }
        }
        finally
        {
            if (_waitingForSpeaker)
            {
                _waitingForSpeaker = false;
                if (!ct.IsCancellationRequested) Set(StreamState.Connecting, L.T("正在连接…"));
            }
        }
    }
}
