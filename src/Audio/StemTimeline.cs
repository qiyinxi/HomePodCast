namespace HomePodCast.Audio;

/// <summary>One inference writer and one audio reader, indexed by original input frame, never by arrival time.</summary>
internal sealed class StemTimeline(int capacity = 65536)
{
    private readonly float[] _samples = new float[capacity * 2];
    private readonly long[] _tags = Enumerable.Repeat(long.MinValue, capacity).ToArray();

    public void Write(long first, ReadOnlySpan<float> stereo)
    {
        for (int f = 0; f < stereo.Length / 2; f++)
        {
            long sequence = first + f;
            if (sequence < 0) continue; // graph's invalid pre-roll
            int index = (int)(sequence % capacity);
            Volatile.Write(ref _tags[index], long.MinValue);
            _samples[index * 2] = stereo[f * 2]; _samples[index * 2 + 1] = stereo[f * 2 + 1];
            Volatile.Write(ref _tags[index], sequence);
        }
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
}

/// <summary>Mixture-consistent accompaniment; equal stem gains reconstruct the original without inference.</summary>
internal static class StemMix
{
    public static float Sample(float original, float vocal, float vocalGain, float accompanimentGain) =>
        Math.Clamp(vocalGain == accompanimentGain ? original * accompanimentGain
            : original * accompanimentGain + vocal * (vocalGain - accompanimentGain), -1f, 1f);
    public static float Gain(int percent) => Math.Clamp(percent, 0, 100) / 100f;
}
