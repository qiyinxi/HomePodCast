using System.Buffers.Binary;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public sealed class MixerDestinationLatencyTests
{
    [Fact]
    public async Task Single_homepod_endpoint_keeps_legacy_latency()
    {
        var legacy = await Probe(false, false, false);
        var coordinated = await Probe(true, false, false);
        var savedAndMigrated = await Probe(true, true, false);
        Equal(legacy, coordinated); Equal(legacy, savedAndMigrated);
        Assert.True(legacy.Endpoint);
        Assert.Equal(0, legacy.ExtraLatencyMs);
        Assert.Equal(105, legacy.SafeLatencyMs);
        Assert.Equal(4630, legacy.NegotiatedFrames);
        Assert.Equal(0, legacy.MarkerFrame);
        Assert.Equal(0, coordinated.HubCalls); Assert.Equal(0, coordinated.TimelineCalls);
        var delayed = await Probe(true, true, true);
        Assert.Equal(RtpSender.FramesPerPacket, delayed.MarkerFrame);
        Assert.NotEqual(legacy.MarkerPresentationFrame, delayed.MarkerPresentationFrame);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Equal(legacy, delayed));
    }

    private static void Equal(ProbeResult a, ProbeResult b)
    {
        Assert.Equal(a.Endpoint, b.Endpoint); Assert.Equal(a.ExtraLatencyMs, b.ExtraLatencyMs);
        Assert.Equal(a.SafeLatencyMs, b.SafeLatencyMs); Assert.Equal(a.NegotiatedFrames, b.NegotiatedFrames);
        Assert.Equal(a.TargetFrames, b.TargetFrames); Assert.Equal(a.CapFrames, b.CapFrames);
        Assert.Equal(a.QueuedFrames, b.QueuedFrames); Assert.Equal(a.PrimeRead, b.PrimeRead);
        Assert.Equal(a.MarkerFrame, b.MarkerFrame); Assert.Equal(a.MarkerPresentationFrame, b.MarkerPresentationFrame);
        Assert.Equal(a.RtpTimes, b.RtpTimes); Assert.Equal(a.Pcm, b.Pcm); Assert.Equal(a.Sync, b.Sync);
    }

    private sealed record ProbeResult(bool Endpoint, int ExtraLatencyMs, int SafeLatencyMs, int NegotiatedFrames,
        int TargetFrames, int CapFrames, int QueuedFrames, bool PrimeRead, int MarkerFrame, long MarkerPresentationFrame,
        uint[] RtpTimes, byte[] Pcm, byte[] Sync, int HubCalls, int TimelineCalls);

    private static async Task<ProbeResult> Probe(bool coordinated, bool saved, bool delayed)
    {
        using var receiver = new FakeAirPlayReceiver(false) { ArrivalToRenderMs = 85 };
        var config = new AppConfig { DeviceId = receiver.DeviceId, DeviceName = "fake HomePod", Host = "127.0.0.1",
            Scene = Scene.Game, LatencyMs = 105, FifoTargetMs = 16, AutoConnect = false };
        if (saved)
        {
            for (int i = 0; i < OutputGroups.MaxGroups; i++) OutputGroups.Save(config, "inactive " + i,
                new() { DeviceId = "other" + i, GroupDelayMs = 30000 }, "inactive" + i);
            MixerRoutes.Migrate(config);
            var settings = MixerRoutes.Read(config).Settings!;
            settings.Apps["game"] = MixerRoutingV2Tests.Named("airplay:" + receiver.DeviceId);
            MixerRoutes.Write(config, settings);
            // Refresh the exact pure source from which the dropdown is built; inactive groups must stay inert.
            foreach (var group in config.SavedOutputGroups)
                Assert.True(MixerDestinationResolver.Resolve(config, new(new MixerOutput(MixerOutputKind.Group, group.Id))).Available);
        }
        int hubs = 0, timelines = 0;
        ProbeSession? session = null;
        var endpoint = !new AppRouting(config, () => { }).Rules.NeedsRouting;
        ICaptureSource Capture(ResolvedMixerDestination _, AudioFifo fifo) => new MarkerCapture(fifo, delayed, endpoint);
        await using var coordinator = new MixerOutputCoordinator(new(), (destination, factory) =>
            session = new ProbeSession(receiver, destination.Config!, factory), Capture,
            () => { hubs++; throw new InvalidOperationException("single destination allocated a hub"); });
        if (coordinated) Assert.Equal(MixerEditResult.Applied, (await coordinator.ConnectAsync(config, ConnectTrigger.Manual)).Result);
        else
        {
            var destination = MixerDestinationResolver.Resolve(config, MixerRoute.Current);
            session = new ProbeSession(receiver, config, fifo => Capture(destination, fifo));
            session.Start(ConnectTrigger.Manual);
        }
        await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await session.Transmit(hubs, timelines);
        if (!coordinated) { await session.StopAsync(); session.Dispose(); }
        Assert.Empty(receiver.Errors);
        return result;
    }

    private sealed class MarkerCapture(AudioFifo fifo, bool delayed, bool endpoint) : ICaptureSource
    {
        public bool Endpoint => endpoint;
        public string? DeviceName => "fake endpoint";
        public double DriftPpm => 0;
        public float Peak => .25f;
        public int ExtraLatencyMs => endpoint ? 0 : RoutedCapture.RoutedExtraLatencyMs;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start()
        {
            if (delayed) fifo.Write(new float[RtpSender.FramesPerPacket * 2]);
            fifo.Write(Enumerable.Repeat(.25f, RtpSender.FramesPerPacket * 2 * 6).ToArray());
        }
        public void Dispose() { }
    }

    private sealed class ProbeSession(FakeAirPlayReceiver receiver, AppConfig config, Func<AudioFifo, ICaptureSource> capture) : IMixerDestinationSession
    {
        public StreamController Controller { get; } = new(config.FifoTargetMs) { ArrivalToRenderMs = 85 };
        private ICaptureSource? _capture;
        private AirPlayClient? _client;
        private int _queued;
        public Task Ready { get; private set; } = Task.CompletedTask;
        public StreamState State => StreamState.Streaming;
        public string Status => "fake";
        public int EffectiveLatencyMs => Controller.SafeLatency(config.LatencyMs);
        public event Action? Changed { add { } remove { } }
        public void Start(ConnectTrigger trigger)
        {
            _capture = capture(Controller.Fifo); _capture.Start(); _queued = Controller.Fifo.Depth;
            Ready = Negotiate();
        }
        private async Task Negotiate()
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _client = await AirPlayClient.PrepareAsync(IPAddress.Loopback, receiver.Port,
                new StreamOptions(EffectiveLatencyMs, 0), ChannelMode.Stereo, guard.Token);
        }
        public async Task<ProbeResult> Transmit(int hubs, int timelines)
        {
            const uint rtpBase = 0xfffff000;
            const ulong epoch = 0x1020304050607080;
            using var sender = new RtpSender([_client!.StreamTarget! with { FirstSeq = 1000 }], _client.LatencyFrames, Controller.Fifo, rtpBase);
            bool prime = false;
            for (int packet = 0; packet < 6; packet++)
            {
                var samples = new float[704];
                bool read = Controller.Fifo.Read(samples); if (packet == 0) prime = read;
                var pcm = new byte[1408]; RtpSender.ToS16BigEndian(samples, pcm);
                sender.Streams[0].SendAudio((ushort)(1000 + packet), unchecked(rtpBase + (uint)(packet * 352)), pcm, packet == 0);
            }
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (receiver.Rtp.Audio.Count < 6 && DateTime.UtcNow < until) await Task.Delay(1);
            var packets = receiver.Rtp.Audio.Take(6).ToArray(); Assert.Equal(6, packets.Length);
            var bytes = packets.SelectMany(p => FakeReceiver.Decrypt(p.Bytes, receiver.StreamKey!)).ToArray();
            int marker = Enumerable.Range(0, bytes.Length / 4).First(f => BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(f * 4)) != 0);
            return new(((MarkerCapture)_capture!).Endpoint, _capture.ExtraLatencyMs, EffectiveLatencyMs, _client.LatencyFrames,
                Controller.Fifo.TargetFrames, Controller.Fifo.CapFrames, _queued, prime, marker,
                marker + _client.LatencyFrames, packets.Select(p => FakeReceiver.Rtp(p.Bytes)).ToArray(), bytes,
                RtpSender.BuildSyncPacket(rtpBase, epoch, true, _client.LatencyFrames), hubs, timelines);
        }
        public async Task StopAsync(string? reason = null) { await Ready; _client?.Dispose(); _capture?.Dispose(); }
        public void Dispose() => Controller.Dispose();
    }
}
