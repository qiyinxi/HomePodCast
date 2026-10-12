using HomePodCast.Net;

namespace HomePodCast;

/// <summary>Why a connection is being made; it decides what a busy speaker means for it (<see cref="TakeoverPolicy"/>).</summary>
public enum ConnectTrigger
{
    /// <summary>The user clicked 连接: taking over a speaker another device is playing to needs their OK first.</summary>
    Manual,

    /// <summary>The user answered 仍然连接: this connection takes the speaker over, also when it retries.</summary>
    TakeOver,

    /// <summary>Nobody clicked: the auto-connect at startup, the first speaker found, another speaker picked in the list.</summary>
    Automatic,

    /// <summary>
    /// Our own session again: a retry after a drop, a restart for a new latency, scene or speaker layout, wake from
    /// sleep, a network change. The speaker may still show our own session as active.
    /// </summary>
    Reconnect,
}

/// <summary>What a speaker's "receiver session is active" status flag said (for a group: any member).</summary>
public enum SpeakerBusy { Free, Busy, Unknown }

public enum TakeoverAction
{
    Connect,

    /// <summary>Ask the user first (only a click can be asked; anywhere else this means <see cref="Wait"/>).</summary>
    Ask,

    /// <summary>Don't connect yet: read the flag again later and connect once the speaker is free.</summary>
    Wait,
}

/// <summary>设置 → 「HomePod 当 Apple TV 家庭影院音箱时」: what a HomePod held by its own home-theater Apple TV means.</summary>
public enum HomeTheaterMode
{
    /// <summary>
    /// 临时接管 (the default): every connect takes the HomePod over from its Apple TV, as TuneBlade did. The Apple TV has
    /// no sound through it until this PC disconnects (TEARDOWN); it connects again the next time it plays.
    /// </summary>
    TakeOver,

    /// <summary>先询问: like any other sender's session (a click asks, naming the Apple TV; automatic connects wait).</summary>
    Ask,
}

/// <summary>
/// Whether to connect to a speaker whose status flag says a session is active. AirPlay lets a new sender take a speaker
/// over: connecting while an iPhone plays to it cuts the iPhone off. So nothing but the user's own click ever takes over
/// another sender's session, and the click asks first. The flag may also be our own session's, still active on the speaker
/// after a drop or a restart; a reconnect of our own session is never held back for it.
/// <para>
/// A HomePod that is an Apple TV's home-theater speaker shows the flag all the time, idle or not: the Apple TV keeps its
/// session. When every busy speaker is held by its own home-theater Apple TV (<see cref="BusyHolder.HomeTheater"/>, from
/// the group fields: <see cref="Net.HomeTheater"/>) and the setting is <see cref="HomeTheaterMode.TakeOver"/>, every
/// trigger connects: the HomePod is taken over for as long as this PC is connected. Not when the speaker ended this
/// connection's own session itself (the Apple TV took it back): then nothing fights it.
/// </para>
/// <list type="table">
/// <item>Free or Unknown (unreadable: offline, timeout): connect. The connection itself then fails, and the next attempt
///   reads again.</item>
/// <item>Held by its home-theater Apple TV, 临时接管: connect (but a retry after the speaker ended our session → Wait).</item>
/// <item>Manual: busy with someone else's session (or a home theater with 先询问) → Ask; busy while ours was alive just
///   now → connect.</item>
/// <item>TakeOver: connect.</item>
/// <item>Automatic: busy → Wait. While ours was alive just now the wait is for our own session to end, so it is bounded by
///   <see cref="OwnSession"/>'s window; after it the flag is someone else's, and the wait goes on until the speaker is free.</item>
/// <item>Reconnect: ours was alive just now → connect without reading; otherwise (a long sleep, the speaker ended our
///   session itself) like Automatic.</item>
/// </list>
/// </summary>
public static class TakeoverPolicy
{
    /// <summary>The flag need not be read at all: the answer is Connect whatever it says.</summary>
    public static bool SkipsCheck(ConnectTrigger trigger, bool oursRecently) =>
        trigger == ConnectTrigger.TakeOver || trigger == ConnectTrigger.Reconnect && oursRecently;

    /// <param name="holder">Who holds the busy speakers (<see cref="BusyReading.Holder"/>).</param>
    /// <param name="mode">The home-theater setting.</param>
    /// <param name="endedBySpeaker">The speaker ended this connection's last session itself (another sender took it).</param>
    public static TakeoverAction Decide(ConnectTrigger trigger, SpeakerBusy busy, bool oursRecently,
        BusyHolder holder = BusyHolder.OtherSender, HomeTheaterMode mode = HomeTheaterMode.Ask, bool endedBySpeaker = false)
    {
        if (busy != SpeakerBusy.Busy || SkipsCheck(trigger, oursRecently)) return TakeoverAction.Connect;
        if (TakesOverHomeTheater(holder, mode, endedBySpeaker)) return TakeoverAction.Connect;
        return trigger switch
        {
            ConnectTrigger.Manual => oursRecently ? TakeoverAction.Connect : TakeoverAction.Ask,
            _ => TakeoverAction.Wait, // Automatic (ours or not), Reconnect long after our session
        };
    }

    /// <summary>
    /// A busy reading that 临时接管 connects through: every busy speaker is held by its home-theater Apple TV, and the
    /// speaker didn't just end our own session (the Apple TV taking it back is never fought).
    /// </summary>
    public static bool TakesOverHomeTheater(BusyHolder holder, HomeTheaterMode mode, bool endedBySpeaker = false) =>
        holder == BusyHolder.HomeTheater && mode == HomeTheaterMode.TakeOver && !endedBySpeaker;

