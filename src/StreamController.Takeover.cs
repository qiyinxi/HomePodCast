using HomePodCast.Net;

namespace HomePodCast;

// Before every connect attempt (single speaker and group): is a speaker busy with another sender's session? Decided by
// TakeoverPolicy; only the user's 连接 click ever takes another sender's session over, and it asks first (UI/TrayApp.Takeover.cs).
// A HomePod held by its own home-theater Apple TV is taken over by every connect while the setting says 临时接管.
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

    /// <summary>设置 → 「HomePod 当 Apple TV 家庭影院音箱时」 (the UI keeps it in step with the config).</summary>
    public HomeTheaterMode HomeTheaterMode { get; set; } = HomeTheaterMode.TakeOver;

    private volatile bool _waitingForSpeaker;
    private TaskCompletionSource _recheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A connect waits for a busy speaker to be free: <see cref="State"/> is Idle meanwhile (连接 takes over, after asking),
    /// <see cref="StatusText"/> says which speaker and why, and nothing is captured.
    /// </summary>
    public bool WaitingForSpeaker => _waitingForSpeaker;

    /// <summary>
    /// The home-theater Apple TV this connection took its HomePod from (临时接管), for the 已连接 line; null otherwise. Set
    /// by the check that took it over, kept while the connection reconnects its own session.
    /// </summary>
    public string? TakenFromAppleTv => _takenFrom;

    private volatile string? _takenFrom;

    /// <summary>
    /// Something a waiting connect's check depends on changed (a scan found the Apple TV a HomePod belongs to): read the
    /// flag again now instead of at the next poll. Also a check reading right now reads once more; nothing else changes.
    /// </summary>
    public void RecheckSpeaker() =>
        Interlocked.Exchange(ref _recheck, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>"已连接 · 卧室", or after a home-theater takeover "已连接 · 卧室（临时从 Apple TV「客厅」接管，断开后交还）".</summary>
    private string ConnectedText(object? name) => _takenFrom is { } tv
        ? L.F("已连接 · {0}（临时从 Apple TV「{1}」接管，断开后交还）", name, tv)
        : L.F("已连接 · {0}", name);

    /// <summary>
    /// Returns once the connection may go ahead; waits (reading again every <see cref="BusyPollInterval"/>) while
    /// <see cref="TakeoverPolicy"/> says so. An Ask (a click whose speaker turned busy after the click's own check) waits
    /// too: only the UI can ask.
    /// </summary>
    /// <param name="endedBySpeaker">The speaker ended this connection's last session itself: a home theater is not taken back.</param>
    /// <param name="takenFrom">A confirmed takeover (仍然连接) of a HomePod its home-theater Apple TV held: that Apple TV's name.</param>
    private async Task WaitUntilFreeAsync(ConnectTrigger trigger, string key, IReadOnlyList<BusyTarget> targets, CancellationToken ct,
        bool endedBySpeaker = false, string? takenFrom = null)
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
                    if (trigger == ConnectTrigger.TakeOver)
                    {
                        _takenFrom = takenFrom;
                        Log.Info("takeover check skipped: the user chose to take the speaker over");
                    }
                    else Log.Debug("takeover check skipped: reconnecting our own session");
                    return;
                }
                var recheck = Volatile.Read(ref _recheck).Task;
                var reading = await reader.ReadAsync(targets, ct);
                var mode = HomeTheaterMode;
                var action = TakeoverPolicy.Decide(trigger, reading.State, ours, reading.Holder, mode, endedBySpeaker);
                if (action == TakeoverAction.Connect)
                {
                    bool homeTheater = reading.State == SpeakerBusy.Busy && TakeoverPolicy.TakesOverHomeTheater(reading.Holder, mode, endedBySpeaker);
                    _takenFrom = homeTheater ? reading.HomeTheater?.Name : null;
                    Log.Info(homeTheater
                        ? $"takeover check ({trigger}): {reading.Detail}; held by its home-theater Apple TV: taking it over for this connection " +
                          "(setting: take over temporarily; the Apple TV connects again when it next plays)"
                        : waitingSince is { } since
                            ? $"takeover check ({trigger}): {reading.Detail}; free after waiting {(UtcNow() - since).TotalSeconds:F0} s: connecting"
                            : $"takeover check ({trigger}): {reading.Detail}: connecting");
                    return;
                }

                // Wait (or an Ask nobody can answer here).
                waitingSince ??= UtcNow();
                bool own = TakeoverPolicy.WaitsForOwnSession(trigger, ours);
                var theater = !own && reading.Holder == BusyHolder.HomeTheater ? reading.HomeTheater : null;
                string names = string.Join(" / ", reading.BusyNames);
                string why = own ? "own" : theater != null ? "theater" : "other";
                if (logged != why)
                {
                    logged = why;
                    Log.Info(own
                        ? $"takeover check ({trigger}): {reading.Detail}; this PC's last session may still be active there " +
                          $"(up to {remaining.TotalSeconds:F0} s more): waiting for it to end"
                        : theater != null
                            ? $"takeover check ({trigger}): {reading.Detail}; held by its home-theater Apple TV " +
                              (endedBySpeaker ? "(it took the speaker back from us)" : "(setting: ask first)") +
                              $": waiting until the speaker is free (reading every {BusyPollInterval.TotalSeconds:F0} s)"
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
                    : theater != null
                        ? L.F("{0}是 Apple TV「{1}」的家庭影院音箱，Apple TV 正连着它", names, theater.Name)
                        : L.F("{0}正在被其他设备使用，空闲后自动连接", names));
                await Task.WhenAny(Task.Delay(own && remaining < BusyPollInterval ? remaining : BusyPollInterval, ct), recheck);
                ct.ThrowIfCancellationRequested();
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
