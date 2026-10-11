using System.Collections.Concurrent;

namespace HomePodCast.Audio;

internal enum SendVolumeStatus { Waiting, Available, MutedOrZero, DifferentSessionLevels, BelowMinimum }

/// <summary>One tracked session of a send target as a check read it: its logical Windows level and its mute.</summary>
internal readonly record struct SessionLevel(float Logical, bool Muted);

/// <summary>
/// What one check of a send target found (<see cref="SendVolumeGain.Evaluate"/>): the status the mixer shows, the
/// common level of the audible sessions (null: nothing to divide out) and the highest level any session plays at
/// (0: all silent; 1: not known).
/// </summary>
internal readonly record struct SendCheck(SendVolumeStatus Status, float? Common, float Loudest);

/// <summary>
/// Process loopback is after the Windows session volume. Dividing that volume out gives an independent HomePod
/// send only when every audible session of the app has the same logical level of at least 1 %. Mixed levels, levels
/// below 1 %, or a check that is missing or stale leave the inverse off: the HomePod then gets Windows level x send,
/// quieter than set. A level change reaches the capture ~35-50 ms after it is made (and after its volume event), so
/// for <c>settleDelay</c> after it the inverse of the highest level the audio still on its way may be at is used,
/// and until a check that began after a volume event, the level that event reported is covered too: never louder
/// than set, for the audio at the old level or at the new one. The router and the volume events write; the capture
/// thread reads without locks or allocations.
/// </summary>
internal sealed class SendVolumeGain(long settleDelay, long verifyWindow)
{
    /// <summary>Below 1 % the inverse (more than x100, +40 dB) is never applied.</summary>
    public const float MinLevel = 0.01f;

    private const float Tolerance = 0.0001f;
    private const int MaxRecent = 16;

    /// <param name="Level">The common level of the audible sessions from the last check (0: none usable).</param>
    /// <param name="Loudest">The highest level a session played at in the last check (1: not known).</param>
    /// <param name="Reported">The highest level volume events reported since the last check that saw them (0: none).</param>
    /// <param name="Recent">Levels audio still on its way to the capture may be at, each until its time.</param>
    /// <param name="ChangedAt">When the last volume event arrived.</param>
    private sealed record State(float Level, float Loudest, float Reported, (float Level, long Until)[] Recent, long ChangedAt);

    private readonly ConcurrentDictionary<uint, State> _states = new();
    private readonly ConcurrentDictionary<uint, long> _verifiedAt = new(); // updated in place: no allocation per check

    /// <summary>
    /// A volume event (any thread): one session of <paramref name="root"/> now plays at <paramref name="level"/>
    /// (logical; null: not known, taken as full level). The audio from before the change is covered for the settle
    /// delay, the reported level until a check that began after this event. Before the first check of the root the
    /// level is only remembered (nothing is amplified before a check anyway).
    /// </summary>
    public void Changed(uint root, long now, float? level = null)
    {
        float reported = Clean(level) ?? 1f;
        _states.AddOrUpdate(root,
            _ => new State(0f, reported, reported, [], now),
            (_, old) => old with
            {
                Reported = Math.Max(old.Reported, reported),
                Recent = Add(old.Recent, Math.Max(old.Loudest, old.Reported), now + settleDelay, now),
                ChangedAt = Math.Max(old.ChangedAt, now),
            });
    }

    /// <summary>
    /// A check of every tracked session of <paramref name="root"/> that began reading at <paramref name="now"/>.
    /// The first check of a root takes the audio in flight to be at the levels it read; a later one that finds
    /// them moved covers the previous levels for the settle delay.
    /// </summary>
    public void Verify(uint root, SendCheck check, long now)
    {
        float level = check.Common is { } c && Clean(c) is { } l && l >= MinLevel ? l : 0f;
        float loudest = Clean(check.Loudest) ?? 1f;
        _states.AddOrUpdate(root,
            _ => new State(level, loudest, 0f, [], long.MinValue),
            (_, old) =>
            {
                bool moved = Math.Abs(old.Level - level) > Tolerance || Math.Abs(old.Loudest - loudest) > Tolerance;
                bool saw = now >= old.ChangedAt; // began reading after the last event, so it read that change
                if (!moved && (!saw || old.Reported == 0f) && !HasExpired(old.Recent, now)) return old;
                var recent = moved
                    ? Add(old.Recent, Math.Max(old.Loudest, old.Reported), now + settleDelay, now)
                    : Add(old.Recent, 0f, long.MinValue, now);
                return new State(level, loudest, saw ? 0f : old.Reported, recent, old.ChangedAt);
            });
        _verifiedAt[root] = now;
    }

    /// <summary>A check of a root whose audible sessions all play at <paramref name="level"/> (null: nothing could be read).</summary>
    public void Verify(uint root, float? level, long now) =>
        Verify(root, level is { } v ? Evaluate([new SessionLevel(v, false)], true) : Evaluate([], false), now);

