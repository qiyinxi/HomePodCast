using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class LocalOutputGroupTests
{
    private sealed class Capture(AudioFifo fifo) : ICaptureSource
    {
        public string DeviceName => "fake capture";
        public string EndpointId => "capture";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() => fifo.Write(TestAudio.Signal(26460));
        public void Dispose() { }
    }

    private static GroupPlan Plan(LocalOutputOptions output) => new(GroupKind.MultiRoom, "Mixed", null,
        [new("airplay", "AirPlay", "127.0.0.1")], false, false, new Dictionary<string, int>()) { LocalOutputs = [output] };

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("fake group did not reach expected state");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Controller_negotiates_the_slowest_output_and_preserves_cap_mute_and_stop()
    {
        using var receiver = new FakeReceiver();
        var device = new FakeLocalRenderDevice();
        StreamOptions? prepared = null;
        using var controller = new StreamController
        {
            CaptureFactory = fifo => new Capture(fifo), CapturedOutputId = () => "capture",
            OpenLocalOutput = id => { Assert.Equal("bluetooth", id); return device; },
            ResolveGroupMembers = (_, _) => Task.FromResult(new List<StreamController.ResolvedMember>
                { new("airplay", "AirPlay", IPAddress.Loopback, 7000) }),
            PrepareGroupMember = (member, options, channels, _) =>
            {
                prepared = options;
                return Task.FromResult<IGroupMember>(new FakeMember(member.Label, receiver, new ConcurrentQueue<string>(), channels)
                {
                    LatencyFrames = options.LatencyMs * 44100 / 1000,
                });
            },
        };
        controller.SetVolumeCap(25);
        controller.StartGroup(Plan(new("bluetooth", "Bluetooth") { ExtraLatencyMs = 200, TrimMs = -20 }), 120, 80);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Equal(262, controller.EffectiveLatencyMs); // 222 ms endpoint - (-20 ms trim) + 20 ms headroom
        Assert.Equal(242, prepared!.LatencyMs);
        Assert.Equal(25, prepared.VolumeCapPercent);
        Assert.Equal(OutputState.Streaming, Assert.Single(controller.LocalGroupOutputs).State);
        Assert.NotNull(controller.ActiveSender);
        Assert.Null(controller.Client);
        controller.SetMuted(true);
        controller.Stop();
        Assert.True(device.Disposed);
        Assert.Null(controller.ActiveSender);
        Assert.Null(controller.Capture);
    }

    [Fact]
    public async Task Controller_refuses_capture_feedback_before_resolving_or_preparing_receivers()
    {
        int resolves = 0, opens = 0;
        using var controller = new StreamController
        {
            CapturedOutputId = () => "CAPTURE",
            OpenLocalOutput = _ => { opens++; return new FakeLocalRenderDevice(); },
            ResolveGroupMembers = (_, _) => { resolves++; throw new InvalidOperationException("must not resolve"); },
        };
        controller.StartGroup(Plan(new("capture", "Local")), 120, 10);
        await Until(() => controller.State == StreamState.Retrying);
        Assert.Contains(L.T(LocalOutputSink.CapturedOutputText), controller.StatusText);
        Assert.Equal(0, resolves);
        Assert.Equal(0, opens);
        Assert.Null(controller.Capture);
    }

    [Fact]
    public async Task Optional_device_reconnects_independently_with_the_latest_volume_and_cap()
    {
        var first = new FakeLocalRenderDevice();
        var second = new FakeLocalRenderDevice();
        var enteredRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        double cap = 40;
        using var outputs = await LocalOutputSet.PrepareAsync([new("local", "Optional")], () => "capture", () => cap,
            default, _ => Interlocked.Increment(ref opens) == 1 ? first : second,
            async (_, ct) => { enteredRetry.TrySetResult(); await releaseRetry.Task.WaitAsync(ct); });
        var timeline = new TimelineBuffer(44100, 44100, 0);
        timeline.WriteFrames(0, Enumerable.Repeat(1f, 44100 * 2).ToArray());
        await outputs.StartAsync(timeline, 200, 30, default);
        await first.NextWrite();
        first.Lose();
        await enteredRetry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outputs.Lost.IsCompleted);
        Assert.Equal(OutputState.Lost, Assert.Single(outputs.Status).State);
        cap = 20; outputs.SetVolume(80);
        releaseRetry.TrySetResult();
        await Until(() => outputs.Status[0].State == OutputState.Streaming);
        await second.NextWrite();
        second.Advance(480, MediaClock.FromMs(500));
        Assert.All(await second.NextWrite(), value => Assert.InRange(value, 0.1999f, 0.2001f));
        Assert.Equal(2, opens);
        Assert.False(outputs.Lost.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_a_required_local_loss_ends_the_AirPlay_group(bool required)
    {
        using var receiver = new FakeReceiver();
        var member = new FakeMember("AirPlay", receiver, new ConcurrentQueue<string>()) { LatencyFrames = 4410 };
        var device = new FakeLocalRenderDevice();
        using var outputs = await LocalOutputSet.PrepareAsync([new("local", "Local") { Required = required }],
            () => "capture", () => 10, default, _ => device,
            (_, ct) => Task.Delay(Timeout.Infinite, ct));
        using var group = await SpeakerGroup.ConnectAsync([member.Setup()], new AudioFifo(44100, 20, 60), 10, default,
            localOutputs: outputs, groupDelayMs: 120);
        await device.NextWrite();
        device.Lose();
        await Until(() => outputs.Status[0].State == OutputState.Lost);
        if (required)
        {
            Assert.Contains("Local", await group.Lost.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("removed", group.LostReason);
        }
        else
        {
            Assert.False(outputs.Lost.IsCompleted);
            Assert.False(group.Lost.IsCompleted);
            Assert.False(member.Disposed);
            Assert.NotNull(group.Sender);
        }
    }

    [Fact]
    public async Task Missing_optional_outputs_do_not_block_prepare_but_required_ones_fail()
    {
        using var optional = await LocalOutputSet.PrepareAsync([new("missing", "Optional") { ExtraLatencyMs = 200 }],
            () => "capture", () => 10, default, _ => throw new IOException("missing"));
        Assert.Equal(OutputState.Lost, Assert.Single(optional.Status).State);
        Assert.Equal(320, StreamController.CoordinatedDelay(120, optional));
        var error = await Assert.ThrowsAsync<GroupMemberException>(() => LocalOutputSet.PrepareAsync(
            [new("missing", "Required") { Required = true }], () => "capture", () => 10, default, _ => throw new IOException("missing")));
        Assert.Equal("Required", error.Member);
    }

    [Fact]
    public async Task CLI_group_delay_covers_the_actual_AirPlay_protocol_floor_even_with_a_tiny_request()
    {
        using var outputs = await LocalOutputSet.PrepareAsync([new("local", "Local") { TrimMs = 30 }],
            () => "capture", () => 10, default, _ => new FakeLocalRenderDevice());
        Assert.Equal(120, StreamController.CoordinatedDelay(1, outputs));
    }

    [Fact]
    public async Task Duplicate_endpoint_ids_are_refused_before_any_device_is_opened()
    {
        int opens = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => LocalOutputSet.PrepareAsync([new("id", "One"), new("ID", "Two")],
            () => "capture", () => 10, default, _ => { opens++; return new FakeLocalRenderDevice(); }));
        Assert.Equal(0, opens);
    }

    [Fact]
    public void CLI_local_options_are_explicit_and_do_not_choose_a_default_device()
    {
        Assert.Empty(GroupCli.ParseLocalOutputs([]));
        var options = GroupCli.ParseLocalOutputs(["--local", "first,second", "--local-extra-ms", "200", "--local-trim-ms", "-25", "--local-required"]);
        Assert.Equal(new[] { "first", "second" }, options.Select(o => o.DeviceId));
        Assert.All(options, o => { Assert.Equal(200, o.ExtraLatencyMs); Assert.Equal(-25, o.TrimMs); Assert.True(o.Required); });
    }
}
