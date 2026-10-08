namespace HomePodCast.Audio;

/// <summary>
/// Adds another 44.1 kHz stereo source (the processed microphone, MicEffects.HomePodSource) to every packet
/// in the send path. Sits at the front of the effects chain, so the output EQ and the night-mode compressor
/// see the mix. The tap zero-fills whatever it doesn't have, so a closed or unrouted mic adds silence.
/// </summary>
public sealed class MixIn(AudioTap source) : IAudioEffect
{
    private readonly float[] _buffer = new float[2 * 2048];

    public void Process(Span<float> interleaved, int frames)
    {
        int n = frames * 2;
        if (n > _buffer.Length) return; // never on the sender path (352 frames per packet)
        var mic = _buffer.AsSpan(0, n);
        source.Read(mic);
        for (int i = 0; i < n; i++) interleaved[i] += mic[i];
    }
}
