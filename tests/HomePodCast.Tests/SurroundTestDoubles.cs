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

    /// <summary>The samples of the first block, as the sender read them from the FIFO.</summary>
    public short[]? FirstBlock { get; private set; }

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
        if (Blocks == 0) FirstBlock = pcm.ToArray();
        return [Unit(Blocks++)];
    }

    public void Dispose() { }
}

/// <summary>
/// The controller's 5.1 capture without a 5.1 endpoint (experimental since 1.4): <see cref="WaitReadyAsync"/> answers
/// <see cref="Problem"/>, else <see cref="Ready"/> (null: capturing 5.1). Writes nothing, so the FIFO gives silence.
/// </summary>
internal sealed class FakeSurroundCapture : ISurroundCapture
{
    private volatile string? _problem, _ready;
    private int _started, _disposed;

    /// <summary>What WaitReadyAsync answers while there is no <see cref="Problem"/>: null, or the refusal (R4, R5).</summary>
    public string? Ready { get => _ready; set => _ready = value; }

    public string? Problem { get => _problem; set => _problem = value; }

    public bool Started => Volatile.Read(ref _started) != 0;
    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public string? DeviceName => "fake 5.1 output";
    public string? EndpointId { get; init; }
    public double DriftPpm => 0;
    public float Peak => 0;
    public int ExtraLatencyMs => 0;
    public event Action<string>? DeviceChanged { add { } remove { } }

    public void Start() => Interlocked.Exchange(ref _started, 1);

    public Task<string?> WaitReadyAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(Problem ?? Ready);

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
