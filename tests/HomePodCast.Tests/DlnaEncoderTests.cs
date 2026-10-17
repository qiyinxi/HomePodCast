using System.Collections.Concurrent;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public sealed class DlnaEncoderTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(1152)]
    public void ReblockingRetainsEveryStereoSampleAcross352FrameCallbacks(int codecFrames)
    {
        var reblocker = new DlnaPcmReblocker(codecFrames);
        var output = new List<short>();
        const int callbacks = 37;
        short[] input = Enumerable.Range(0, callbacks * 352 * 2).Select(i => (short)(i % 30000 - 15000)).ToArray();
        for (int i = 0; i < callbacks; i++)
            foreach (short[] block in reblocker.Append(input.AsSpan(i * 704, 704)))
            {
                Assert.Equal(codecFrames * 2, block.Length);
                output.AddRange(block);
            }
        int complete = input.Length / (codecFrames * 2) * codecFrames * 2;
        Assert.Equal(input[..complete], output);
        Assert.Equal((input.Length - complete) / 2, reblocker.PendingFrames);
    }

    [Fact]
    public async Task WorkerConstructsEncodesDrainsAndDisposesOnOneOwningThread()
    {
        var calls = new ConcurrentQueue<(string Name, int Thread)>();
        int caller = Environment.CurrentManagedThreadId;
        var worker = new DlnaEncoderWorker(() =>
        {
            if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            return new FakeEncoder(calls);
        });
        Assert.Equal(1024, await worker.FramesPerInput.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(await worker.EncodeAsync(new short[2048], CancellationToken.None));
        var packets = await worker.EncodeAsync(new short[2048], CancellationToken.None);
        Assert.Equal(new[] { 1024, 2048 }, packets.Select(p => p.PcmFrames));
        Assert.Single(await worker.DrainAsync(CancellationToken.None));
        worker.Dispose();
        await worker.Finished.WaitAsync(TimeSpan.FromSeconds(3));
        var actual = calls.ToArray();
        Assert.Equal(new[] { "create", "encode", "encode", "drain", "dispose" }, actual.Select(c => c.Name));
        Assert.Single(actual.Select(c => c.Thread).Distinct());
        Assert.NotEqual(caller, actual[0].Thread);
    }

    [Fact]
    public async Task CancellationNeverWaitsForBlockedCodecAndDisposalStaysOnWorker()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new DlnaEncoderWorker(() => new BlockingEncoder(entered, release));
        await worker.FramesPerInput.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource();
        Task pending = worker.EncodeAsync(new short[2048], cancel.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
            worker.Dispose();
            Assert.False(worker.Finished.IsCompleted);
        }
        finally { release.Set(); await worker.Finished.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task FactoryAndCodecFailuresPropagateWithoutLeakingWorker()
    {
        using var failed = new DlnaEncoderWorker(() => throw new InvalidOperationException("factory"));
        Assert.Equal("factory", (await Assert.ThrowsAsync<InvalidOperationException>(() => failed.FramesPerInput)).Message);
        await failed.Finished.WaitAsync(TimeSpan.FromSeconds(3));
        var calls = new ConcurrentQueue<(string, int)>();
        using var worker = new DlnaEncoderWorker(() => new FakeEncoder(calls, true));
        Assert.Equal("codec", (await Assert.ThrowsAsync<InvalidDataException>(() => worker.EncodeAsync(new short[2048], CancellationToken.None))).Message);
        await worker.Finished.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(calls, c => c.Item1 == "dispose");
    }

    [Fact]
    public async Task WorkerAllowsOnlyOneQueuedBlockBehindBlockedCodec()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new DlnaEncoderWorker(() => new BlockingEncoder(entered, release));
        await worker.FramesPerInput.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource();
        Task first = worker.EncodeAsync(new short[2048], cancel.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        Task second = worker.EncodeAsync(new short[2048], cancel.Token);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.EncodeAsync(new short[2048], cancel.Token));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            worker.Dispose();
        }
        finally { release.Set(); worker.Dispose(); await worker.Finished.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public void AdtsHeaderBoundsAndMp3DurationFollowFrameStructure()
    {
        byte[] aac = DlnaCodecFraming.Adts([1, 2, 3]);
        Assert.Equal(new byte[] { 0xff, 0xf1, 0x50, 0x80, 1, 0x5f, 0xfc, 1, 2, 3 }, aac);
        Assert.Equal(8191, DlnaCodecFraming.Adts(new byte[8184]).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => DlnaCodecFraming.Adts(new byte[8185]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DlnaCodecFraming.Adts([]));
        byte[] first = Mp3Frame(false), padded = Mp3Frame(true);
        Assert.Equal(first.Length + 1, padded.Length);
        Assert.Equal(2304, DlnaCodecFraming.Mp3Frames([.. first, .. padded]));
        var split = DlnaCodecFraming.Mp3Packets([.. first, .. padded]);
        Assert.Equal(new[] { first.Length, padded.Length }, split.Select(p => p.Data.Length));
        Assert.All(split, packet => Assert.Equal(1152, packet.PcmFrames));
        Assert.Throws<InvalidDataException>(() => DlnaCodecFraming.Mp3Frames(first.AsSpan(0, first.Length - 1)));
        first[2] |= 4; // 48 kHz cannot be mistaken for the negotiated 44.1 kHz duration.
        Assert.Throws<InvalidDataException>(() => DlnaCodecFraming.Mp3Frames(first));
    }

    private static byte[] Mp3Frame(bool padded)
    {
        var bytes = new byte[144_000 * 192 / 44100 + (padded ? 1 : 0)];
        bytes[0] = 0xff; bytes[1] = 0xfb; bytes[2] = (byte)(0xb0 | (padded ? 2 : 0));
        return bytes;
    }

    private sealed class FakeEncoder(ConcurrentQueue<(string Name, int Thread)> calls, bool fail = false) : IDlnaAudioEncoder
    {
        private int _count;
        private readonly bool _created = Record(calls, "create");
        public int FramesPerInput => 1024;
        public IReadOnlyList<DlnaEncodedPacket> Encode(ReadOnlySpan<short> stereo)
        {
            _ = _created;
            Record(calls, "encode");
            if (fail) throw new InvalidDataException("codec");
            return ++_count == 1 ? [] : [new([1], 1024), new([2, 3], 2048)];
        }
        public IReadOnlyList<DlnaEncodedPacket> Drain() { Record(calls, "drain"); return [new([4], 1024)]; }
        public void Dispose() => Record(calls, "dispose");
    }
    private static bool Record(ConcurrentQueue<(string Name, int Thread)> calls, string name)
    { calls.Enqueue((name, Environment.CurrentManagedThreadId)); return true; }

    private sealed class BlockingEncoder(ManualResetEventSlim entered, ManualResetEventSlim release) : IDlnaAudioEncoder
    {
        public int FramesPerInput => 1024;
        public IReadOnlyList<DlnaEncodedPacket> Encode(ReadOnlySpan<short> stereo) { entered.Set(); release.Wait(); return []; }
        public IReadOnlyList<DlnaEncodedPacket> Drain() => [];
        public void Dispose() { }
    }
}
