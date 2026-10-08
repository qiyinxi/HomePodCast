namespace HomePodCast.Audio;

/// <summary>
/// A non-loopback input mixed into the HomePod stream (e.g. a microphone with effects): pulled as
/// interleaved float stereo at the stream rate (44.1 kHz). <see cref="Read"/> must fill the whole span,
/// padding with silence when it has nothing buffered; it handles its own clock drift.
/// </summary>
public interface IMixSource
{
    void Read(Span<float> dest);
}

/// <summary>
/// Sums capture streams that run off the same audio-engine clock (several process-loopback clients,
/// or just the endpoint loopback) at a common rate. Inputs are drained once per capture cycle, then
/// <see cref="Mix"/> emits what every live input has delivered, so packets stay aligned even when one
/// client's packet lands a moment after the others'. An input that stops delivering is dropped from the
/// alignment after <see cref="StallCycles"/> cycles; backlog beyond <see cref="MaxLagFrames"/> is trimmed.
/// </summary>
public sealed class StreamMixer(int maxLagFrames)
{
    private sealed class Input
    {
        public float[] Buffer = new float[4096];
        public int Frames;
        public int IdleCycles;
        public bool Pushed;
    }

    private readonly Dictionary<int, Input> _inputs = [];

    public int MaxLagFrames { get; } = maxLagFrames;
    public int StallCycles { get; init; } = 3;
    public int Count => _inputs.Count;
    public IEnumerable<int> Ids => _inputs.Keys;

    public void Add(int id) => _inputs.TryAdd(id, new Input());

    public void Remove(int id) => _inputs.Remove(id);

    public bool Contains(int id) => _inputs.ContainsKey(id);

    public void Clear() => _inputs.Clear();

    /// <summary>Frames waiting in one input (for tests and diagnostics).</summary>
    public int Pending(int id) => _inputs.TryGetValue(id, out var i) ? i.Frames : 0;

    /// <summary>Queue interleaved stereo frames for an input, scaled by <paramref name="gain"/>.</summary>
    public void Push(int id, ReadOnlySpan<float> stereo, float gain = 1f)
    {
        var input = _inputs[id];
        input.Pushed = true;
        int frames = stereo.Length / 2;
        if (frames == 0) return;
        int need = (input.Frames + frames) * 2;
        if (input.Buffer.Length < need) Array.Resize(ref input.Buffer, Math.Max(need, input.Buffer.Length * 2));
        var dst = input.Buffer.AsSpan(input.Frames * 2, frames * 2);
        if (gain == 1f) stereo[..(frames * 2)].CopyTo(dst);
        else for (int i = 0; i < dst.Length; i++) dst[i] = stereo[i] * gain;
        input.Frames += frames;
    }

    /// <summary>
    /// Call once per capture cycle, after every input was drained: appends the mixed frames to
    /// <paramref name="output"/> (clamped to ±1) and returns how many frames were mixed.
    /// </summary>
    public int Mix(List<float> output)
    {
        if (_inputs.Count == 0) return 0;
        int min = int.MaxValue, max = 0;
        bool anyLive = false;
        foreach (var input in _inputs.Values)
        {
            input.IdleCycles = input.Pushed ? 0 : input.IdleCycles + 1;
            input.Pushed = false;
            max = Math.Max(max, input.Frames);
            if (input.IdleCycles < StallCycles)
            {
                anyLive = true;
                min = Math.Min(min, input.Frames);
            }
        }
        int n = anyLive ? min : max;
        if (n > 0)
        {
            int start = output.Count;
            for (int i = 0; i < n * 2; i++) output.Add(0f);
            var mix = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output)[start..];
            foreach (var input in _inputs.Values)
            {
                int take = Math.Min(n, input.Frames);
                var src = input.Buffer.AsSpan(0, take * 2);
                for (int i = 0; i < src.Length; i++) mix[i] += src[i];
                Consume(input, take);
            }
            for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        }
        foreach (var input in _inputs.Values)
            if (input.Frames > MaxLagFrames) Consume(input, input.Frames - MaxLagFrames);
        return n;
    }

    private static void Consume(Input input, int frames)
    {
        if (frames <= 0) return;
        int remain = input.Frames - frames;
        if (remain > 0) Array.Copy(input.Buffer, frames * 2, input.Buffer, 0, remain * 2);
        input.Frames = Math.Max(0, remain);
    }

    /// <summary>
    /// Add every extra source into <paramref name="mix"/> (interleaved stereo at the stream rate), pulling
    /// exactly as many frames as were captured, and clamp to ±1.
    /// </summary>
    public static void AddSources(Span<float> mix, IReadOnlyList<IMixSource> sources, ref float[] scratch)
    {
        if (sources.Count == 0) return;
        if (scratch.Length < mix.Length) scratch = new float[mix.Length];
        var tmp = scratch.AsSpan(0, mix.Length);
        foreach (var source in sources)
        {
            tmp.Clear();
            try
            {
                source.Read(tmp);
            }
            catch (Exception ex)
            {
                Log.Warn($"mix source {source.GetType().Name}: {ex.Message}");
                continue;
            }
            for (int i = 0; i < mix.Length; i++) mix[i] += tmp[i];
        }
        for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
    }
}
