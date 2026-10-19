using System.Collections.Concurrent;
using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>One complete encoded media packet and its decoded 44.1 kHz stereo duration.</summary>
internal readonly record struct DlnaEncodedPacket(byte[] Data, int PcmFrames);

/// <summary>Owned entirely by one encoder thread, including construction and disposal.</summary>
internal interface IDlnaAudioEncoder : IDisposable
{
    int FramesPerInput { get; }
    IReadOnlyList<DlnaEncodedPacket> Encode(ReadOnlySpan<short> stereo);
    IReadOnlyList<DlnaEncodedPacket> Drain();
}

internal static class DlnaAudioEncoders
{
    public static IDlnaAudioEncoder Create(DlnaAudioFormat format)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DLNA encoding requires Windows Media Foundation or an injected encoder.");
        return new DlnaMediaFoundationEncoder(format);
    }
}

/// <summary>Bounded bridge between asynchronous HTTP and a thread-affine synchronous codec.</summary>
internal sealed class DlnaEncoderWorker : IDisposable
{
    private sealed record Request(short[]? Pcm, TaskCompletionSource<IReadOnlyList<DlnaEncodedPacket>> Completion);
    private readonly BlockingCollection<Request> _requests = new(1);
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public DlnaEncoderWorker(Func<IDlnaAudioEncoder> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var thread = new Thread(() => Run(factory)) { IsBackground = true, Name = "DLNA encoder" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public Task<int> FramesPerInput => _ready.Task;
    public Task Finished => _finished.Task;

    public Task<IReadOnlyList<DlnaEncodedPacket>> EncodeAsync(ReadOnlySpan<short> stereo, CancellationToken ct)
        => Submit(stereo.ToArray(), ct);
    public Task<IReadOnlyList<DlnaEncodedPacket>> DrainAsync(CancellationToken ct) => Submit(null, ct);

    private async Task<IReadOnlyList<DlnaEncodedPacket>> Submit(short[]? pcm, CancellationToken ct)
    {
        await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<IReadOnlyList<DlnaEncodedPacket>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only one request may wait behind the codec. HTTP cancellation never waits for a blocked codec.
        if (!_requests.TryAdd(new(pcm, completion))) throw new InvalidOperationException("The DLNA encoder already has a pending request.");
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void Run(Func<IDlnaAudioEncoder> factory)
    {
        Exception? failure = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var encoder = factory();
            if (encoder.FramesPerInput is <= 0 or > 8192) throw new InvalidDataException("Invalid encoder input block size.");
            _ready.TrySetResult(encoder.FramesPerInput);
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    request.Completion.TrySetException(new ObjectDisposedException(nameof(DlnaEncoderWorker)));
                    continue;
                }
                try
                {
                    var packets = request.Pcm == null ? encoder.Drain() : encoder.Encode(request.Pcm);
                    if (packets.Count > 64) throw new InvalidDataException("Encoder output exceeded its bounded packet limit.");
                    foreach (var packet in packets)
                        if (packet.Data == null || packet.Data.Length > 1024 * 1024 || packet.PcmFrames < 0
                            || packet.Data.Length != 0 && packet.PcmFrames == 0)
                            throw new InvalidDataException("Invalid encoded media packet.");
                    request.Completion.TrySetResult(packets);
                }
                catch (Exception e) { request.Completion.TrySetException(e); throw; }
            }
        }
        catch (Exception e) { failure = e; _ready.TrySetException(e); }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);
            _requests.CompleteAdding();
            while (_requests.TryTake(out var pending)) pending.Completion.TrySetException(failure ?? new ObjectDisposedException(nameof(DlnaEncoderWorker)));
            _finished.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _requests.CompleteAdding();
    }
}

/// <summary>Retains every source sample across the 352-frame capture and codec block boundaries.</summary>
internal sealed class DlnaPcmReblocker
{
    private readonly short[] _pending;
    private int _count;
    public DlnaPcmReblocker(int framesPerInput)
    {
        if (framesPerInput is <= 0 or > 8192) throw new ArgumentOutOfRangeException(nameof(framesPerInput));
        _pending = new short[framesPerInput * 2];
    }
    public int PendingFrames => _count / 2;
    public IReadOnlyList<short[]> Append(ReadOnlySpan<short> stereo)
    {
        if (stereo.Length % 2 != 0) throw new ArgumentException("Stereo frames must be complete.", nameof(stereo));
        var blocks = new List<short[]>();
        while (!stereo.IsEmpty)
        {
            int take = Math.Min(stereo.Length, _pending.Length - _count);
            stereo[..take].CopyTo(_pending.AsSpan(_count));
            _count += take;
            stereo = stereo[take..];
            if (_count == _pending.Length) { blocks.Add((short[])_pending.Clone()); _count = 0; }
        }
        return blocks;
    }
}

internal static class DlnaCodecFraming
{
    public static byte[] Adts(ReadOnlySpan<byte> aac)
    {
        int size = aac.Length + 7;
        if (aac.IsEmpty || size > 8191) throw new ArgumentOutOfRangeException(nameof(aac));
        return [0xff, 0xf1, 0x50, (byte)(0x80 | (size >> 11)), (byte)(size >> 3), (byte)((size << 5) | 0x1f), 0xfc, .. aac];
    }

    /// <summary>Counts complete MPEG-1 Layer III frames; padding changes byte length, never duration.</summary>
    public static int Mp3Frames(ReadOnlySpan<byte> bytes)
    {
        int frames = 0;
        while (!bytes.IsEmpty)
        {
            int length = Mp3FrameLength(bytes);
            frames = checked(frames + 1152);
            bytes = bytes[length..];
        }
        return frames;
    }

    public static IReadOnlyList<DlnaEncodedPacket> Mp3Packets(ReadOnlySpan<byte> bytes)
    {
        var packets = new List<DlnaEncodedPacket>();
        while (!bytes.IsEmpty)
        {
            int length = Mp3FrameLength(bytes);
            packets.Add(new(bytes[..length].ToArray(), 1152));
            bytes = bytes[length..];
        }
        return packets;
    }

    private static int Mp3FrameLength(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<int> bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        if (bytes.Length < 4 || bytes[0] != 0xff || (bytes[1] & 0xfe) != 0xfa
            || (bytes[2] & 0x0c) != 0 || (bytes[3] >> 6) == 3)
            throw new InvalidDataException("Expected 44.1 kHz stereo MPEG-1 Layer III frames.");
        int index = bytes[2] >> 4;
        if (index is 0 or 15) throw new InvalidDataException("Invalid MP3 bitrate.");
        int length = 144_000 * bitrates[index] / 44_100 + ((bytes[2] >> 1) & 1);
        if (bytes.Length < length) throw new InvalidDataException("Incomplete MP3 frame.");
        return length;
    }
}
