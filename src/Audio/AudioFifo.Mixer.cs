namespace HomePodCast.Audio;

public sealed partial class AudioFifo
{
    // Allocated only by the multi-destination hub. The ordinary endpoint FIFO has no source ledger.
    private Dictionary<uint, float[]>? _mixerContributions;
    private float[]? _mixerScratch;

    internal void WriteMixer(Dictionary<uint, float[]> contributions, int frames)
    {
        if (frames <= 0) return;
        if (frames > _capacity) throw new ArgumentOutOfRangeException(nameof(frames));
        lock (_lock)
        {
            _mixerContributions ??= [];
            _mixerScratch ??= new float[_capacity * 2];
            var mix = _mixerScratch.AsSpan(0, frames * 2);
            mix.Clear();
            foreach (uint id in contributions.Keys)
                if (!_mixerContributions.ContainsKey(id)) _mixerContributions[id] = new float[_capacity * 2];
            int write = (_read + _count) % _capacity;
            foreach (var (id, history) in _mixerContributions)
            {
                contributions.TryGetValue(id, out var samples);
                for (int f = 0; f < frames; f++)
                {
                    int slot = (write + f) % _capacity * 2;
                    float left = samples?[2 * f] ?? 0, right = samples?[2 * f + 1] ?? 0;
                    history[slot] = left; history[slot + 1] = right;
                    mix[2 * f] += left; mix[2 * f + 1] += right;
                }
            }
            for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1, 1);
            Write(mix);
        }
    }

    /// <summary>Remove only this root's unconsumed sound, preserving the other apps and their queued frame positions.</summary>
    internal void RemoveMixerSource(uint id)
    {
        lock (_lock)
        {
            if (_mixerContributions == null || !_mixerContributions.Remove(id)) return;
            for (int f = 0; f < _count; f++)
            {
                int slot = (_read + f) % _capacity * 2;
                float left = 0, right = 0;
                foreach (var history in _mixerContributions.Values)
                { left += history[slot]; right += history[slot + 1]; }
                _buf[slot] = Math.Clamp(left, -1, 1); _buf[slot + 1] = Math.Clamp(right, -1, 1);
            }
        }
    }

    internal int MixerLedgerSources => _mixerContributions?.Count ?? 0;
}
