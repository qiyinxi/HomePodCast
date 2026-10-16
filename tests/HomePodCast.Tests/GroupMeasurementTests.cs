using System.Collections.Concurrent;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class GroupMeasurementTests
{
    private sealed class Capture : ICaptureSource
    {
        public string DeviceName => "fake";
        public string EndpointId => "capture";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task An_auxiliary_only_group_has_a_distinct_identity_after_reconnecting()
    {
        using var controller = new StreamController
        {
            CaptureFactory = _ => new Capture(), CapturedOutputId = () => "capture",
            OpenLocalOutput = _ => new FakeLocalRenderDevice(),
        };
        var plan = new GroupPlan(GroupKind.MultiRoom, "local", null, [], false, false, new Dictionary<string, int>())
            { LocalOutputs = [new("local", "local")] };
        async Task Connected()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (controller.State != StreamState.Streaming) await Task.Delay(5, timeout.Token);
        }
        controller.StartGroup(plan, 150, 60);
        await Connected();
        var original = controller.AudioSessionIdentity;
        Assert.NotNull(original);
        controller.Stop();
        Assert.Null(controller.AudioSessionIdentity);
        controller.StartGroup(plan, 150, 60);
        await Connected();
        Assert.NotNull(controller.AudioSessionIdentity);
        Assert.NotSame(original, controller.AudioSessionIdentity);
        controller.SetMeasurementCap(25);
        await controller.ConfirmGroupMeasurementVolumeAsync(default);
    }

    private sealed class GatedMember(FakeMember inner) : IGroupMember
    {
        internal Action<double>? OnVolume;
        public string Name => inner.Name;
        public RtpTarget Target => inner.Target;
        public int LatencyFrames => inner.LatencyFrames;
        public int? ArrivalToRenderMs => inner.ArrivalToRenderMs;
        public double? InitialVolumeDb => inner.InitialVolumeDb;
        public string? LostReason => inner.LostReason;
        public event Action<string>? Lost { add => inner.Lost += value; remove => inner.Lost -= value; }
        public void Record() => inner.Record();
        public void Flush(ushort seq, uint rtp) => inner.Flush(seq, rtp);
        public void SetVolumePercent(double percent) { OnVolume?.Invoke(percent); inner.SetVolumePercent(percent); }
        public void Dispose() => inner.Dispose();
        internal MemberSetup Setup(int offset = 0) => new(Name, _ => Task.FromResult<IGroupMember>(this), offset);
    }

    [Fact]
    public void An_older_measurement_cannot_remove_the_newer_measurements_ceiling()
    {
        using var controller = new StreamController();
        long old = controller.SetScopedMeasurementCap(25);
        long current = controller.SetScopedMeasurementCap(10);
        controller.EndMeasurementCap(old);
        Assert.Equal(10, controller.MeasurementCapPercent);
        controller.EndMeasurementCap(current);
        Assert.Null(controller.MeasurementCapPercent);
        controller.EndMeasurementCap(current); // repeated cleanup stays harmless
        Assert.Null(controller.MeasurementCapPercent);
        controller.SetMeasurementCap(25);
        controller.EndMeasurementCap();
        Assert.Null(controller.MeasurementCapPercent);
    }

    [Fact]
    public async Task A_click_renderer_stopped_before_start_never_opens_a_render_device()
    {
        using var clicks = new ClickRenderer("this endpoint must never be opened");
        clicks.Stop();
        clicks.Start();
        await clicks.Stopped.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Confirmation_waits_for_every_ack_and_caps_positive_offsets()
    {
        using var rx = new FakeReceiver();
        var member = new GatedMember(new("test", rx, new ConcurrentQueue<string>()));
        double ceiling = 100;
        using var group = await SpeakerGroup.ConnectAsync([member.Setup(40)], new AudioFifo(44100, 20, 60), 60,
            default, volumeCap: () => ceiling);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        ceiling = 25;
        member.OnVolume = volume => { entered.TrySetResult(volume); release.Wait(); };
        Task confirmation = group.ConfirmMeasurementVolumeAsync(default);
        try
        {
            Assert.Equal(25, await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(confirmation.IsCompleted);
        }
        finally { release.Set(); }
        await confirmation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(60, group.MasterVolume); // confirmation never changes the user's master setting
    }

    [Fact]
    public async Task Confirmation_does_not_hide_a_failed_volume_command_or_allow_an_uncapped_test()
    {
        using var rx = new FakeReceiver();
        var member = new GatedMember(new("test", rx, new ConcurrentQueue<string>()));
        double ceiling = 100;
        using var group = await SpeakerGroup.ConnectAsync([member.Setup()], new AudioFifo(44100, 20, 60), 60,
            default, volumeCap: () => ceiling);
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.ConfirmMeasurementVolumeAsync(default));
        ceiling = 25;
        var failure = new IOException("fake receiver refused volume");
        member.OnVolume = _ => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => group.ConfirmMeasurementVolumeAsync(default)));
        using var controller = new StreamController();
        controller.SetMeasurementCap(25);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmGroupMeasurementVolumeAsync(default));
    }
}