    /// <summary>
    /// The normalization for the capture thread: 1 / the level, or 1 (no inverse) while the root has no usable common
    /// level or no check within the verify window (a stalled router fails quiet, never loud). Covers the levels still
    /// in flight and those reported but not checked yet, so it is never more than the inverse of any of them.
    /// </summary>
    public float For(uint root, long now)
    {
        if (!_states.TryGetValue(root, out var state) || state.Level < MinLevel ||
            !_verifiedAt.TryGetValue(root, out long verifiedAt) || now - verifiedAt > verifyWindow) return 1f;
        float cap = Math.Max(state.Level, state.Reported);
        foreach (var (level, until) in state.Recent)
            if (now < until) cap = Math.Max(cap, level);
        return 1f / cap;
    }

    public void Remove(uint root)
    {
        _states.TryRemove(root, out _);
        _verifiedAt.TryRemove(root, out _);
    }

    public void KeepOnly(IReadOnlySet<uint> roots)
    {
        foreach (var root in _states.Keys) if (!roots.Contains(root)) Remove(root);
        foreach (var root in _verifiedAt.Keys) if (!roots.Contains(root)) Remove(root);
    }

    public void Clear()
    {
        _states.Clear();
        _verifiedAt.Clear();
    }

    /// <summary>
    /// What a check of one send target means. <paramref name="complete"/>: the sessions are every session the plan
    /// sends through the target, all read without error (otherwise <see cref="SendVolumeStatus.Waiting"/>, nothing
    /// divided out). Muted and 0 % sessions are silent and left out: one becoming audible raises a volume event (and
    /// the next check, ~16 ms later, sees it) before its audio reaches the capture. One audible session below 1 %
    /// gives <see cref="SendVolumeStatus.BelowMinimum"/>, different levels <see cref="SendVolumeStatus.DifferentSessionLevels"/>,
    /// no audible session <see cref="SendVolumeStatus.MutedOrZero"/>.
    /// </summary>
    internal static SendCheck Evaluate(ReadOnlySpan<SessionLevel> sessions, bool complete)
    {
        if (!complete || sessions.IsEmpty) return new(SendVolumeStatus.Waiting, null, 1f);
        float loudest = 0f, common = -1f;
        bool below = false, mixed = false;
        foreach (var session in sessions)
        {
            if (!float.IsFinite(session.Logical)) return new(SendVolumeStatus.Waiting, null, 1f);
            float level = Math.Clamp(session.Logical, 0f, 1f);
            if (session.Muted || level == 0f) continue;
            loudest = Math.Max(loudest, level);
            if (level < MinLevel) below = true;
            else if (common < 0f) common = level;
            else if (Math.Abs(common - level) > Tolerance) mixed = true;
        }
        if (loudest == 0f) return new(SendVolumeStatus.MutedOrZero, null, 0f);
        if (below) return new(SendVolumeStatus.BelowMinimum, null, loudest);
        if (mixed) return new(SendVolumeStatus.DifferentSessionLevels, null, loudest);
        return new(SendVolumeStatus.Available, common, loudest);
    }

    /// <summary>
    /// Refuses (returns 1) a normalization that would lift this packet's peak above 4 (+12 dBFS, louder than an app
    /// plausibly plays before its Windows volume), or one that is not a finite gain of at least 1. It only caps the
    /// absolute level: a packet still at a louder Windows level than the normalization assumes passes as long as it
    /// stays under that cap (audio at 50 % normalized for 20 % comes out 2.5x, +8 dB, too loud). Keeping the
    /// normalization in step with the level is the job of the settle window, the volume events and the checks.
    /// </summary>
    public static float Guard(float normalization, float peakAfterAttenuation) =>
        !float.IsFinite(normalization) || normalization < 1f ||
        peakAfterAttenuation * normalization > 4f ? 1f : normalization;

    private static float? Clean(float? level) =>
        level is { } v && float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : null;

    private static bool HasExpired((float Level, long Until)[] recent, long now)
    {
        foreach (var entry in recent)
            if (entry.Until <= now) return true;
        return false;
    }

    /// <summary>The entries still running at <paramref name="now"/>, plus one (unless already over); merged when there are too many.</summary>
    private static (float Level, long Until)[] Add((float Level, long Until)[] recent, float level, long until, long now)
    {
        bool add = until > now;
        int live = add ? 1 : 0;
        foreach (var entry in recent)
            if (entry.Until > now) live++;
        if (live == 0) return [];
        var result = new (float Level, long Until)[live];
        int i = 0;
        foreach (var entry in recent)
            if (entry.Until > now) result[i++] = entry;
        if (add) result[i] = (level, until);
        if (result.Length <= MaxRecent) return result;
        // A long drag: one entry covering them all (the highest level, until the last of them ends).
        float highest = 0f;
        long last = now;
        foreach (var entry in result)
        {
            highest = Math.Max(highest, entry.Level);
            last = Math.Max(last, entry.Until);
        }
        return [(highest, last)];
    }
}
