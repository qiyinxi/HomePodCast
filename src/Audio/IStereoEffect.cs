namespace HomePodCast.Audio;

/// <summary>
/// An in-place effect on interleaved stereo float audio. Process is called from a real-time audio thread:
/// implementations must not allocate, lock for long or throw; parameter setters may be called from any
/// thread and take effect smoothly.
/// </summary>
public interface IStereoEffect
{
    void Process(Span<float> interleaved, int frames);
}
