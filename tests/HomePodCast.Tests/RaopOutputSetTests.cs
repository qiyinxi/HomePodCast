using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public class RaopOutputSetTests
{
    private static RaopGroupOutputOptions Output(FakeGroupRaop fake, string id = "raop", bool required = false) =>
        new(id, id, IPAddress.Loopback, fake.Port, new RaopOptions
        {
            Txt = new Dictionary<string, string> { ["et"] = "0", ["cn"] = "1" },
            EndTimeout = TimeSpan.FromSeconds(5),
        }) { Required = required };

    private static TimelineBuffer History(long origin)
    {
        var timeline = new TimelineBuffer(44100, 44100 * 8, origin);
        var samples = new float[44100 * 8 * 2];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            samples[frame * 2] = 0.25f;
            samples[frame * 2 + 1] = -0.5f;
        }
        timeline.WriteFrames(0, samples);
        return timeline;
    }

    [Fact]
    public async Task A_published_required_failure_prevents_start_while_its_teardown_is_blocked()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failed = new FakeGroupRaop { FlushGate = release.Task };
        using var healthy = new FakeGroupRaop();
        using var set = await RaopOutputSet.PrepareAsync([Output(failed, "required", true), Output(healthy, "healthy")],
            () => 10, CancellationToken.None);
        try
        {
            failed.VolumeStatus = 403;
            set.SetVolume(10);
            await set.Lost.WaitAsync(TimeSpan.FromSeconds(5));
            await failed.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<GroupMemberException>(() => set.StartAsync(
                History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None));
            Assert.IsType<RaopRefusedException>(error.InnerException);
            Assert.Equal(0, healthy.Audio.Available);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Preparation_is_silent_until_timeline_volume_and_epoch_are_bound()
    {
        using var left = new FakeGroupRaop();
        using var right = new FakeGroupRaop();
        double cap = 10;
        long epoch = MediaClock.Now + MediaClock.FromMs(250);
        using var set = await RaopOutputSet.PrepareAsync([
            Output(left, "left") with { Channels = ChannelMode.LeftOnly, TrimMs = 100, VolumeOffset = 20 },
            Output(right, "right") with { Channels = ChannelMode.RightOnly, TrimMs = -200, Options = Output(right).Options with { LatencyMs = 1500 } },
        ], () => cap, CancellationToken.None, epoch: () => epoch);
        Assert.Equal(2000, set.MinimumDelayMs);
        Assert.Equal(4100, set.HistoryMs(2000));
        Assert.All(set.Status, s => Assert.Equal(OutputState.Preparing, s.State));
        Assert.All(left.Volumes, v => Assert.Equal(-144, v));
        await Task.Delay(100);
        Assert.Equal(0, left.Audio.Available);
        Assert.Equal(0, right.Audio.Available);

        await set.StartAsync(History(epoch - MediaClock.FromMs(3000)), 2000, 80, CancellationToken.None);
        var leftPacket = await left.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var rightPacket = await right.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(RaopTests.DecodeEscape(leftPacket.Buffer[12..]), v => Assert.Equal((short)8192, v));
        Assert.All(RaopTests.DecodeEscape(rightPacket.Buffer[12..]), v => Assert.Equal((short)-16384, v));
        Assert.Equal(-27, left.Volumes.Last());
        Assert.Equal(-27, right.Volumes.Last());
        Assert.All(set.Status, s => Assert.Equal(OutputState.Streaming, s.State));
        set.SetVolume(0);
        Assert.Equal(-144, left.Volumes.Last()); // A positive device offset cannot undo master mute.
        cap = 5;
        set.SetVolume(80);
        Assert.Equal(-28.5, left.Volumes.Last());
        set.Dispose();
        await Task.WhenAll(left.TornDown.Task, right.TornDown.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("FLUSH", left.Methods);
        Assert.Equal("TEARDOWN", left.Methods.Last());
    }

    [Fact]
    public async Task A_mute_published_before_Start_wins_over_its_older_initial_volume()
    {
        using var fake = new FakeGroupRaop();
        using var set = await RaopOutputSet.PrepareAsync([Output(fake) with { VolumeOffset = 20 }], () => 10, CancellationToken.None);
        set.SetVolume(0);
        await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 80, CancellationToken.None);
        await fake.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(fake.Volumes, v => Assert.Equal(-144, v));
        Assert.Equal(0, set.MasterVolume);
    }

    [Fact]
    public async Task Cancellation_of_prepared_group_tears_down_while_gate_is_waiting()
    {
        using var fake = new FakeGroupRaop();
        using var stop = new CancellationTokenSource();
        using var set = await RaopOutputSet.PrepareAsync([Output(fake)], () => 10, stop.Token);
        stop.Cancel();
        await fake.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fake.Audio.Available);
        set.Dispose();
        Assert.Equal(OutputState.Stopped, set.Status[0].State);
    }

    [Fact]
    public async Task Required_setup_failure_tears_down_already_prepared_peers()
    {
        using var fake = new FakeGroupRaop();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = Output(fake, "required", true);
        var ex = await Assert.ThrowsAsync<GroupMemberException>(() => RaopOutputSet.PrepareAsync(
            [Output(fake), failed], () => 10, CancellationToken.None,
            connect: async (output, options, ct) =>
            {
                if (output.Required)
                {
                    await connected.Task.WaitAsync(ct);
                    throw new RaopUnsupportedException();
                }
                var client = await RaopClient.ConnectAsync(output.Address, output.Port, options, ct);
                connected.TrySetResult();
                return client;
            }));
        Assert.Equal("required", ex.Member);
        Assert.IsType<RaopUnsupportedException>(ex.InnerException);
        await fake.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fake.Audio.Available);
    }

    [Fact]
    public async Task Required_transport_loss_before_start_signals_failure_and_tears_down_peers()
    {
        using var required = new FakeGroupRaop();
        using var other = new FakeGroupRaop();
        using var set = await RaopOutputSet.PrepareAsync([Output(required, "required", true), Output(other, "other")],
            () => 10, CancellationToken.None);
        required.CloseConnection(reset: true);
        await required.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        set.SetVolume(10);
        await set.Lost.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("required", set.LostOutputName);
        Assert.NotEmpty(set.LostReason!);
        Assert.NotEqual(RaopClient.ClosedByReceiver, set.LostReason);
        Assert.False(set.LostRequiresUserAction);
        await other.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, other.Audio.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_volume_refusal_is_terminal_and_required_failure_tears_down_peers(bool required)
    {
        using var refused = new FakeGroupRaop();
        using var other = new FakeGroupRaop();
        int retries = 0;
        using var set = await RaopOutputSet.PrepareAsync([Output(refused, "refused", required), Output(other, "other")],
            () => 10, CancellationToken.None,
            retry: (_, ct) => { Interlocked.Increment(ref retries); return Task.Delay(10, ct); });
        refused.VolumeStatus = 403;
        if (required)
        {
            var ex = await Assert.ThrowsAsync<GroupMemberException>(() => set.StartAsync(
                History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None));
            Assert.Equal("refused", ex.Member);
            Assert.IsType<RaopRefusedException>(ex.InnerException);
            await other.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, other.Audio.Available);
        }
        else
        {
            await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None);
            await other.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(set.Status.Single(s => s.Id == "refused").NeedsUserAction);
            Assert.False(set.Lost.IsCompleted);
        }
        await refused.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, refused.Audio.Available);
        Assert.Equal(0, retries);
    }

    [Theory]
    [InlineData("noTransport")]
    [InlineData("password")]
    [InlineData("busy")]
    [InlineData("refused")]
    [InlineData("fairplay")]
    [InlineData("preflight")]
    public async Task Optional_terminal_refusal_stays_paused_without_a_retry_loop(string failure)
    {
        using var fake = new FakeGroupRaop
        {
            NoTransport = failure == "noTransport",
            AnnounceStatus = failure == "busy" ? 453 : failure == "refused" ? 403 : 200,
        };
        int attempts = 0, retries = 0;
        var actions = new ConcurrentQueue<Exception>();
        var output = Output(fake) with { ActionRequired = actions.Enqueue };
        if (failure is "password" or "fairplay") output = output with
        {
            Options = output.Options with
            {
                Txt = failure == "password" ? new Dictionary<string, string> { ["pw"] = "true" }
                    : new Dictionary<string, string> { ["et"] = "3,5" },
            },
        };
        if (failure == "preflight") output = output with { BeforeConnect = _ => throw new RaopBusyException() };
        using var set = await RaopOutputSet.PrepareAsync([output], () => 10, CancellationToken.None,
            connect: (o, transport, ct) =>
            {
                Interlocked.Increment(ref attempts);
                return RaopClient.ConnectAsync(o.Address, o.Port, transport, ct);
            }, retry: (_, ct) => { Interlocked.Increment(ref retries); return Task.Delay(10, ct); });
        await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None);
        await Task.Delay(100);
        var status = Assert.Single(set.Status);
        Assert.Equal(OutputState.Lost, status.State);
        Assert.True(status.NeedsUserAction);
        Assert.False(status.Retrying);
        Assert.NotEmpty(status.Error!);
        Assert.Equal(failure == "preflight" ? 0 : 1, attempts);
        Assert.Equal(0, retries);
        Assert.False(set.Lost.IsCompleted);
        Assert.Equal(0, fake.Audio.Available);
        var action = Assert.Single(actions);
        switch (failure)
        {
            case "password": Assert.IsType<RaopPasswordRequiredException>(action); break;
            case "busy" or "preflight": Assert.IsType<RaopBusyException>(action); break;
            case "refused": Assert.IsType<RaopRefusedException>(action); break;
            default: Assert.IsType<RaopUnsupportedException>(action); break;
        }
    }

    [Fact]
    public async Task Terminal_recovery_preflight_notifies_once_with_original_typed_reason()
    {
        using var fake = new FakeGroupRaop();
        var action = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        int preflights = 0, attempts = 0, retries = 0, notifications = 0;
        var password = new RaopPasswordRequiredException();
        var output = Output(fake) with
        {
            BeforeConnect = _ => Interlocked.Increment(ref preflights) == 1 ? Task.CompletedTask : throw password,
            ActionRequired = ex => { Interlocked.Increment(ref notifications); action.TrySetResult(ex); },
        };
        using var set = await RaopOutputSet.PrepareAsync([output], () => 10, CancellationToken.None,
            connect: (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new SocketException((int)SocketError.ConnectionRefused);
            }, retry: (_, ct) => { Interlocked.Increment(ref retries); return Task.Delay(10, ct); });
        Assert.Same(password, await action.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None);
        set.SetVolume(0);
        await Task.Delay(100);
        Assert.Equal(2, preflights);
        Assert.Equal(1, attempts);
        Assert.Equal(1, retries);
        Assert.Equal(1, notifications);
        Assert.True(set.Status[0].NeedsUserAction);
        Assert.False(set.Lost.IsCompleted);
    }

    [Fact]
    public async Task Startup_action_callback_runs_outside_volume_lock_and_consumer_failure_is_contained()
    {
        using var refused = new FakeGroupRaop();
        using var healthy = new FakeGroupRaop();
        using var reentered = new ManualResetEventSlim();
        var outsideLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RaopOutputSet? set = null;
        Exception? observed = null;
        int notifications = 0;
        set = await RaopOutputSet.PrepareAsync([Output(refused, "refused") with
        {
            ActionRequired = ex =>
            {
                Interlocked.Increment(ref notifications);
                observed = ex;
                _ = Task.Run(() => { _ = set!.Status; reentered.Set(); });
                outsideLock.TrySetResult(reentered.Wait(TimeSpan.FromSeconds(2)));
                throw new InvalidOperationException("consumer failure must not end healthy outputs");
            },
        }, Output(healthy, "healthy")], () => 10, CancellationToken.None);
        using (set)
        {
            refused.VolumeStatus = 403;
            await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None);
            await healthy.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => Volatile.Read(ref notifications) == 1 && reentered.IsSet);
            Assert.True(await outsideLock.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsType<RaopRefusedException>(observed);
            Assert.False(set.Lost.IsCompleted);
            set.SetVolume(0);
            await Task.Delay(50);
            Assert.Equal(1, notifications);
        }
    }

    [Fact]
    public async Task Optional_recovery_uses_latest_mute_cap_and_offset_without_ending_group()
    {
        using var first = new FakeGroupRaop();
        using var recovered = new FakeGroupRaop();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        double cap = 10;
        int attempts = 0, preflights = 0, epochs = 0;
        RaopClient? secondClient = null;
        var original = Output(first) with
        {
            TrimMs = 100, VolumeOffset = 20, Channels = ChannelMode.LeftOnly,
            BeforeConnect = _ => { Interlocked.Increment(ref preflights); return Task.CompletedTask; },
            Options = Output(first).Options with { Password = "kept", LatencyMs = 1500, EncryptionOverride = 0 },
        };
        using var set = await RaopOutputSet.PrepareAsync([original], () => cap, CancellationToken.None,
            connect: async (o, transport, ct) =>
            {
                Assert.Equal("kept", transport.Password);
                Assert.Equal(1500, transport.LatencyMs);
                Assert.Equal(0, transport.EncryptionOverride);
                Assert.Same(original.Options.Txt, transport.Txt);
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt > 1)
                {
                    retryStarted.TrySetResult();
                    await reconnect.Task.WaitAsync(ct);
                }
                var client = await RaopClient.ConnectAsync(o.Address, attempt == 1 ? first.Port : recovered.Port, transport, ct);
                if (attempt > 1) secondClient = client;
                return client;
            }, retry: (_, _) => Task.CompletedTask,
            epoch: () => { Interlocked.Increment(ref epochs); return MediaClock.Now; });
        await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 1600, 80, CancellationToken.None);
        await first.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        first.CloseConnection(reset: true);
        await first.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        set.SetVolume(70); // The reset is a network loss, unlike a receiver's orderly session closure.
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cap = 3;
        set.SetVolume(0);
        reconnect.TrySetResult();
        var packet = await recovered.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(RaopTests.DecodeEscape(packet.Buffer[12..]), v => Assert.Equal((short)8192, v));
        Assert.All(recovered.Volumes, v => Assert.Equal(-144, v));
        Assert.Equal(3, secondClient!.VolumeCapPercent);
        Assert.Equal(2, preflights);
        Assert.Equal(2, epochs);
        Assert.False(set.Lost.IsCompleted);
        Assert.False(set.Status[0].NeedsUserAction);
        cap = 2;
        set.SetVolume(80);
        Assert.Equal(-29.4, recovered.Volumes.Last(), 5);
        Assert.Equal(2, secondClient.VolumeCapPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receiver_takeover_never_reconnects_and_preserves_required_loss_reason(bool required)
    {
        using var fake = new FakeGroupRaop();
        int attempts = 0, retries = 0, notifications = 0;
        using var set = await RaopOutputSet.PrepareAsync([Output(fake, required: required) with
            { ActionRequired = _ => Interlocked.Increment(ref notifications) }], () => 10, CancellationToken.None,
            connect: (o, transport, ct) =>
            {
                Interlocked.Increment(ref attempts);
                return RaopClient.ConnectAsync(o.Address, o.Port, transport, ct);
            }, retry: (_, ct) => { Interlocked.Increment(ref retries); return Task.Delay(10, ct); });
        await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, CancellationToken.None);
        await fake.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fake.CloseConnection(reset: false);
        await fake.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        set.SetVolume(5);
        if (required)
        {
            await set.Lost.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RaopClient.ClosedByReceiver, set.LostReason);
            Assert.Equal("raop", set.LostOutputName);
            Assert.True(set.LostRequiresUserAction);
        }
        else
        {
            await Until(() => set.Status[0].NeedsUserAction);
            Assert.Equal(RaopClient.ClosedByReceiver, set.Status[0].Error);
            Assert.False(set.Lost.IsCompleted);
        }
        await Task.Delay(100);
        Assert.Equal(1, attempts);
        Assert.Equal(0, retries);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Cancellation_during_optional_reconnect_closes_other_live_sessions()
    {
        using var healthy = new FakeGroupRaop();
        using var stop = new CancellationTokenSource();
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var set = await RaopOutputSet.PrepareAsync([Output(healthy, "healthy"), Output(healthy, "offline")], () => 10, stop.Token,
            connect: async (o, transport, ct) =>
            {
                if (o.Id == "healthy") return await RaopClient.ConnectAsync(o.Address, o.Port, transport, ct);
                if (Interlocked.Increment(ref attempts) == 1) throw new SocketException((int)SocketError.ConnectionRefused);
                retrying.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            }, retry: (_, _) => Task.CompletedTask);
        await set.StartAsync(History(MediaClock.Now - MediaClock.FromMs(3000)), 2000, 10, stop.Token);
        await retrying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await healthy.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => set.Status.All(s => s.State == OutputState.Stopped));
        Assert.False(set.Lost.IsCompleted);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, limit.Token);
    }

    /// <summary>Loopback RTSP/UDP only, with explicit orderly takeover and network-reset controls.</summary>
    internal sealed class FakeGroupRaop : IDisposable
    {
        private readonly TcpListener _listen = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly UdpClient _control = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _timing = new(new IPEndPoint(IPAddress.Loopback, 0));
        private TcpClient? _connection;
        private volatile bool _takenOver;
        public readonly UdpClient Audio = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly ConcurrentQueue<double> Volumes = new();
        public readonly ConcurrentQueue<string> Methods = new();
        public readonly ConcurrentQueue<byte[]> Sync = new();
        public TaskCompletionSource TornDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? FlushGate { get; init; }
        public Task? VolumeGate;
        public TaskCompletionSource VolumeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => ((IPEndPoint)_listen.LocalEndpoint).Port;
        public int AnnounceStatus { get; init; } = 200;
        public int VolumeStatus = 200;
        public bool NoTransport { get; init; }

        public FakeGroupRaop()
        {
            _listen.Start();
            _ = Run();
            _ = ReadSync();
        }

        private async Task ReadSync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    Sync.Enqueue((await _control.ReceiveAsync(_stop.Token)).Buffer);
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }

        public void CloseConnection(bool reset)
        {
            if (reset)
            {
                _connection!.Client.LingerState = new LingerOption(true, 0);
                _connection.Dispose();
            }
            else
            {
                // Keep draining the sender after FIN. Disposing a Windows socket with a pending read can send RST,
                // which would model a network fault rather than the receiver's orderly end of session.
                _takenOver = true;
                _connection!.Client.Shutdown(SocketShutdown.Send);
                Closed.TrySetResult();
            }
        }

        private async Task Run()
        {
            try
            {
                using var connection = await _listen.AcceptTcpClientAsync(_stop.Token);
                _connection = connection;
                var stream = connection.GetStream();
                var buffer = new List<byte>();
                var bytes = new byte[8192];
                int timingPort = 0;
                while (!_stop.IsCancellationRequested)
                {
                    var request = HttpMessage.TryParse(buffer);
                    if (request == null)
                    {
                        int count = await stream.ReadAsync(bytes, _stop.Token);
                        if (count == 0) return;
                        buffer.AddRange(bytes.AsSpan(0, count).ToArray());
                        continue;
                    }
                    string method = request.StartLine.Split(' ')[0];
                    Methods.Enqueue(method);
                    if (_takenOver) continue;
                    var headers = new List<KeyValuePair<string, string>>
                    {
                        new("CSeq", request.Header("CSeq")), new("Content-Length", "0"),
                    };
                    int status = method == "ANNOUNCE" ? AnnounceStatus : 200;
                    if (method == "SETUP")
                    {
                        if (!NoTransport) headers.Add(new("Transport", $"RTP/AVP/UDP;unicast;server_port={((IPEndPoint)Audio.Client.LocalEndPoint!).Port};control_port={((IPEndPoint)_control.Client.LocalEndPoint!).Port}"));
                        headers.Add(new("Session", "1"));
                        timingPort = int.Parse(request.Header("Transport").Split(';').Single(s => s.StartsWith("timing_port=")).Split('=')[1]);
                    }
                    if (method == "RECORD")
                    {
                        var timing = new byte[32];
                        timing[0] = 0x80; timing[1] = 0xd2;
                        BinaryPrimitives.WriteUInt64BigEndian(timing.AsSpan(24), MediaClock.NtpNow());
                        await _timing.SendAsync(timing, new IPEndPoint(IPAddress.Loopback, timingPort), _stop.Token);
                        await _timing.ReceiveAsync(_stop.Token);
                    }
                    if (method == "SET_PARAMETER")
                    {
                        string volume = Encoding.ASCII.GetString(request.Body)["volume: ".Length..].Trim();
                        Volumes.Enqueue(double.Parse(volume, System.Globalization.CultureInfo.InvariantCulture));
                        status = Volatile.Read(ref VolumeStatus);
                        if (Volatile.Read(ref VolumeGate) is { } volumeGate)
                        { VolumeEntered.TrySetResult(); await volumeGate.WaitAsync(_stop.Token); }
                    }
                    if (method == "FLUSH")
                    {
                        FlushEntered.TrySetResult();
                        if (FlushGate != null) await FlushGate.WaitAsync(_stop.Token);
                    }
                    await stream.WriteAsync(HttpMessage.Format($"RTSP/1.0 {status} Test", headers, []), _stop.Token);
                    if (method == "TEARDOWN") { TornDown.TrySetResult(); return; }
                }
            }
            catch (Exception ex) when (_stop.IsCancellationRequested || ex is IOException or ObjectDisposedException or SocketException) { }
            catch (Exception ex) { TornDown.TrySetException(ex); }
            finally { Closed.TrySetResult(); }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _connection?.Dispose();
            _listen.Stop();
            Audio.Dispose(); _control.Dispose(); _timing.Dispose();
        }
    }
}
