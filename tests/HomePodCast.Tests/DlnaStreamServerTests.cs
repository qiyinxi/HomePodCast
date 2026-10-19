using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HomePodCast.Net;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public sealed class DlnaStreamServerTests
{
    private const int ChunkBytes = DlnaAudioFormats.FramesPerChunk * 4;
    private static DlnaStreamServer Create(DlnaAudioFormat format = DlnaAudioFormat.L16,
        DlnaPcmReader? reader = null, Func<bool>? policy = null,
        DlnaHttpFraming framing = DlnaHttpFraming.Chunked, long length = ChunkBytes * 2)
        => new(new(IPAddress.Loopback, IPAddress.Loopback, format, reader ?? Pattern, policy ?? (() => true))
        { Framing = framing, LargeContentLength = length });

    private static void Pattern(long ticks, Span<short> samples)
    {
        samples[0] = 0x1234;
        samples[1] = -2;
        samples[2] = short.MinValue;
        samples[3] = short.MaxValue;
    }

    private static HttpClient Http() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task ChunkedL16IsSignedBigEndianAndAdvertisesNoSeeking()
    {
        using var server = Create();
        server.Start();
        using var http = Http();
        using var response = await http.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/L16", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("rate=44100", response.Content.Headers.ContentType.ToString());
        Assert.True(response.Headers.TransferEncodingChunked);
        Assert.Equal("Streaming", Assert.Single(response.Headers.GetValues("transferMode.dlna.org")));
        Assert.Equal(DlnaAudioFormats.ContentFeatures, Assert.Single(response.Headers.GetValues("contentFeatures.dlna.org")));
        var bytes = new byte[ChunkBytes * 2];
        await (await response.Content.ReadAsStreamAsync()).ReadExactlyAsync(bytes);
        Assert.Equal(new byte[] { 0x12, 0x34, 0xff, 0xfe, 0x80, 0, 0x7f, 0xff }, bytes[..8]);
        Assert.All(bytes[8..ChunkBytes], b => Assert.Equal(0, b));
        Assert.Equal(bytes[..ChunkBytes], bytes[ChunkBytes..]);
        await WaitUntil(() => server.BytesSent >= bytes.Length);
        Assert.True(server.FramesSent >= DlnaAudioFormats.FramesPerChunk * 2);
        Assert.True(await server.FirstPcmTicks >= await server.FirstGetTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FiniteCompatibilityResponseHasExactContentLengthAndByteOrder(int formatValue)
    {
        var format = (DlnaAudioFormat)formatValue;
        int header = format == DlnaAudioFormat.Wav ? 44 : 0;
        using var server = Create(format, framing: DlnaHttpFraming.LargeContentLength, length: header + ChunkBytes * 2);
        server.Start();
        using var http = Http();
        using var response = await http.GetAsync(server.Url);
        Assert.Equal(header + ChunkBytes * 2, response.Content.Headers.ContentLength);
        Assert.False(response.Headers.TransferEncodingChunked ?? false);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(header + ChunkBytes * 2, body.Length);
        if (header != 0)
        {
            Assert.Equal("audio/wav", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(body, 0, 4));
            Assert.Equal((uint)(body.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
            Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(body, 8, 8));
            Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20)));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(22)));
            Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
            Assert.Equal(176400u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(28)));
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(32)));
            Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(34)));
            Assert.Equal("data", Encoding.ASCII.GetString(body, 36, 4));
            Assert.Equal((uint)(ChunkBytes * 2), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(40)));
        }
        Assert.Equal(format == DlnaAudioFormat.Wav
            ? new byte[] { 0x34, 0x12, 0xfe, 0xff, 0, 0x80, 0xff, 0x7f }
            : new byte[] { 0x12, 0x34, 0xff, 0xfe, 0x80, 0, 0x7f, 0xff }, body[header..(header + 8)]);
        await WaitUntil(() => server.BytesSent == ChunkBytes * 2);
    }

    [Fact]
    public async Task RawChunkedWavStartsWithFiniteRiffHeaderAndFreshHeaderAfterReconnect()
    {
        using var server = Create(DlnaAudioFormat.Wav);
        server.Start();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var socket = await Connect(server);
            await Send(socket, Request(server));
            Assert.Contains("200 OK", await Headers(socket.GetStream()));
            Assert.Equal("2C", await Line(socket.GetStream()));
            var wav = new byte[44];
            await socket.GetStream().ReadExactlyAsync(wav);
            Assert.Equal(DlnaAudioFormats.WavHeader(DlnaAudioFormats.MaxWavDataLength), wav);
            Assert.Equal("", await Line(socket.GetStream()));
            Assert.Equal(ChunkBytes.ToString("X"), await Line(socket.GetStream()));
            byte[] pcm = new byte[ChunkBytes];
            await socket.GetStream().ReadExactlyAsync(pcm);
            Assert.Equal(0x34, pcm[0]);
            socket.Client.LingerState = new LingerOption(true, 0);
            socket.Dispose();
            await WaitUntil(() => !server.IsStreaming);
        }
    }

    [Fact]
    public async Task HeadNeverConsumesAudioOrClaimsActiveGet()
    {
        int calls = 0;
        using var server = Create(reader: (ticks, pcm) => Interlocked.Increment(ref calls));
        server.Start();
        using var http = Http();
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(0, calls);
        Assert.False(server.FirstGetTicks.IsCompleted);
        Assert.False(server.FirstPcmTicks.IsCompleted);
        using var get = await http.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        await server.FirstPcmTicks.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(calls > 0);
    }

    [Theory]
    [InlineData("POST {path} HTTP/1.1\r\nHost: {host}\r\n\r\n", 405)]
    [InlineData("GET /wrong.l16 HTTP/1.1\r\nHost: {host}\r\n\r\n", 404)]
    [InlineData("GET {path}?token=bad HTTP/1.1\r\nHost: {host}\r\n\r\n", 404)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: other.invalid\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nHost: {host}\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nContent-Length: -1\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nContent-Length: 999999999999999999999\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nContent-Length: 1\r\n\r\nx", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nRange: bytes=0-\r\n\r\n", 416)]
    [InlineData("GET {path} HTTP/1.1\nHost: {host}\n\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost : {host}\r\n\r\n", 400)]
    [InlineData("GET {path} HTTP/1.1\r\nHost: {host}\r\nX: bad\rvalue\r\n\r\n", 400)]
    [InlineData("garbage\r\n\r\n", 400)]
    public async Task RejectsMalformedOrUnsupportedRequestsBeforeAudio(string request, int expected)
    {
        int calls = 0;
        using var server = Create(reader: (ticks, pcm) => Interlocked.Increment(ref calls));
        server.Start();
        using var socket = await Connect(server);
        await Send(socket, request.Replace("{path}", server.Url.AbsolutePath).Replace("{host}", server.Url.Authority));
        Assert.StartsWith($"HTTP/1.1 {expected} ", await Headers(socket.GetStream()));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundsBothHeaderLineAndHeaderCount(bool longLine)
    {
        using var server = Create();
        server.Start();
        using var socket = await Connect(server);
        string extra = longLine ? "X: " + new string('a', 2050) + "\r\n"
            : string.Concat(Enumerable.Range(0, 33).Select(i => $"X-{i}: a\r\n"));
        await Send(socket, Request(server, extra));
        Assert.StartsWith("HTTP/1.1 431 ", await Headers(socket.GetStream()));
        Assert.False(server.FirstPcmTicks.IsCompleted);
    }

    [Fact]
    public async Task UnauthorizedLoopbackPeerCannotInvokeSource()
    {
        int calls = 0;
        using var server = Create(reader: (ticks, pcm) => Interlocked.Increment(ref calls));
        server.Start();
        using var socket = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        await socket.ConnectAsync(IPAddress.Loopback, server.Url.Port);
        try
        {
            await Send(socket, Request(server));
            byte[] one = new byte[1];
            Assert.Equal(0, await socket.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        catch (IOException) { } // A reset is also a refusal, with no HTTP body or source invocation.
        catch (SocketException) { }
        Assert.Equal(0, calls);
        Assert.False(server.FirstGetTicks.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentGetConflictsButHeadStillWorksAndDisconnectedGetReleasesSlot()
    {
        using var server = Create();
        server.Start();
        using var first = await Connect(server);
        await Send(first, Request(server));
        Assert.Contains("200 OK", await Headers(first.GetStream()));
        using var http = Http();
        using var other = await http.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        first.Client.LingerState = new LingerOption(true, 0);
        first.Dispose();
        await WaitUntil(() => !server.IsStreaming);
        using var next = await http.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task PrivatePolicyRefusesInitialBindAndLaterClosesListenerAndExistingGet()
    {
        using var denied = Create(policy: () => false);
        Assert.Throws<InvalidOperationException>(denied.Start);
        Assert.Throws<InvalidOperationException>(() => denied.Url);
        Assert.True(denied.FirstGetTicks.IsCanceled);
        Assert.True(denied.FirstPcmTicks.IsCanceled);
        Assert.True(denied.IsPolicyClosed);
        int allowed = 1, checks = 0;
        using var server = Create(policy: () => { Interlocked.Increment(ref checks); return Volatile.Read(ref allowed) != 0; });
        server.Start();
        using var socket = await Connect(server);
        await Send(socket, Request(server));
        Assert.Contains("200 OK", await Headers(socket.GetStream()));
        await server.FirstPcmTicks.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Write(ref allowed, 0);
        await WaitUntil(() => server.IsStopped);
        Assert.True(server.IsPolicyClosed);
        Assert.InRange(checks, 3, 8); // Connection checks plus paced policy monitoring, never every PCM chunk.
        using var refused = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => refused.ConnectAsync(IPAddress.Loopback, server.Url.Port));
        await DrainUntilClosed(socket.GetStream());
    }

    [Fact]
    public async Task DisposeCancelsIdleHeadersAndDoesNotWaitForBlockedSourceCallback()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = Create(reader: (ticks, pcm) =>
        {
            entered.Set();
            release.Wait();
            returned.TrySetResult();
        });
        server.Start();
        using var idle = await Connect(server);
        using var active = await Connect(server);
        await Send(active, Request(server));
        Assert.Contains("200 OK", await Headers(active.GetStream()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Task.Run(server.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            await server.DisposeAsync();
            Assert.True(server.IsStopped);
            await DrainUntilClosed(idle.GetStream());
            await DrainUntilClosed(active.GetStream());
        }
        finally
        {
            release.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task DisposeClosesAnActualBackpressuredTcpWriterPromptly()
    {
        using var server = Create();
        server.Start();
        using var client = new TcpClient { ReceiveBufferSize = 1024 };
        await client.ConnectAsync(IPAddress.Loopback, server.Url.Port);
        await Send(client, Request(server));
        Assert.Contains("200 OK", await Headers(client.GetStream()));
        // Stop reading: a small advertised receive window eventually blocks the server's socket write.
        await WaitUntil(() => server.PendingWriteStartedTicks is long started && started != 0
            && MediaClock.Now - started > MediaClock.FromMs(100));
        await Task.Run(server.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntil(() => !server.IsStreaming && server.PendingWriteStartedTicks == 0);
        Assert.True(server.MaxWriteTicks >= MediaClock.FromMs(100));
        await DrainUntilClosed(client.GetStream());
    }

    [Fact]
    public async Task ReaderGetsSampleGridDeadlinesAndZeroedChunks()
    {
        var ticks = new ConcurrentQueue<long>();
        var nonzero = new ConcurrentQueue<bool>();
        using var server = Create(reader: (planned, pcm) =>
        {
            ticks.Enqueue(planned);
            nonzero.Enqueue(pcm.ContainsAnyExcept((short)0));
            pcm.Fill(123);
        }, framing: DlnaHttpFraming.LargeContentLength, length: ChunkBytes * 3);
        server.Start();
        long before = MediaClock.Now;
        using var http = Http();
        using var response = await http.GetAsync(server.Url);
        long[] planned = ticks.ToArray();
        Assert.Equal(3, planned.Length);
        Assert.InRange(planned[0], before, MediaClock.Now);
        for (int i = 1; i < planned.Length; i++)
        {
            // Scheduling stalls may skip chunks, but every callback remains on the original sample grid.
            long chunk = (long)Math.Round((planned[i] - planned[0]) * 44100.0 / MediaClock.Frequency / DlnaAudioFormats.FramesPerChunk);
            Assert.True(chunk >= i);
            Assert.Equal((long)((Int128)chunk * DlnaAudioFormats.FramesPerChunk * MediaClock.Frequency / 44100), planned[i] - planned[0]);
        }
        Assert.All(nonzero, value => Assert.False(value));
        Assert.Equal(planned[0], await server.FirstPcmTicks);
    }

    [Theory]
    [InlineData((int)DlnaAudioFormat.Mp3, "audio/mpeg", ".mp3", 1152)]
    [InlineData((int)DlnaAudioFormat.Aac, "audio/aac", ".aac", 1024)]
    public async Task CompressedResponsesUseCodecDurationAndFreshWorkerOnReconnect(int formatValue,
        string mime, string extension, int inputFrames)
    {
        var format = (DlnaAudioFormat)formatValue;
        int created = 0, disposed = 0, nextSample = 0;
        var inputs = new ConcurrentQueue<short[]>();
        var observations = new ConcurrentQueue<DlnaWriteObservation>();
        var starts = new ConcurrentQueue<long>();
        using var server = new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Loopback, format,
            (ticks, pcm) => { for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)nextSample++; }, () => true)
        {
            Framing = DlnaHttpFraming.LargeContentLength, LargeContentLength = 8,
            Pacing = DlnaTransferPacing.Receiver, ObserveWrite = observations.Enqueue,
            OnStreamStarted = starts.Enqueue,
            EncoderFactory = _ => { Interlocked.Increment(ref created); return new TestEncoder(inputFrames, inputs, () => Interlocked.Increment(ref disposed)); },
        });
        server.Start();
        using var http = Http();
        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url)))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(mime, head.Content.Headers.ContentType!.MediaType);
            Assert.Equal(DlnaAudioFormats.ReceiverPacedContentFeatures, Assert.Single(head.Headers.GetValues("contentFeatures.dlna.org")));
            Assert.EndsWith(extension, server.Url.AbsolutePath);
            Assert.Equal(0, created);
            Assert.Equal(0, nextSample);
            Assert.Empty(starts);
        }
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using var response = await http.GetAsync(server.Url);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(8, response.Content.Headers.ContentLength);
            await WaitUntil(() => !server.IsStreaming && Volatile.Read(ref disposed) == attempt);
            Assert.Equal(attempt * 8, server.BytesSent);
            Assert.Equal(attempt * 8, server.EncodedBytesSent);
            Assert.Equal(attempt * inputFrames * 2, server.FramesSent);
        }
        Assert.Equal(2, created);
        Assert.Equal(2, starts.Count);
        Assert.True(starts.Last() > starts.First());
        Assert.Equal(0, server.FramesSkipped);
        Assert.All(inputs, input => Assert.Equal(inputFrames * 2, input.Length));
        short[][] blocks = inputs.ToArray();
        Assert.Equal(blocks[0][^1] + 1, blocks[1][0]);
        Assert.Equal(blocks[2][^1] + 1, blocks[3][0]);
        Assert.Equal((long)nextSample / 2, server.SourceFramesRead);
        Assert.All(observations, observation => Assert.False(observation.BackpressureEligible));
    }

    [Fact]
    public async Task CompressedPrimingNeverWritesTerminatingEmptyChunk()
    {
        using var server = new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Loopback, DlnaAudioFormat.Aac, Pattern, () => true)
        {
            EncoderFactory = _ => new TestEncoder(1024, new(), () => { }),
        });
        server.Start();
        using var socket = await Connect(server);
        await Send(socket, Request(server));
        Assert.Contains("200 OK", await Headers(socket.GetStream()));
        Assert.Equal("3", await Line(socket.GetStream()));
        var first = new byte[3];
        await socket.GetStream().ReadExactlyAsync(first);
        Assert.Equal(new byte[] { 1, 2, 3 }, first);
        Assert.Equal("", await Line(socket.GetStream()));
        Assert.Equal("5", await Line(socket.GetStream()));
        socket.Client.LingerState = new LingerOption(true, 0);
    }

    [Fact]
    public async Task PartialFiniteCompressedPacketCountsBytesButNotIncompleteMediaDuration()
    {
        using var server = new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Loopback, DlnaAudioFormat.Aac, Pattern, () => true)
        {
            Framing = DlnaHttpFraming.LargeContentLength, LargeContentLength = 4,
            Pacing = DlnaTransferPacing.Receiver, EncoderFactory = _ => new TestEncoder(1024, new(), () => { }),
        });
        server.Start();
        using var http = Http();
        using var response = await http.GetAsync(server.Url);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        await WaitUntil(() => !server.IsStreaming);
        Assert.Equal(4, server.BytesSent);
        Assert.Equal(4, server.EncodedBytesSent);
        Assert.Equal(1024, server.FramesSent);
        Assert.Equal(352 * 6, server.SourceFramesRead);
    }

    [Fact]
    public async Task BeforeReadGateRunsBeforeSourceAndIsCancelledByShutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        using var server = new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Loopback, DlnaAudioFormat.Aac,
            (ticks, pcm) => Interlocked.Increment(ref reads), () => true)
        {
            EncoderFactory = _ => new TestEncoder(1024, new(), () => { }),
            BeforeReadAsync = async (ticks, ct) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cancelled.TrySetResult(); }
            },
        });
        server.Start();
        using var http = Http();
        using var response = await http.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        server.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, reads);
        Assert.Equal(0, server.SourceFramesRead);
    }

    [Fact]
    public async Task ShutdownClosesHttpWithoutWaitingForBlockedEncoder()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Loopback, DlnaAudioFormat.Mp3, Pattern, () => true)
        {
            Pacing = DlnaTransferPacing.Receiver,
            EncoderFactory = _ => new BlockedEncoder(entered, release, disposed),
        });
        server.Start();
        using var socket = await Connect(server);
        await Send(socket, Request(server));
        Assert.Contains("200 OK", await Headers(socket.GetStream()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Task.Run(server.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            await WaitUntil(() => !server.IsStreaming);
            await DrainUntilClosed(socket.GetStream());
            Assert.False(disposed.Task.IsCompleted);
        }
        finally { release.Set(); await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CodecAndSourceFailuresCloseEndpointAndExposeOriginalCause(bool codec)
    {
        var expected = new InvalidDataException(codec ? "codec failed" : "source failed");
        using var server = new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Loopback, DlnaAudioFormat.Aac,
            (ticks, pcm) => { if (!codec) throw expected; }, () => true)
        {
            Pacing = DlnaTransferPacing.Receiver,
            EncoderFactory = _ => codec ? throw expected : new TestEncoder(1024, new(), () => { }),
        });
        server.Start();
        using var socket = await Connect(server);
        await Send(socket, Request(server));
        Assert.Contains("200 OK", await Headers(socket.GetStream()));
        await WaitUntil(() => server.IsStopped && !server.IsStreaming);
        Assert.Same(expected, server.Failure);
        Assert.False(server.IsPolicyClosed);
        await DrainUntilClosed(socket.GetStream());
    }

    private sealed class BlockedEncoder(ManualResetEventSlim entered, ManualResetEventSlim release, TaskCompletionSource disposed) : IDlnaAudioEncoder
    {
        public int FramesPerInput => 1152;
        public IReadOnlyList<DlnaEncodedPacket> Encode(ReadOnlySpan<short> stereo) { entered.Set(); release.Wait(); return []; }
        public IReadOnlyList<DlnaEncodedPacket> Drain() => [];
        public void Dispose() => disposed.TrySetResult();
    }

    private sealed class TestEncoder(int frames, ConcurrentQueue<short[]> inputs, Action disposed) : IDlnaAudioEncoder
    {
        private int _calls;
        public int FramesPerInput => frames;
        public IReadOnlyList<DlnaEncodedPacket> Encode(ReadOnlySpan<short> stereo)
        {
            inputs.Enqueue(stereo.ToArray());
            return ++_calls == 1 ? [new([], 0)] : [new([1, 2, 3], frames), new([4, 5, 6, 7, 8], frames)];
        }
        public IReadOnlyList<DlnaEncodedPacket> Drain() => [];
        public void Dispose() => disposed();
    }

    [Fact]
    public void SessionTokensAreUniqueAndAddressesMustBeSelectedUnicast()
    {
        using var a = Create();
        using var b = Create();
        a.Start(); b.Start();
        Assert.NotEqual(a.Url.AbsolutePath, b.Url.AbsolutePath);
        Assert.Matches("^/stream/[a-f0-9]{32}\\.l16$", a.Url.AbsolutePath);
        Assert.Equal("127.0.0.1", a.Url.Host);
        foreach (string address in new[] { "0.0.0.0", "255.255.255.255", "224.0.0.1", "192.168.1.255", "::", "ff02::1" })
        {
            Assert.Throws<ArgumentException>(() => new DlnaStreamServer(new(IPAddress.Parse(address), IPAddress.Loopback, DlnaAudioFormat.L16, Pattern, () => true)));
            Assert.Throws<ArgumentException>(() => new DlnaStreamServer(new(IPAddress.Loopback, IPAddress.Parse(address), DlnaAudioFormat.L16, Pattern, () => true)));
        }
    }

    private static async Task<TcpClient> Connect(DlnaStreamServer server)
    {
        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, server.Url.Port);
        return socket;
    }

    private static string Request(DlnaStreamServer server, string extra = "")
        => $"GET {server.Url.AbsolutePath} HTTP/1.1\r\nHost: {server.Url.Authority}\r\n{extra}\r\n";

    private static async Task Send(TcpClient client, string request)
        => await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));

    private static async Task<string> Headers(NetworkStream stream)
    {
        string result = "";
        while (true)
        {
            string line = await Line(stream);
            if (line.Length == 0) return result;
            result += line + "\r\n";
            Assert.True(result.Length < 8192);
        }
    }

    private static async Task<string> Line(NetworkStream stream)
    {
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        while (bytes.Count < 8192)
        {
            await stream.ReadExactlyAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (one[0] == 10) return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("Response line exceeded bound.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task DrainUntilClosed(NetworkStream stream)
    {
        byte[] bytes = new byte[4096];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await stream.ReadAsync(bytes, timeout.Token) != 0) { }
        }
        catch (IOException) { }
    }
}
