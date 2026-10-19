namespace HomePodCast.ProtocolHarness;

/// <summary>
/// Bounded decoded PCM for the software receiver checks. The collecting thread owns it until collection stops;
/// appending must not copy the accumulated recording or allocate while the sender is meeting audio deadlines.
/// </summary>
internal sealed class PcmCaptureBuffer
{
    private readonly byte[] _marker;
    private readonly byte[] _buffer;

    public int Count { get; private set; }
    public int MarkerOffset { get; private set; } = -1;
    public long MarkerArrivedAt { get; private set; }
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Count);

    public PcmCaptureBuffer(byte[] marker, int capacity)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Length == 0) throw new ArgumentException("The PCM marker must not be empty", nameof(marker));
        if (capacity < marker.Length) throw new ArgumentOutOfRangeException(nameof(capacity), "The capture must fit its PCM marker");
        _marker = (byte[])marker.Clone();
        _buffer = new byte[capacity];
    }

    public void Append(ReadOnlySpan<byte> data, long arrivedAt)
    {
        // Reject the entire block before changing either the recording or the first marker observation.
        if (data.Length > _buffer.Length - Count) throw new InvalidOperationException("Decoded PCM exceeded the fixed capture capacity");
        if (data.Length == 0) return;
        int oldCount = Count;
        data.CopyTo(_buffer.AsSpan(oldCount));
        Count += data.Length;
        if (MarkerOffset >= 0) return;

        // A marker crossing the read boundary can begin in any of the preceding marker.Length - 1 bytes.
        int searchStart = Math.Max(0, oldCount - _marker.Length + 1);
        int offset = Written[searchStart..].IndexOf(_marker);
        if (offset < 0) return;
        MarkerOffset = searchStart + offset;
        MarkerArrivedAt = arrivedAt;
    }
}
