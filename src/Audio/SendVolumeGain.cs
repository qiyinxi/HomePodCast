using System.Collections.Concurrent;

namespace HomePodCast.Audio;

internal enum SendVolumeStatus { Waiting, Available, MutedOrZero, DifferentSessionLevels, BelowMinimum }

/// <summary>
/// Process loopback is after Windows session volume. Dividing that volume out gives an independent
/// HomePod send only when every contributing session has the same nonzero logical level. Mixed levels,
/// mute/zero, a changing level or a stale check use the ordinary captured copy without amplification.
/// </summary>
internal sealed class SendVolumeGain(long settleDelay, long verifyWindow)
{
    private sealed record State(float Level, long OpenAt, long VerifiedAt);
    private readonly ConcurrentDictionary<uint, State> _states = new();

    public void Changed(uint root, long now) =>
        _states.AddOrUpdate(root, new State(0, now + settleDelay, now),
            (_, old) => old with { OpenAt = Math.Max(old.OpenAt, now + settleDelay), VerifiedAt = -1 });

    public void Verify(uint root, float? logicalLevel, long now)
    {
        float level = logicalLevel is { } v && float.IsFinite(v) && v >= 0.01f && v <= 1f ? v : 0;
        _states.AddOrUpdate(root, new State(level, now + settleDelay, now), (_, old) =>
            new State(level, Math.Abs(old.Level - level) > 0.0001f ? now + settleDelay : old.OpenAt, now));
    }

    /// <summary>The read-only normalizer; never invert tiny, mismatched or unverified levels.</summary>
    public float For(uint root, long now) =>
        _states.TryGetValue(root, out var state) && state.Level >= 0.01f && state.VerifiedAt >= 0 && now >= state.OpenAt &&
        now - state.VerifiedAt <= verifyWindow ? 1f / state.Level : 1f;

    public void Remove(uint root) => _states.TryRemove(root, out _);
    public void KeepOnly(IReadOnlySet<uint> roots)
    {
        foreach (var root in _states.Keys.Where(root => !roots.Contains(root))) Remove(root);
    }
    public void Clear() => _states.Clear();

    public static float? CommonLevel(IEnumerable<float> logicalLevels)
    {
        float? common = null;
        foreach (float level in logicalLevels)
        {
            if (!float.IsFinite(level) || level < 0.01f || level > 1f) return null;
            if (common is { } previous && Math.Abs(previous - level) > 0.0001f) return null;
            common = level;
        }
        return common;
    }

    /// <summary>A stale/louder packet cannot be multiplied beyond a plausible +12 dB app peak.</summary>
    public static float Guard(float normalization, float peakAfterAttenuation) =>
        !float.IsFinite(normalization) || normalization < 1f ||
        peakAfterAttenuation * normalization > 4f ? 1f : normalization;
}
