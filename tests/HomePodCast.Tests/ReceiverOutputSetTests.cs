using System.Collections.Concurrent;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class ReceiverOutputSetTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    internal sealed class Transport(int latency = 250, ReceiverProtocol protocol = ReceiverProtocol.AirPlay2Realtime) : IPreparedReceiverSession
    {
        private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReceiverProtocol Protocol => protocol;
        public bool UsesPtp => protocol == ReceiverProtocol.AirPlay2Buffered;
        public int LatencyMs => latency;
        public int? ArrivalToRenderMs => 100;
        public IReadOnlyDictionary<string, object?> Info => new Dictionary<string, object?>();
        public Task<string> Lost => _lost.Task;
        public Exception? LostException { get; private set; }
        public long PacketsSent => 0;
        public int Starts, Stops, Disposals;
        public long StartAt;
        public IReceiverAudioSource? Source;
        public readonly ConcurrentQueue<double> Volumes = new();
        public readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StartGate, DisposeGate;
        public void Start(IReceiverAudioSource source, long startAt, double percent, double cap)
        {
            Source = source; StartAt = startAt; Interlocked.Increment(ref Starts); Volumes.Enqueue(Math.Min(percent, cap));
            StartEntered.TrySetResult(); StartGate?.GetAwaiter().GetResult();
        }
        public void SetVolume(double percent, double cap) => Volumes.Enqueue(Math.Min(percent, cap));
        public void StopSending() => Interlocked.Increment(ref Stops);
        public void Dispose() { Interlocked.Increment(ref Disposals); DisposeEntered.TrySetResult(); DisposeGate?.GetAwaiter().GetResult(); }
        public void Lose(Exception error) { LostException = error; _lost.TrySetResult(error.Message); }
    }
    private static ReceiverGroupOutputOptions Output(string id, bool required = false) => new(id, id,
        _ => Task.FromResult(new ReceiverGroupTarget(new AirPlayDevice(id, id, IPAddress.Loopback, 1, "ThirdParty",
            new Dictionary<string, string> { ["features"] = "0x0,0x300" }), null)),
        new(250, 0, 100, null, null, new EffectChain())) { Required = required };
    private static TimelineBuffer History(long epoch)
    {
        var history = new TimelineBuffer(44100, 44100 * 8, epoch - MediaClock.FromMs(3000));
        float[] samples = new float[44100 * 8 * 2];
        for (int i = 0; i < samples.Length; i += 2) { samples[i] = .25f; samples[i + 1] = -.5f; }
        history.WriteFrames(0, samples); return history;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(5, stop.Token);
    }

    [Fact]
    public async Task Negotiated_transports_bind_independent_channels_on_one_history_and_current_cap()
    {
        var realtime = new Transport(); var buffered = new Transport(1500, ReceiverProtocol.AirPlay2Buffered);
        using var set = await ReceiverOutputSet.PrepareAsync([
            Output("aabb") with { Channels = ChannelMode.LeftOnly, VolumeOffset = 20 },
            Output("ccdd") with { Channels = ChannelMode.RightOnly, TrimMs = -100 },
        ], () => 25, default, connect: (_, device, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(device.DeviceId == "aabb" ? realtime : buffered));
        Assert.Equal(1600, set.MinimumDelayMs);
        Assert.All(set.Status, s => Assert.Equal(OutputState.Preparing, s.State));
        long epoch = MediaClock.Now;
        await set.StartAsync(History(epoch), 2000, 80, false, epoch, default);
        float[] pcm = new float[704];
        realtime.Source!.Read(epoch, pcm); Assert.All(pcm, value => Assert.Equal(.25f, value));
        buffered.Source!.Read(epoch, pcm); Assert.All(pcm, value => Assert.Equal(-.5f, value));
        Assert.All(realtime.Volumes, volume => Assert.Equal(25, volume));
        Assert.All(buffered.Volumes, volume => Assert.Equal(25, volume));
        Assert.All(set.Status, s => Assert.Equal(OutputState.Streaming, s.State));
    }

    [Fact]
    public async Task A_lowered_live_cap_uses_the_remote_decibel_curve_without_waiting_for_a_command()
    {
        var transport = new Transport(); double cap = 100; bool muted = false;
        using var set = await ReceiverOutputSet.PrepareAsync([Output("aabb")], () => cap, default,
            connect: (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport), liveVolume: () => (80, muted));
        long epoch = MediaClock.Now;
        await set.StartAsync(History(epoch), 2000, 80, false, epoch, default);
        cap = 40;
        float[] pcm = new float[704]; transport.Source!.Read(epoch, pcm);
        Assert.Equal(80, transport.Volumes.Last());
        float expected = .25f * (float)Math.Pow(10, (AirPlayClient.PercentToDb(40) - AirPlayClient.PercentToDb(80)) / 20);
        Assert.Equal(expected, pcm[0], 6);
        muted = true; transport.Source.Read(epoch, pcm); Assert.All(pcm, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task A_live_mute_during_start_never_opens_the_audio_gate_with_an_old_volume()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { StartGate = release.Task }; bool muted = false;
        using var set = await ReceiverOutputSet.PrepareAsync([Output("aabb")], () => 25, default,
            connect: (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport), liveVolume: () => (80, muted));
        long epoch = MediaClock.Now;
        var starting = set.StartAsync(History(epoch), 2000, 80, false, epoch, default);
        try
        {
            await transport.StartEntered.Task.WaitAsync(Guard);
            muted = true;
            float[] pcm = new float[704]; transport.Source!.Read(epoch, pcm); Assert.All(pcm, value => Assert.Equal(0, value));
            release.TrySetResult(); await starting.WaitAsync(Guard);
            Assert.Equal(0, transport.Volumes.Last());
            transport.Source.Read(epoch, pcm); Assert.All(pcm, value => Assert.Equal(0, value));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Optional_transport_loss_re_resolves_and_joins_without_affecting_the_healthy_leg()
    {
        var first = new Transport(); var rejoined = new Transport(); var healthy = new Transport();
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0, resolutions = 0;
        var optional = Output("aabb") with { Resolve = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Output("aabb").Resolve(default);
        } };
        using var set = await ReceiverOutputSet.PrepareAsync([optional, Output("ccdd", true)], () => 25, default,
            connect: (_, device, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(device.DeviceId == "ccdd" ? healthy
                : Interlocked.Increment(ref attempts) == 1 ? first : rejoined), retry: (_, ct) => retry.Task.WaitAsync(ct));
        long epoch = MediaClock.Now;
        await set.StartAsync(History(epoch), 2000, 20, false, epoch, default);
        first.Lose(new IOException("reset"));
        await Until(() => set.Status[0].Retrying);
        set.SetVolume(0, true); retry.TrySetResult();
        await Until(() => rejoined.Starts == 1);
        Assert.False(set.Lost.IsCompleted);
        Assert.Equal(0, healthy.Stops);
        Assert.True(resolutions >= 2);
        Assert.All(rejoined.Volumes, volume => Assert.Equal(0, volume));
    }

    [Fact]
    public async Task A_slower_optional_fallback_waits_for_group_delay_reconfiguration()
    {
        var initial = new Transport(); var fallback = new Transport(1500, ReceiverProtocol.AirPlay2Buffered);
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var set = await ReceiverOutputSet.PrepareAsync([Output("aabb")], () => 25, default,
            connect: (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(Interlocked.Increment(ref attempts) == 1 ? initial : fallback),
            retry: (_, ct) => retry.Task.WaitAsync(ct));
        long epoch = MediaClock.Now;
        await set.StartAsync(History(epoch), 500, 20, false, epoch, default);
        initial.Lose(new IOException("reset")); retry.TrySetResult();
        await Until(() => set.Status[0].NeedsUserAction);
        Assert.False(set.Lost.IsCompleted);
        Assert.Equal(0, fallback.Starts);
        Assert.True(fallback.Stops > 0);
    }

    [Fact]
    public async Task Required_takeover_stops_all_local_production_before_remote_cleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var required = new Transport { DisposeGate = release.Task }; var healthy = new Transport();
        using var set = await ReceiverOutputSet.PrepareAsync([Output("aabb", true), Output("ccdd")], () => 25, default,
            connect: (_, device, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(device.DeviceId == "aabb" ? required : healthy));
        long epoch = MediaClock.Now;
        await set.StartAsync(History(epoch), 2000, 20, false, epoch, default);
        try
        {
            required.Lose(new ReceiverGroupStoppedException(EventChannel.ClosedBySpeaker));
            await set.Lost.WaitAsync(Guard);
            Assert.True(set.LostRequiresUserAction);
            await Until(() => required.Stops > 0 && healthy.Stops > 0);
            Assert.False(release.Task.IsCompleted);
            Assert.IsType<ReceiverGroupStoppedException>(set.LostException);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Normalized_duplicate_ids_fail_before_resolving_and_terminal_optional_failure_is_visible()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ReceiverOutputSet.PrepareAsync([Output("aa:bb"), Output("AABB")], () => 25, default));
        using var set = await ReceiverOutputSet.PrepareAsync([Output("aabb")], () => 25, default,
            connect: (_, _, _, _, _, _) => Task.FromException<IPreparedReceiverSession>(new RaopBusyException()));
        Assert.True(set.AllTerminal);
        Assert.False(set.Lost.IsCompleted);
        Assert.True(Assert.Single(set.Status).NeedsUserAction);
    }
}
