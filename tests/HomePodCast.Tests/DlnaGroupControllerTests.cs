using System.Collections.Concurrent;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaGroupControllerTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private sealed class Capture(AudioFifo fifo) : ICaptureSource
    {
        public int Disposals;
        public string DeviceName => "fake capture";
        public string EndpointId => "capture";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() => fifo.Write(TestAudio.Signal(26460));
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class Pull : IAsyncDisposable
    {
        private readonly HttpClient _http = DlnaHttp.CreateClient(IPAddress.Loopback);
        private readonly CancellationTokenSource _stop = new();
        private HttpResponseMessage? _response;
        private Task? _drain;
        public async Task StartAsync(Uri uri, CancellationToken ct)
        {
            _response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            _response.EnsureSuccessStatusCode();
            var stream = await _response.Content.ReadAsStreamAsync(ct);
            _drain = Task.Run(async () =>
            {
                try
                {
                    byte[] buffer = new byte[4096];
                    while (await stream.ReadAsync(buffer, _stop.Token) != 0) { }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _response?.Dispose(); _http.Dispose();
            if (_drain != null) await _drain;
            _stop.Dispose();
        }
    }

    private static GroupPlan Plan(bool required = false, params GroupMemberRef[] members) => new(GroupKind.MultiRoom, "Mixed", null,
        members, false, false, new Dictionary<string, int>())
    { DlnaOutputs = [new("uuid:renderer", "DLNA") { Required = required, EstimatedLatencyMs = 1500, TrimMs = -100 }] };

    private static StreamController Controller(FakeDlnaRenderer renderer, Action<Capture>? onCapture = null) => new()
    {
        CaptureFactory = fifo => { var capture = new Capture(fifo); onCapture?.Invoke(capture); return capture; },
        CapturedOutputId = () => "capture",
        ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members.Select(m =>
            new StreamController.ResolvedMember(m.DeviceId, m.Name, IPAddress.Loopback, 12345)).ToList()),
        ResolveGroupDlnaOutput = (_, _) => Task.FromResult(new DlnaConnectionTarget(renderer.Device, IPAddress.Loopback, () => true)),
        ConnectGroupDlnaOutput = (_, target, options, ct) => DlnaClient.ConnectAsync(target.Device,
            options with { PollInterval = TimeSpan.FromMilliseconds(20), PullTimeout = Guard }, ct),
    };

    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(5, stop.Token);
    }

    [Fact]
    public async Task DLNA_only_controller_uses_one_history_and_stops_capture_without_an_AirPlay_sender()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        Capture? capture = null;
        using var controller = Controller(renderer, c => capture = c);
        controller.SetVolumeCap(25);
        controller.StartGroup(Plan(), 120, 80);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Equal(1870, controller.EffectiveLatencyMs); // 1500 ms estimate + 100 ms advance + 250 ms prefill + 20 ms producer.
        Assert.Null(controller.ActiveSender);
        Assert.Null(controller.Client);
        Assert.Equal(OutputState.Streaming, Assert.Single(controller.DlnaGroupOutputs).State);
        Assert.Equal(25, renderer.Volume);
        Assert.False(renderer.Muted);
        controller.SetMuted(true);
        await Until(() => renderer.Muted);
        controller.Stop();
        Assert.Null(controller.Capture);
        Assert.Equal(1, capture!.Disposals);
    }

    [Fact]
    public async Task Optional_DLNA_takeover_leaves_the_HomePod_and_capture_running()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        using var receiver = new FakeReceiver();
        var homePod = new FakeMember("HomePod", receiver, new ConcurrentQueue<string>());
        Capture? capture = null;
        using var controller = Controller(renderer, c => capture = c);
        controller.PrepareGroupMember = (_, _, _, _) => Task.FromResult<IGroupMember>(homePod);
        controller.StartGroup(Plan(false, new GroupMemberRef("aabb", "HomePod", null)), 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        var sender = controller.ActiveSender;
        renderer.CurrentUri = "http://192.0.2.10/phone";
        await Until(() => controller.DlnaGroupOutputs.Any(o => o.NeedsUserAction));
        Assert.Equal(StreamState.Streaming, controller.State);
        Assert.Same(sender, controller.ActiveSender);
        Assert.Equal(0, capture!.Disposals);
        Assert.False(homePod.Disposed);
        controller.Stop();
        Assert.DoesNotContain(renderer.Requests, r => r.SoapAction.EndsWith("#Stop"));
    }

    [Fact]
    public async Task Required_DLNA_takeover_cancels_a_pending_AirPlay_prepare_and_keeps_the_named_reason()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Capture? capture = null;
        using var controller = Controller(renderer, c => capture = c);
        controller.PrepareGroupMember = async (_, _, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new Xunit.Sdk.XunitException("Preparation unexpectedly completed.");
        };
        controller.StartGroup(Plan(true, new GroupMemberRef("aabb", "HomePod", null)), 120, 10);
        await entered.Task.WaitAsync(Guard);
        renderer.CurrentUri = "http://192.0.2.10/phone";
        await cancelled.Task.WaitAsync(Guard);
        await Until(() => controller.State == StreamState.Idle && capture!.Disposals == 1);
        Assert.Contains("DLNA", controller.StatusText);
        Assert.Contains(L.T("DLNA 设备已被其他控制器接管。"), controller.StatusText);
        Assert.Null(controller.Capture);
    }

    [Fact]
    public async Task Terminal_RAOP_does_not_end_a_healthy_DLNA_only_capture()
    {
        await using var renderer = new FakeDlnaRenderer();
        await using var pull = new Pull();
        renderer.OnPlayAsync = pull.StartAsync;
        using var dlna = await DlnaOutputSet.PrepareAsync([new("uuid:renderer", "DLNA",
            _ => Task.FromResult(new DlnaConnectionTarget(renderer.Device, IPAddress.Loopback, () => true)))], () => 25, default,
            connect: (_, target, options, ct) => DlnaClient.ConnectAsync(target.Device,
                options with { PollInterval = TimeSpan.FromMilliseconds(20) }, ct));
        using var raop = await RaopOutputSet.PrepareAsync([new("aabb", "RAOP", IPAddress.Loopback, 12345,
            new RaopOptions { ReadAudio = pcm => pcm.Clear() })], () => 25, default,
            connect: (_, _, _) => Task.FromException<RaopClient>(new RaopBusyException()));
        int delay = Math.Max(raop.MinimumDelayMs, dlna.MinimumDelayMs) + TimelinePump.HeadroomMs;
        using var group = await SpeakerGroup.ConnectAsync([], new AudioFifo(44100, 20, 60), 10, default,
            raopOutputs: raop, dlnaOutputs: dlna, groupDelayMs: delay);
        Assert.True(group.TimelineRunning);
        Assert.False(group.Lost.IsCompleted);
        renderer.TransportState = "STOPPED";
        await group.Lost.WaitAsync(Guard);
        Assert.True(group.LostRequiresUserAction);
    }
}
