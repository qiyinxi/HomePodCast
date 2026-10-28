namespace HomePodCast.Audio;

/// <summary>
/// In-place processing of interleaved stereo float audio at the stream rate, on a real-time thread (the
/// sender, once per packet of RtpSender.FramesPerPacket frames, or the mic capture thread): must not
/// allocate, lock or block; parameter setters may be called from any thread and take effect smoothly.
/// </summary>
public interface IAudioEffect
{
    void Process(Span<float> interleaved, int frames);
}

/// <summary>
/// Captured samples are made finite and bounded where they enter (the mic, every loopback drain), before any
/// stateful effect sees them. 2026-10-27 review: one NaN or Inf from a driver, an APO or a virtual mic stayed in the
/// state of an EQ, the reverb or the gate for good, and the senders turned everything after it into silence.
/// </summary>
internal static class SampleGuard
{
    /// <summary>+12 dBFS: far above any real signal (a float mix may exceed 1), low enough that no filter overflows.</summary>
    public const float Limit = 4f;

    public static float Clean(float v) => float.IsFinite(v) ? Math.Clamp(v, -Limit, Limit) : 0f;
}

/// <summary>
/// The effects applied to every packet just before it is encoded (the hook in RtpSender.SendLoop).
/// Order matters: tone shaping (an output EQ) belongs in front of dynamics (the night-mode compressor),
/// so the compressor sees the final spectrum and its ceiling still holds — insert an EQ at index 0.
/// Changes swap an immutable array, so the sender thread never sees a half-built list.
/// </summary>
public sealed class EffectChain : IAudioEffect
{
    private readonly object _edit = new();
    private volatile IAudioEffect[] _effects;

    public EffectChain(params IAudioEffect[] effects) => _effects = [.. effects];

    public IReadOnlyList<IAudioEffect> Effects => _effects;

    public void Insert(int index, IAudioEffect effect)
    {
        lock (_edit)
        {
            var list = _effects.ToList();
            list.Insert(Math.Clamp(index, 0, list.Count), effect);
            _effects = [.. list];
        }
    }

    public void Add(IAudioEffect effect) => Insert(int.MaxValue, effect);

    public void Remove(IAudioEffect effect)
    {
        lock (_edit) _effects = _effects.Where(e => !ReferenceEquals(e, effect)).ToArray();
    }

    public void Process(Span<float> interleaved, int frames)
    {
        var effects = _effects;
        for (int i = 0; i < effects.Length; i++) effects[i].Process(interleaved, frames);
    }
}
