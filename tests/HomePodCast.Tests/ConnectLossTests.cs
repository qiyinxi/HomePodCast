using System.Diagnostics;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>A HomePod session that is lost while it is still being set up, against <see cref="FakeAirPlayReceiver"/>.</summary>
public sealed class ConnectLossTests
{
    private sealed class SilentCapture : ICaptureSource
    {
        public string? DeviceName => "fake";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    private static async Task Until(Func<bool> condition, Func<string> what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what());
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_session_the_speaker_ends_during_its_setup_is_retried_not_shown_as_streaming()
    {
        // The session raises Lost once. The speaker closing the event channel between the stream SETUP and the end of
        // the connect raised it before the loop listened, and the loop then waited on a dead session for good.
        int records = 0, volumes = 0;
        FakeAirPlayReceiver? homePod = null;
        homePod = new FakeAirPlayReceiver(appleTv: false)
        {
            Name = "卧室",
            RefuseRequest = request =>
            {
                if (request.StartLine.StartsWith("RECORD ") && Interlocked.Increment(ref records) == 1)
                    homePod!.CloseEventChannels();
                // The connect's last request: answered once the sender has seen the event channel close.
                if (request.StartLine.StartsWith("SET_PARAMETER ") && Interlocked.Increment(ref volumes) == 1)
                    Thread.Sleep(300);
                return null;
            },
        };
        using (homePod)
        {
            using var c = new StreamController
            {
                CaptureFactory = _ => new SilentCapture(),
                AirPlayPort = homePod.Port,
                TakeoverRestartAfter = TimeSpan.FromHours(1),
            };
            var states = new List<StreamState>();
            c.Changed += () => { lock (states) states.Add(c.State); };
            c.Start(homePod.DeviceId, "127.0.0.1", 150, 20);
            await Until(() => c.State == StreamState.Retrying,
                () => $"a retry: {c.State} ({c.StatusText}); {string.Join(", ", homePod.Requests)}");
            Assert.Contains(L.T(EventChannel.ClosedBySpeaker), c.StatusText);
            lock (states) Assert.Contains(StreamState.Streaming, states);
        }
    }
}
