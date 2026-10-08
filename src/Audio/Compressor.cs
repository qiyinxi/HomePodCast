namespace HomePodCast.Audio;

/// <summary>
/// Night mode: a stereo-linked, feed-forward compressor with make-up gain and a peak ceiling. Loud
/// passages (explosions, gunfire) come down, quiet ones (dialogue, footsteps) come up, so the speaker can
/// play softly without either waking the house or losing detail.
/// <para>
/// Built to be cheap on the real-time sender thread: the detector is the peak of a 16-frame block
/// (0.36 ms), the gain curve (one log, one exp) runs once per block, and the gain is ramped linearly
/// across the block. Attack is immediate (one block), release is smooth. There is no look-ahead, so the
/// first block of a sudden transient can overshoot the ramp; a per-sample clamp at the ceiling catches
/// it. Allocation-free. Turning it off fades the gain back to unity before bypassing, so no step.
/// </para>
/// </summary>
public sealed class Compressor : IAudioEffect
{
    public const int BlockFrames = 16;
    public const float ThresholdDb = -30f;
    public const float Ratio = 4f;
    public const float KneeDb = 10f;
    public const float MakeupDb = 8f;
    public const float CeilingDb = -1f;
    public const float ReleaseMs = 250f;

    /// <summary>Below this the input counts as silence: no make-up gain, so hiss is not pumped up.</summary>
    public const float FloorDb = -70f;

    private readonly float _release;   // one-pole coefficient per full block
    private readonly float _ceiling = FromDb(CeilingDb);
    private volatile bool _enabled;
    private bool _active;               // processing: enabled, or still fading back to unity
    private float _gainDb;              // smoothed gain at the end of the last block
    private float _gain = 1f;           // the same, linear

    public Compressor(int sampleRate = 44100)
    {
        _release = 1f - MathF.Exp(-BlockFrames / (ReleaseMs / 1000f * sampleRate));
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Gain currently applied (dB); 0 when bypassed. Approximate if read from another thread.</summary>
    public float CurrentGainDb => _gainDb;

    /// <summary>
    /// Static curve: gain (dB) for a block whose peak is <paramref name="inputDb"/> dBFS. Soft knee around the
    /// threshold, then 1/Ratio slope; plus make-up gain; never lifts a peak above the ceiling.
    /// </summary>
    public static float GainDb(float inputDb)
    {
        if (!(inputDb >= FloorDb)) return 0f; // also catches NaN / -∞
        float over = inputDb - ThresholdDb;
        float outDb;
        if (2 * over <= -KneeDb) outDb = inputDb;
        else if (2 * over >= KneeDb) outDb = ThresholdDb + over / Ratio;
        else
        {
            float k = over + KneeDb / 2;
            outDb = inputDb + (1 / Ratio - 1) * k * k / (2 * KneeDb);
        }
        return MathF.Min(outDb - inputDb + MakeupDb, CeilingDb - inputDb);
    }

    public void Process(Span<float> interleaved, int frames)
    {
        bool enabled = _enabled;
        if (!enabled && !_active) return;
        _active = true;

        var x = interleaved[..(frames * 2)];
        float ceiling = enabled ? _ceiling : float.MaxValue;
        for (int start = 0; start < frames; start += BlockFrames)
        {
            int n = Math.Min(BlockFrames, frames - start);
            var block = x.Slice(start * 2, n * 2);

            float peak = 0f;
            for (int i = 0; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));

            float target = enabled ? GainDb(ToDb(peak)) : 0f;
            _gainDb = target < _gainDb
                ? target                                                        // attack: at once
                : _gainDb + (target - _gainDb) * (_release * n / BlockFrames);   // release: smooth

            float g0 = _gain, g1 = FromDb(_gainDb), dg = (g1 - g0) / n;
            for (int i = 0; i < n; i++)
            {
                float g = g0 + dg * (i + 1);
                block[i * 2] = Math.Clamp(block[i * 2] * g, -ceiling, ceiling);
                block[i * 2 + 1] = Math.Clamp(block[i * 2 + 1] * g, -ceiling, ceiling);
            }
            _gain = g1;
        }

        if (!enabled && MathF.Abs(_gainDb) < 0.01f)
        {
            _gainDb = 0f;
            _gain = 1f;
            _active = false;
        }
    }

    private static float ToDb(float amplitude) => 20f * MathF.Log10(MathF.Max(amplitude, 1e-7f));

    private static float FromDb(float db) => MathF.Exp(db * (MathF.Log(10f) / 20f));
}
