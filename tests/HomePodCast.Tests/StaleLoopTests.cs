using System.Net;
using System.Reflection;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// A connect loop that Stop gave up waiting for (3 s) still finishes later: the realtime trial's RTSP setup ignores its
/// token. Whatever the receiver answers then belongs to a connection that no longer exists.
/// </summary>
public class StaleLoopTests
{
    [Theory]
    [InlineData("busy")]
    [InlineData("password")]
    [InlineData("pairing")]
    public async Task A_refusal_after_Stop_gave_up_leaves_the_new_connection_alone(string refusal)
    {
        var device = new AirPlayDevice("Test", "02:11", IPAddress.Loopback, 7001, "ThirdParty",
            new Dictionary<string, string> { ["features"] = "0x0,0x300" });
        var preferences = ReceiverPreferences.Of(device); preferences.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        var captures = new List<CountingCapture>();
        using var release = new ManualResetEventSlim();
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var controller = new StreamController
        {
            ReceiverLookup = _ => device, ReceiverSettings = _ => preferences,
            CaptureFactory = _ => { var capture = new CountingCapture(); lock (captures) captures.Add(capture); return capture; },
            ReceiverConnectorOverride = protocol => (_, _, _, _, _) =>
            {
                if (Interlocked.Increment(ref calls) > 1) return Task.FromResult<IReceiverSession>(new Session(protocol));
                firstCall.TrySetResult();
                return Task.Run(IReceiverSession () => // like the RTSP setup: the token is not looked at
                {
                    release.Wait();
                    throw refusal switch
                    {
                        "busy" => new RaopBusyException(),
                        "password" => new RaopPasswordRequiredException(),
                        _ => (Exception)new PairingRequiredException("Test", device.DeviceId, PairingReason.NotPaired),
                    };
                });
            },
        };
        int userPrompts = 0;
        controller.RaopTakeoverRequired += _ => Interlocked.Increment(ref userPrompts);
        controller.RaopPasswordRequired += _ => Interlocked.Increment(ref userPrompts);
        controller.PairingRequired += _ => Interlocked.Increment(ref userPrompts);
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += () => { if (controller.State == StreamState.Streaming) streaming.TrySetResult(); };

        controller.Start(device.DeviceId, null, 120, 10); // a click: a refusal would ask the user
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stale = (Task)typeof(StreamController).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        await Task.Run(() => controller.Start(device.DeviceId, null, 120, 10)); // Stop waits 3 s, then the new loop starts
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(10));

        release.Set(); // the old receiver answers only now
        await stale.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, captures.Count);
        Assert.True(captures[0].Disposed); // Stop's
        Assert.False(captures[1].Disposed); // the new connection's
        Assert.Equal(StreamState.Streaming, controller.State);
        Assert.Equal(0, userPrompts);
    }

    private sealed class CountingCapture : ICaptureSource
    {
        public bool Disposed;
        public string? DeviceName => "fake"; public double DriftPpm => 0; public float Peak => 0; public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class Session(ReceiverProtocol protocol) : IReceiverSession
    {
        public ReceiverProtocol Protocol => protocol;
        public IReadOnlyDictionary<string, object?> Info { get; } = new Dictionary<string, object?>();
        public int LatencyMs => 250; public int? ArrivalToRenderMs => null; public double? InitialVolumeDb => null;
        public double VolumeCapPercent { get; set; } public long PacketsSent => 0; public string? LostReason => null;
        public Action<long>? OnsetSent { get; set; }
        public event Action<string>? Lost { add { } remove { } }
        public void SetVolume(double percent) { }
        public void Dispose() { }
    }
}
