using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// The 5.1 sender's encoder without Media Foundation (experimental since 1.4): every call must be one block of 1024
/// six-channel frames, and gives one access unit of <see cref="Unit"/> bytes for that block.
/// </summary>
internal sealed class FakeAacEncoder : IAacEncoder
{
    public int Blocks { get; private set; }

    /// <summary>The largest |sample| seen: 0 for a FIFO nobody wrote to.</summary>
    public int Peak { get; private set; }

    public static byte[] Unit(int block)
    {
        var unit = new byte[48];
        for (int i = 0; i < unit.Length; i++) unit[i] = (byte)(0x21 + block * 7 + i);
        return unit;
    }

    public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != 1024 * 6) throw new ArgumentException($"expected 1024 x 6 samples, got {pcm.Length}", nameof(pcm));
        foreach (short s in pcm) Peak = Math.Max(Peak, Math.Abs((int)s));
        return [Unit(Blocks++)];
    }

    public void Dispose() { }
}
