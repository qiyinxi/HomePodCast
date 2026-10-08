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
