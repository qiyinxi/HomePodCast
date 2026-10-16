using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaOutputSetTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static DlnaGroupOutputOptions Output(FakeDlnaRenderer renderer, string udn = "uuid:one", bool required = false) =>
        new(udn, udn, _ => Task.FromResult(new DlnaConnectionTarget(renderer.Device, IPAddress.Loopback, () => true)))
        { Required = required };

    private static TimelineBuffer History(long epoch)
    {
        var timeline = new TimelineBuffer(44100, 44100 * 8, epoch - MediaClock.FromMs(3000));
        var samples = new float[44100 * 8 * 2];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            samples[frame * 2] = 0.25f;
            samples[frame * 2 + 1] = -0.5f;
        }
        timeline.WriteFrames(0, samples);
        return timeline;
    }

    private static async Task<DlnaClient> Connect(DlnaGroupOutputOptions _, DlnaConnectionTarget target,
        DlnaClientOptions options, CancellationToken ct) => await DlnaClient.ConnectAsync(target.Device,
            options with { PollInterval = TimeSpan.FromMilliseconds(20), PullTimeout = Guard }, ct);

    [Fact]
    public async Task A_live_mute_changed_during_SOAP_publication_wins_before_the_audio_gate_opens()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        bool muted = false;
        DlnaClientOptions? transport = null;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 25, default,
            connect: async (o, target, options, ct) => { transport = options; return await Connect(o, target, options, ct); },
            liveVolume: () => (20, Volatile.Read(ref muted)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int blocked = 0;
        renderer.AfterActionAsync = async (action, ct) =>
        {
            if (action == "SetVolume" && Interlocked.Exchange(ref blocked, 1) == 0)
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        var starting = set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 80, false, default);
        await entered.Task.WaitAsync(Guard);
        Volatile.Write(ref muted, true);
        release.TrySetResult();
        await starting.WaitAsync(Guard);
        Assert.True(renderer.Muted);
        Assert.True(transport!.LiveMuted!());
        Assert.True(transport.LiveRemoteMuted!());
    }

    [Fact]
    public async Task Slow_initial_volume_publication_reanchors_after_the_old_history_has_wrapped()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        long epoch = MediaClock.Now;
        DlnaClientOptions? transport = null;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 25, default,
            connect: async (_, target, options, ct) =>
            {
                transport = options;
                return await DlnaClient.ConnectAsync(target.Device, options with
                {
                    ReadAudio = (_, pcm) => pcm.Clear(), BeforeReadAsync = null, ObserveWrite = null,
                    OnStreamStarted = null, Pacing = DlnaTransferPacing.Sender,
                }, ct);
            }, epoch: () => Volatile.Read(ref epoch));
        var timeline = History(epoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int blocked = 0;
        renderer.AfterActionAsync = async (action, ct) =>
        {
            if (action == "SetVolume" && Interlocked.Exchange(ref blocked, 1) == 0)
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        var starting = set.StartAsync(timeline, set.MinimumDelayMs, 20, false, default);
        await entered.Task.WaitAsync(Guard);
        Interlocked.Add(ref epoch, MediaClock.FromMs(10000));
        timeline.WriteFrames(timeline.EndFrame, Enumerable.Repeat(0.5f, 44100 * 12 * 2).ToArray());
        release.TrySetResult();
        await starting.WaitAsync(Guard);
        short[] samples = new short[704];
        transport!.ReadAudio(epoch, samples);
        Assert.All(samples, sample => Assert.Equal((short)16384, sample));
    }

    [Fact]
    public async Task A_long_same_GET_pause_does_not_keep_reading_overwritten_silence()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        long epoch = MediaClock.Now;
        DlnaClientOptions? transport = null;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 25, default,
            connect: async (_, target, options, ct) =>
            {
                transport = options;
                return await DlnaClient.ConnectAsync(target.Device, options with
                {
                    ReadAudio = (_, pcm) => pcm.Clear(), BeforeReadAsync = null, ObserveWrite = null,
                    OnStreamStarted = null, Pacing = DlnaTransferPacing.Sender,
                }, ct);
            }, epoch: () => Volatile.Read(ref epoch));
        var timeline = History(epoch);
        await set.StartAsync(timeline, set.MinimumDelayMs, 20, false, default);
        short[] samples = new short[704];
        transport!.ReadAudio(epoch, samples);
        Assert.Equal((short)8192, samples[0]);
        Interlocked.Add(ref epoch, MediaClock.FromMs(10000));
        timeline.WriteFrames(timeline.EndFrame, Enumerable.Repeat(0.5f, 44100 * 12 * 2).ToArray());
        await transport.BeforeReadAsync!(epoch, default);
        transport.ReadAudio(epoch, samples);
        Assert.All(samples, sample => Assert.Equal((short)16384, sample));
    }

    [Fact]
    public async Task Prepare_serves_silence_muted_then_binds_independent_channels_and_current_volume()
    {
        await using var left = new FakeDlnaRenderer();
        await using var right = new FakeDlnaRenderer();
        await using var leftPull = new Pull();
        await using var rightPull = new Pull();
        left.OnPlayAsync = leftPull.StartAsync;
        right.OnPlayAsync = rightPull.StartAsync;
        var transports = new ConcurrentDictionary<string, DlnaClientOptions>();
        long epoch = MediaClock.Now;
        double cap = 10;
        using var set = await DlnaOutputSet.PrepareAsync([
            Output(left, "uuid:left") with { Channels = ChannelMode.LeftOnly, TrimMs = 100, VolumeOffset = 20 },
            Output(right, "uuid:right") with { Channels = ChannelMode.RightOnly, TrimMs = -200, EstimatedLatencyMs = 1500 },
        ], () => cap, CancellationToken.None, connect: async (output, target, options, ct) =>
        {
            transports[output.Udn] = options;
            return await Connect(output, target, options, ct);
        }, epoch: () => epoch);
        Assert.Equal(2250, set.MinimumDelayMs);
        Assert.Equal(4350, set.HistoryMs(2250));
        Assert.True(left.Muted);
        Assert.True(right.Muted);
        Assert.Equal(0, leftPull.FirstSample);
        Assert.Equal(0, rightPull.FirstSample);
        Assert.All(set.Status, s => Assert.Equal(OutputState.Preparing, s.State));
        await set.StartAsync(History(epoch), set.MinimumDelayMs, 80, false, CancellationToken.None);
        short[] pcm = new short[704];
        transports["uuid:left"].ReadAudio(epoch, pcm);
        Assert.All(pcm, v => Assert.Equal((short)8192, v));
        transports["uuid:right"].ReadAudio(epoch, pcm);
        Assert.All(pcm, v => Assert.Equal((short)-16384, v));
        Assert.Equal(10, left.Volume);
        Assert.Equal(10, right.Volume);
        Assert.All(set.Status, s => Assert.Equal(OutputState.Streaming, s.State));
        Assert.All(set.Status, s => Assert.False(s.HasClockEstimate));
        cap = 5;
        Assert.Equal(5, transports["uuid:left"].LiveVolumePercent!());
        set.SetVolume(0, false);
        Assert.Equal(0, transports["uuid:left"].LiveVolumePercent!());
    }

    [Fact]
    public async Task A_prestart_mute_wins_over_initial_start_arguments()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 25, CancellationToken.None, connect: Connect);
        set.SetVolume(0, true);
        await set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 80, false, CancellationToken.None);
        Assert.Equal(0, set.MasterVolume);
        Assert.Equal(0, renderer.Volume);
        Assert.True(renderer.Muted);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("policy")]
    [InlineData("unsupported")]
    public async Task Optional_action_required_failures_are_terminal_and_keep_a_healthy_peer(string kind)
    {
        await using var healthy = new FakeDlnaRenderer();
        await using var pull = new Pull();
        healthy.OnPlayAsync = pull.StartAsync;
        int attempts = 0, notified = 0;
        Exception error = kind switch
        {
            "busy" => new DlnaBusyException(), "policy" => new DlnaPolicyException(), _ => new DlnaUnsupportedException("unsupported"),
        };
        var failed = new DlnaGroupOutputOptions("uuid:missing", "missing", _ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<DlnaConnectionTarget>(error);
        }) { ActionRequired = _ => Interlocked.Increment(ref notified) };
        using var set = await DlnaOutputSet.PrepareAsync([Output(healthy), failed], () => 10, CancellationToken.None,
            connect: Connect, retry: (_, _) => throw new Xunit.Sdk.XunitException("A terminal output retried."));
        await set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 10, false, CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Equal(1, notified);
        Assert.False(set.AllTerminal);
        Assert.False(set.Lost.IsCompleted);
        var status = set.Status.Single(s => s.Id == "dlna:uuid:missing");
        Assert.True(status.NeedsUserAction);
        Assert.False(status.Retrying);
        Assert.Equal(OutputState.Streaming, set.Status.Single(s => s.Id == "dlna:uuid:one").State);
    }

    [Fact]
    public async Task Optional_takeover_closes_only_that_leg_and_never_reclaims_the_renderer()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var healthy = new FakeDlnaRenderer();
        await using var pull = new Pull();
        await using var healthyPull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        healthy.OnPlayAsync = healthyPull.StartAsync;
        int attempts = 0;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer), Output(healthy, "uuid:healthy")], () => 10,
            CancellationToken.None, connect: async (o, target, options, ct) =>
            {
                if (o.Udn == "uuid:one") Interlocked.Increment(ref attempts);
                return await Connect(o, target, options, ct);
            });
        await set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 10, false, CancellationToken.None);
        renderer.CurrentUri = "http://192.0.2.10/phone";
        await Until(() => set.Status[0].NeedsUserAction);
        Assert.Equal(1, attempts);
        Assert.Equal(DlnaFailureKind.TakenOver, set.Status[0].FailureKind);
        Assert.False(set.Lost.IsCompleted);
        Assert.Equal(OutputState.Streaming, set.Status[1].State);
        Assert.DoesNotContain(renderer.Requests, r => r.SoapAction.EndsWith("#Stop"));
        await pull.AssertClosedAsync();
    }

    [Fact]
    public async Task A_renderer_stopped_by_its_user_stays_terminal_until_a_manual_restart()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        int attempts = 0;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 10, CancellationToken.None,
            connect: async (o, target, options, ct) => { Interlocked.Increment(ref attempts); return await Connect(o, target, options, ct); },
            retry: (_, _) => throw new Xunit.Sdk.XunitException("Stopped renderer retried."));
        await set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 10, false, CancellationToken.None);
        renderer.TransportState = "STOPPED";
        await Until(() => set.AllTerminal);
        Assert.Equal(1, attempts);
        Assert.True(set.Status[0].NeedsUserAction);
        Assert.Equal(DlnaFailureKind.Stopped, set.Status[0].FailureKind);
        Assert.False(set.Lost.IsCompleted);
        await pull.AssertClosedAsync();
    }

    [Fact]
    public async Task Master_and_mute_changed_while_Start_awaits_SOAP_win_before_audio_publication()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 25, CancellationToken.None, connect: Connect);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.AfterActionAsync = async (action, ct) =>
        {
            if (action != "SetVolume") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var starting = set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 20, false, CancellationToken.None);
        await entered.Task.WaitAsync(Guard);
        set.SetVolume(0, true);
        release.TrySetResult();
        await starting.WaitAsync(Guard);
        Assert.Equal(0, renderer.Volume);
        Assert.True(renderer.Muted);
        Assert.Equal(0, set.MasterVolume);
        Assert.Equal(OutputState.Streaming, set.Status[0].State);
    }

    [Fact]
    public async Task Optional_transient_rejoin_resolves_again_never_reuses_takeover_and_uses_latest_master()
    {
        await using var first = new FakeDlnaRenderer();
        await using var second = new FakeDlnaRenderer();
        await using var firstPull = new Pull();
        await using var secondPull = new Pull();
        first.OnPlayAsync = firstPull.StartAsync;
        second.OnPlayAsync = secondPull.StartAsync;
        int resolutions = 0;
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var takeovers = new ConcurrentQueue<bool>();
        var output = new DlnaGroupOutputOptions("uuid:one", "one", _ =>
        {
            var renderer = Interlocked.Increment(ref resolutions) == 1 ? first : second;
            return Task.FromResult(new DlnaConnectionTarget(renderer.Device, IPAddress.Loopback, () => true));
        }) { InitialTakeOverOnce = true, VolumeOffset = 20 };
        double cap = 25;
        using var set = await DlnaOutputSet.PrepareAsync([output], () => cap, CancellationToken.None,
            connect: async (o, target, options, ct) =>
            {
                takeovers.Enqueue(options.TakeOver);
                if (resolutions == 2) { reconnecting.TrySetResult(); await reconnect.Task.WaitAsync(ct); }
                return await Connect(o, target, options, ct);
            }, retry: async (_, ct) => { retrying.TrySetResult(); await retry.Task.WaitAsync(ct); });
        await set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 20, false, CancellationToken.None);
        first.Faults["GetTransportInfo"] = 501;
        await retrying.Task.WaitAsync(Guard);
        set.SetVolume(15, false);
        retry.TrySetResult();
        await reconnecting.Task.WaitAsync(Guard);
        cap = 5;
        set.SetVolume(0, true);
        reconnect.TrySetResult();
        await Until(() => set.Status[0].State == OutputState.Streaming);
        Assert.Equal(2, resolutions);
        Assert.Equal(new[] { true, false }, takeovers);
        Assert.Equal(0, second.Volume);
        Assert.True(second.Muted);
        Assert.Equal(0, secondPull.FirstSample);
        Assert.False(set.Lost.IsCompleted);
    }

    [Fact]
    public async Task Required_takeover_signals_typed_loss_and_closes_all_local_streams_before_slow_SOAP_cleanup()
    {
        await using var required = new FakeDlnaRenderer();
        await using var other = new FakeDlnaRenderer();
        await using var requiredPull = new Pull();
        await using var otherPull = new Pull();
        required.OnPlayAsync = requiredPull.StartAsync;
        other.OnPlayAsync = otherPull.StartAsync;
        var clients = new ConcurrentBag<DlnaClient>();
        using var set = await DlnaOutputSet.PrepareAsync([Output(required, "uuid:required", true), Output(other)], () => 10,
            CancellationToken.None, connect: async (o, target, options, ct) =>
            {
                var client = await Connect(o, target, options, ct); clients.Add(client); return client;
            });
        await set.StartAsync(History(MediaClock.Now), set.MinimumDelayMs, 10, false, CancellationToken.None);
        other.Delays["Stop"] = TimeSpan.FromSeconds(5);
        required.CurrentUri = "http://192.0.2.10/replacement";
        await set.Lost.WaitAsync(Guard);
        Assert.True(set.LostRequiresUserAction);
        Assert.Equal("uuid:required", set.LostOutputName);
        await Until(() => clients.All(c => c.StreamServer.IsStopped));
        await Task.WhenAll(requiredPull.AssertClosedAsync(), otherPull.AssertClosedAsync());
        Assert.DoesNotContain(required.Requests, r => r.SoapAction.EndsWith("#Stop"));
    }

    [Fact]
    public async Task Receiver_prefill_waits_for_produced_history_without_advancing_the_audio_cursor()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        DlnaClientOptions? transport = null;
        long epoch = MediaClock.Now;
        using var set = await DlnaOutputSet.PrepareAsync([Output(renderer)], () => 10, CancellationToken.None,
            connect: async (o, target, options, ct) => { transport = options; return await Connect(o, target, options, ct); },
            epoch: () => epoch);
        var timeline = new TimelineBuffer(44100, 44100 * 8, epoch - MediaClock.FromMs(3000));
        await set.StartAsync(timeline, set.MinimumDelayMs, 10, false, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var waiting = transport!.BeforeReadAsync!(epoch, stop.Token).AsTask();
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);
        timeline.WriteFrames(0, Enumerable.Repeat(0.25f, 44100 * 8 * 2).ToArray());
        await waiting.WaitAsync(Guard);
        short[] pcm = new short[704];
        transport.ReadAudio(epoch, pcm);
        Assert.All(pcm, sample => Assert.Equal((short)8192, sample));
    }

    [Fact]
    public async Task All_terminal_optional_outputs_are_observable_without_claiming_a_required_failure()
    {
        var output = new DlnaGroupOutputOptions("uuid:one", "one", _ => Task.FromException<DlnaConnectionTarget>(new DlnaBusyException()));
        using var set = await DlnaOutputSet.PrepareAsync([output], () => 10, CancellationToken.None);
        Assert.True(set.AllTerminal);
        Assert.False(set.Lost.IsCompleted);
    }

    [Fact]
    public async Task Duplicate_UDNs_are_case_insensitive_and_invalid_options_never_resolve()
    {
        int resolutions = 0;
        var output = new DlnaGroupOutputOptions("uuid:One", "one", _ =>
        {
            Interlocked.Increment(ref resolutions);
            throw new Xunit.Sdk.XunitException("Invalid options resolved.");
        });
        await Assert.ThrowsAsync<ArgumentException>(() => DlnaOutputSet.PrepareAsync([output, output with { Udn = "UUID:ONE" }], () => 10, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DlnaOutputSet.PrepareAsync([output with { TrimMs = -5001 }], () => 10, CancellationToken.None));
        Assert.Equal(0, resolutions);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!predicate()) await Task.Delay(10, guard.Token);
    }

    private sealed class Pull : IAsyncDisposable
    {
        private readonly HttpClient _http = DlnaHttp.CreateClient(IPAddress.Loopback);
        private HttpResponseMessage? _response;
        private Stream? _stream;
        public short FirstSample { get; private set; }
        public async Task StartAsync(Uri uri, CancellationToken ct)
        {
            _response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync(ct);
            byte[] sample = new byte[2];
            await _stream.ReadExactlyAsync(sample, ct);
            FirstSample = BinaryPrimitives.ReadInt16BigEndian(sample);
        }
        public async Task AssertClosedAsync()
        {
            Assert.NotNull(_stream);
            using var guard = new CancellationTokenSource(Guard);
            byte[] bytes = new byte[8192];
            int total = 0;
            try
            {
                int read;
                while ((read = await _stream.ReadAsync(bytes, guard.Token)) != 0)
                {
                    total += read;
                    Assert.True(total < 512 * 1024, "Stopped HTTP stream continued producing PCM.");
                }
            }
            catch (IOException) { }
        }
        public ValueTask DisposeAsync()
        {
            _response?.Dispose(); _http.Dispose(); return ValueTask.CompletedTask;
        }
    }
}
