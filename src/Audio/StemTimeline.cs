namespace HomePodCast.Audio;

/// <summary>One inference writer and one audio reader, indexed by original input frame, never by arrival time.</summary>
internal sealed class StemTimeline(int capacity = 65536)
{
    private readonly float[] _samples = new float[capacity * 2];
    private readonly long[] _tags = Enumerable.Repeat(long.MinValue, capacity).ToArray();

    public void Write(long first, ReadOnlySpan<float> stereo)
    {
        for (int f = 0; f < stereo.Length / 2; f++) WriteFrame(first + f, stereo[f * 2], stereo[f * 2 + 1]);
    }

    public void WriteFrame(long sequence, float left, float right)
    {
        if (sequence < 0) return; // graph's invalid pre-roll
        int index = (int)(sequence % capacity);
        Volatile.Write(ref _tags[index], long.MinValue);
        _samples[index * 2] = left; _samples[index * 2 + 1] = right;
        Volatile.Write(ref _tags[index], sequence);
    }

    public bool Read(long sequence, out float left, out float right)
    {
        left = right = 0;
        if (sequence < 0) return false;
        int index = (int)(sequence % capacity);
        if (Volatile.Read(ref _tags[index]) != sequence) return false;
        left = _samples[index * 2]; right = _samples[index * 2 + 1];
        if (Volatile.Read(ref _tags[index]) == sequence) return true;
        left = right = 0;
        return false;
    }

    /// <summary>Whether frame <paramref name="sequence"/> is there now (the look-ahead of <see cref="StemBus"/>).</summary>
    public bool Has(long sequence) => sequence >= 0 && Volatile.Read(ref _tags[(int)(sequence % capacity)]) == sequence;
}

/// <summary>Mixture-consistent accompaniment; equal stem gains reconstruct the original without inference.</summary>
internal static class StemMix
{
    public static float Sample(float original, float vocal, float vocalGain, float accompanimentGain) =>
        Math.Clamp(vocalGain == accompanimentGain ? original * accompanimentGain
            : original * accompanimentGain + vocal * (vocalGain - accompanimentGain), -1f, 1f);
    public static float Gain(int percent) => Math.Clamp(percent, 0, 100) / 100f;
}

/// <summary>
/// One destination's stem mix (the HomePod, or the local monitor), frame by frame from a pipeline's timelines. The gains
/// slew, so a slider never steps the output (zipper noise), and an envelope fades the output out before model results
/// go missing, looking <see cref="FadeFrames"/> ahead, and back in once they return, so a late block never clicks. While
/// results are missing, a mix that needs them stays silent: it never falls back to the unseparated original (a karaoke
/// mix would get the vocals back). Audio thread only.
/// </summary>
internal sealed class StemBus
{
    /// <summary>Frames for a gain to go from 0 to 100 % (10 ms).</summary>
    public const int GainSlewFrames = 441;

    /// <summary>Frames of the availability fade (5 ms); the bus also looks this far ahead for a missing result.</summary>
    public const int FadeFrames = 220;

    private const float GainStep = 1f / GainSlewFrames;
    private float _vocal = float.NaN, _music;
    private int _fade; // the envelope in frames, 0 … FadeFrames: exactly silent at 0

    public float Envelope => (float)_fade / FadeFrames;

    /// <summary>
    /// Mixes frame <paramref name="at"/> at the slider gains <paramref name="targetVocal"/> / <paramref name="targetMusic"/>.
    /// Returns false when the frame needed a model result that is missing (a missed frame); frames before the first
    /// original one (the start-up wait) are silent and not missed.
    /// </summary>
    public bool Mix(StemTimeline original, StemTimeline vocals, long at, float targetVocal, float targetMusic,
        out float left, out float right)
    {
        left = right = 0;
        bool hasOriginal = original.Read(at, out float originalLeft, out float originalRight);
        bool hasVocal = vocals.Read(at, out float vocalLeft, out float vocalRight);
        bool usable = hasVocal && vocals.Has(at + FadeFrames);
        if (float.IsNaN(_vocal)) { _vocal = targetVocal; _music = targetMusic; }
        bool wanted = targetVocal != targetMusic;
        if (usable || (!wanted && _vocal == _music))
        {
            _vocal = Toward(_vocal, targetVocal, GainStep);
            _music = Toward(_music, targetMusic, GainStep);
        }
        else if (_fade == 0)
        {
            // Results are missing and silent already: take the new gains at once, the fade-in starts from them.
            _vocal = targetVocal;
            _music = targetMusic;
        }
        // else: hold the gains while the envelope fades out; a slew now would need the results that are missing.
        bool needs = _vocal != _music;
        _fade = Math.Clamp(_fade + (hasOriginal && (usable || (!needs && !wanted)) ? 1 : -1), 0, FadeFrames);
        bool missed = hasOriginal && (needs || wanted) && !hasVocal;
        if (!hasOriginal || (needs && !hasVocal)) return !missed;
        float envelope = Envelope;
        left = StemMix.Sample(originalLeft, vocalLeft, _vocal, _music) * envelope;
        right = StemMix.Sample(originalRight, vocalRight, _vocal, _music) * envelope;
        return !missed;
    }

    private static float Toward(float value, float target, float step) =>
        Math.Abs(target - value) <= step ? target : value + (target > value ? step : -step);
}