    /// <summary>
    /// The trigger of the next attempt of one connection's retry loop: once it has streamed, a retry is our own session
    /// again; before that it keeps the first one's (a startup that never got through still never takes over).
    /// </summary>
    public static ConnectTrigger RetryTrigger(ConnectTrigger first, bool streamed) => streamed ? ConnectTrigger.Reconnect : first;

    /// <summary>A waiting connect waits for our own last session to end (it may still hold the flag), not for another sender.</summary>
    public static bool WaitsForOwnSession(ConnectTrigger trigger, bool oursRecently) =>
        oursRecently && trigger == ConnectTrigger.Automatic;
}

/// <summary>This PC's last session as saved in the config, so a restart after a crash knows the flag may still be ours.</summary>
/// <param name="Key">The speaker selection (<see cref="OwnSession.KeyOf(string)"/>).</param>
/// <param name="AliveUtc">When it was last known to be alive: its end, or while streaming the last heartbeat.</param>
/// <param name="Clean">We ended it ourselves (TEARDOWN): the speaker drops it at once.</param>
public sealed record OwnSessionRecord(string Key, DateTime AliveUtc, bool Clean);

/// <summary>
/// "Ours recently" for <see cref="TakeoverPolicy"/>: whether the speaker's active-session flag may still be our own
/// session's. True while we stream; after an end we caused (Stop: TEARDOWN sent) for <see cref="CleanWindow"/>; after
/// an end we didn't (network gone, the PC went to sleep, the app was killed: the speaker keeps the session until it
/// times out) for <see cref="StaleWindow"/>; never after the speaker ended it itself (another sender took over). Times are
/// wall-clock UTC, so a sleep or a restart counts. Thread-safe.
/// </summary>
public sealed class OwnSession
{
    /// <summary>
    /// How long after an unclean end the flag may still be ours: the speaker times a silent sender's session out (the
    /// exact time is not published; a minute or two). Persisted heartbeats are up to a minute old, which this covers too.
    /// </summary>
    public static readonly TimeSpan StaleWindow = TimeSpan.FromMinutes(3);

    /// <summary>After our own TEARDOWN the speaker ends the session at once; a little slack for the flag to follow.</summary>
    public static readonly TimeSpan CleanWindow = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private string? _key;
    private DateTime _aliveUtc;
    private bool _live, _clean;

    /// <summary>The selection key of a single speaker.</summary>
    public static string KeyOf(string deviceId) => GroupPlan.Key(deviceId);

    /// <summary>The selection key of a group: the pair, or every member of a multi-room group.</summary>
    public static string KeyOf(GroupPlan plan) => plan.Kind == GroupKind.StereoPair && plan.PairTsid != null
        ? KeyOf(StereoPairs.PairId(plan.PairTsid))
        : string.Join("+", plan.Members.Select(m => KeyOf(m.DeviceId)));

    /// <summary>The key of what the config selects (null: nothing selected).</summary>
    public static string? KeyOf(AppConfig cfg) =>
        GroupPlan.FromConfig(cfg) is { } plan ? KeyOf(plan) : cfg.DeviceId is { } id ? KeyOf(id) : null;

    /// <summary>A session to <paramref name="key"/> streams.</summary>
    public void Began(string key, DateTime now)
    {
        lock (_lock) (_key, _aliveUtc, _live, _clean) = (key, now, true, false);
    }

    /// <summary>The streaming session ended; <paramref name="clean"/>: we stopped it (TEARDOWN). Nothing if none was live.</summary>
    public void Ended(DateTime now, bool clean)
    {
        lock (_lock)
        {
            if (!_live) return;
            (_aliveUtc, _live, _clean) = (now, false, clean);
        }
    }

    /// <summary>The PC goes to sleep: the session is as good as ended now, and not by us (the reconnect at wake ends it later).</summary>
    public void Suspending(DateTime now) => Ended(now, clean: false);

    /// <summary>The speaker ended our session itself (another sender took over): a set flag is not ours.</summary>
    public void Forget()
    {
        lock (_lock) (_key, _live) = (null, false);
    }

    public bool Live
    {
        get { lock (_lock) return _live; }
    }

    /// <summary>A session to <paramref name="key"/> streams right now (not merely recently, not another selection's).</summary>
    public bool IsLiveFor(string? key)
    {
        lock (_lock) return _live && key != null && _key == key;
    }

    public bool IsRecent(string? key, DateTime now) => Remaining(key, now) > TimeSpan.Zero;

    /// <summary>How much longer the flag may be ours (zero: not ours; <see cref="TimeSpan.MaxValue"/> while live).</summary>
    public TimeSpan Remaining(string? key, DateTime now)
    {
        lock (_lock)
        {
            if (key == null || _key != key) return TimeSpan.Zero;
            if (_live) return TimeSpan.MaxValue;
            var elapsed = now - _aliveUtc;
            if (elapsed < TimeSpan.Zero) return TimeSpan.Zero; // the clock went back: don't guess
            var left = (_clean ? CleanWindow : StaleWindow) - elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>What to save: while streaming, alive now and not cleanly ended (a crash leaves exactly that).</summary>
    public OwnSessionRecord? Record(DateTime now)
    {
        lock (_lock) return _key == null ? null : new OwnSessionRecord(_key, _live ? now : _aliveUtc, !_live && _clean);
    }

    /// <summary>The previous run's session (from the config): never live any more.</summary>
    public void Restore(OwnSessionRecord? record)
    {
        lock (_lock)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return;
            (_key, _aliveUtc, _live, _clean) = (record.Key, DateTime.SpecifyKind(record.AliveUtc, DateTimeKind.Utc), false, record.Clean);
        }
    }
}
