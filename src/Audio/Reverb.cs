namespace HomePodCast.Audio;

/// <summary>
/// Stereo Freeverb (Jezar's public-domain design): 8 parallel low-passed feedback combs and 4 series
/// allpasses per channel, the right bank detuned by 23 samples, plus a pre-delay line in front.
/// Parameters may be set from any thread; they glide on the audio thread, the pre-delay crossfades
/// between the old and new tap, and switching off fades the wet signal out before bypassing.
/// Process is allocation-free; tiny values are flushed so a decaying tail never goes subnormal.
/// </summary>
public sealed class Reverb : IStereoEffect
{
    public const float MaxPreDelayMs = 250;

    private const int Spread = 23;
    private const int Block = 32;
    private const float InputGain = 0.015f;
    private const float AllpassFeedback = 0.5f;
    private const float RoomOffset = 0.70f, RoomScale = 0.25f; // feedback 0.70 … 0.95
    private const float DampScale = 0.4f;
    private static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTuning = [556, 441, 341, 225];

    private readonly int _rate;
    private readonly Comb[] _combL, _combR;
    private readonly Allpass[] _apL, _apR;
    private readonly float[] _pre;              // mono pre-delay line
    private readonly int _fadeLength;           // pre-delay tap crossfade (frames)
    private readonly float _paramCoef, _gainCoef, _activeStep;
    private int _preWrite, _preDelay, _preOld, _fade;

    // Settings (any thread).
    private volatile float _roomSize = 0.5f, _damping = 0.5f, _mix = 0.25f, _preDelayMs = 20;
    private volatile bool _enabled = true, _resetRequested;

    // Smoothed values (audio thread).
    private float _feedback, _damp, _wet, _dry = 1, _active = 1;
    private bool _idle, _started;

    public Reverb(int sampleRate = 44100)
    {
        _rate = sampleRate;
        double scale = sampleRate / 44100.0;
        _combL = CombTuning.Select(t => new Comb((int)(t * scale))).ToArray();
        _combR = CombTuning.Select(t => new Comb((int)((t + Spread) * scale))).ToArray();
        _apL = AllpassTuning.Select(t => new Allpass((int)(t * scale))).ToArray();
        _apR = AllpassTuning.Select(t => new Allpass((int)((t + Spread) * scale))).ToArray();
        _pre = new float[(int)(MaxPreDelayMs * sampleRate / 1000) + 1];
        _fadeLength = sampleRate / 50; // 20 ms
        _fade = _fadeLength;
        _paramCoef = (float)(1 - Math.Exp(-Block / (0.05 * sampleRate)));
        _gainCoef = (float)(1 - Math.Exp(-Block / (0.02 * sampleRate)));
        _activeStep = Block / (0.05f * sampleRate); // on/off fade: 50 ms
    }

    public int SampleRate => _rate;

    /// <summary>0 = small room, 1 = large hall (longer tail).</summary>
    public float RoomSize { get => _roomSize; set => _roomSize = Math.Clamp(value, 0f, 1f); }

    /// <summary>0 = bright, 1 = dark (high frequencies die away faster).</summary>
    public float Damping { get => _damping; set => _damping = Math.Clamp(value, 0f, 1f); }

    /// <summary>Wet/dry balance (equal power): 0 = dry only, 1 = reverb only.</summary>
    public float Mix { get => _mix; set => _mix = Math.Clamp(value, 0f, 1f); }

    public float PreDelayMs { get => _preDelayMs; set => _preDelayMs = Math.Clamp(value, 0f, MaxPreDelayMs); }

    /// <summary>Off fades the reverb out (50 ms) and then passes audio through untouched.</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>Clears the tail (on the audio thread, at the next Process).</summary>
    public void Reset() => _resetRequested = true;

    private int PreDelayFrames(float ms) => Math.Clamp((int)Math.Round(ms * _rate / 1000), 0, _pre.Length - 1);

    private static (float Wet, float Dry) MixGains(float mix, float active)
    {
        float wet = MathF.Sin(mix * MathF.PI / 2), dry = MathF.Cos(mix * MathF.PI / 2);
        return (wet * active, 1 - active * (1 - dry));
    }

    public void Process(Span<float> interleaved, int frames)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            Clear();
        }
        if (!_started)
        {
            // The first block starts at the configured values instead of gliding from the defaults.
            _started = true;
            _active = _enabled ? 1 : 0;
            _feedback = RoomOffset + RoomScale * _roomSize;
            _damp = DampScale * _damping;
            _preDelay = _preOld = PreDelayFrames(_preDelayMs);
            (_wet, _dry) = MixGains(_mix, _active);
        }
        for (int pos = 0; pos < frames; pos += Block)
        {
            int n = Math.Min(Block, frames - pos);

            // Per-block parameter glide.
            float activeTarget = _enabled ? 1 : 0;
            if (_active != activeTarget)
                _active = activeTarget > _active ? Math.Min(activeTarget, _active + _activeStep) : Math.Max(activeTarget, _active - _activeStep);
            if (_active == 0 && _wet < 1e-3f && _dry > 1 - 1e-3f) // what is left of the tail is below -60 dB
            {
                if (!_idle) // faded out: drop the tail, pass through untouched
                {
                    Clear();
                    (_wet, _dry, _idle) = (0, 1, true);
                }
                continue;
            }
            _idle = false;
            _feedback += (RoomOffset + RoomScale * _roomSize - _feedback) * _paramCoef;
            _damp += (DampScale * _damping - _damp) * _paramCoef;
            var (wetTarget, dryTarget) = MixGains(_mix, _active);
            float wet0 = _wet, dry0 = _dry;
            _wet += (wetTarget - _wet) * _gainCoef;
            _dry += (dryTarget - _dry) * _gainCoef;
            if (Math.Abs(_wet - wetTarget) < 1e-5f) _wet = wetTarget;
            if (Math.Abs(_dry - dryTarget) < 1e-5f) _dry = dryTarget;
            float wetStep = (_wet - wet0) / n, dryStep = (_dry - dry0) / n;

            int wantDelay = PreDelayFrames(_preDelayMs);
            if (_fade >= _fadeLength && wantDelay != _preDelay)
            {
                _preOld = _preDelay;
                _preDelay = wantDelay;
                _fade = 0;
            }

            float fb = _feedback, damp = _damp;
            var x = interleaved.Slice(pos * 2, n * 2);
            for (int i = 0; i < n; i++)
            {
                float inL = x[i * 2], inR = x[i * 2 + 1];

                _pre[_preWrite] = (inL + inR) * InputGain;
                float input = _pre[Tap(_preDelay)];
                if (_fade < _fadeLength)
                {
                    float t = (float)_fade / _fadeLength;
                    input = _pre[Tap(_preOld)] * (1 - t) + input * t;
                    _fade++;
                }
                if (++_preWrite == _pre.Length) _preWrite = 0;

                float outL = 0, outR = 0;
                for (int c = 0; c < _combL.Length; c++)
                {
                    outL += _combL[c].Process(input, fb, damp);
                    outR += _combR[c].Process(input, fb, damp);
                }
                for (int a = 0; a < _apL.Length; a++)
                {
                    outL = _apL[a].Process(outL);
                    outR = _apR[a].Process(outR);
                }

                float w = wet0 + wetStep * (i + 1), d = dry0 + dryStep * (i + 1);
                x[i * 2] = outL * w + inL * d;
                x[i * 2 + 1] = outR * w + inR * d;
            }
        }
    }

    private int Tap(int delay)
    {
        int i = _preWrite - delay;
        return i < 0 ? i + _pre.Length : i;
    }

    private void Clear()
    {
        Array.Clear(_pre);
        foreach (var c in _combL) c.Clear();
        foreach (var c in _combR) c.Clear();
        foreach (var a in _apL) a.Clear();
        foreach (var a in _apR) a.Clear();
    }

    /// <summary>Test hook: true if any internal state holds a subnormal float.</summary>
    internal bool HasSubnormals()
    {
        static bool Any(float[] b) => b.Any(v => v != 0 && float.IsSubnormal(v));
        return Any(_pre) || _combL.Concat(_combR).Any(c => Any(c.Buffer) || float.IsSubnormal(c.Store)) ||
               _apL.Concat(_apR).Any(a => Any(a.Buffer));
    }

    private static float Flush(float v) => v is > -1e-15f and < 1e-15f ? 0f : v;

    private sealed class Comb(int length)
    {
        public readonly float[] Buffer = new float[Math.Max(1, length)];
        public float Store;
        private int _index;

        public float Process(float input, float feedback, float damp)
        {
            float output = Buffer[_index];
            Store = Flush(output * (1 - damp) + Store * damp);
            Buffer[_index] = Flush(input + Store * feedback);
            if (++_index == Buffer.Length) _index = 0;
            return output;
        }

        public void Clear()
        {
            Array.Clear(Buffer);
            Store = 0;
        }
    }

    private sealed class Allpass(int length)
    {
        public readonly float[] Buffer = new float[Math.Max(1, length)];
        private int _index;

        public float Process(float input)
        {
            float buffered = Buffer[_index];
            Buffer[_index] = Flush(input + buffered * AllpassFeedback);
            if (++_index == Buffer.Length) _index = 0;
            return buffered - input;
        }

        public void Clear() => Array.Clear(Buffer);
    }
}
