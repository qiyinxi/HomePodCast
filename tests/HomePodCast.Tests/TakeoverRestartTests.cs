using System.Diagnostics;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// A single speaker taken from another sender's session starts our session once more shortly after (2026-10-16: a HomePod
/// taken from its home-theater Apple TV kept the session but played nothing until 断开 and 连接), against
/// <see cref="FakeAirPlayReceiver"/> as the HomePod.
/// </summary>
public sealed class TakeoverRestartTests
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

    private static StreamController Controller(FakeAirPlayReceiver homePod, TimeSpan restartAfter) => new()
    {
        CaptureFactory = _ => new SilentCapture(),
        AirPlayPort = homePod.Port,
        TakeoverRestartAfter = restartAfter,
    };

    private static List<string> Requests(FakeAirPlayReceiver r) => [.. r.Requests];

    private static int Count(FakeAirPlayReceiver r, string method) => Requests(r).Count(q => q == method + " [encrypted]");

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
    public async Task A_takeover_ends_its_first_session_and_starts_it_once_more()
    {
        using var homePod = new FakeAirPlayReceiver(appleTv: false) { Name = "卧室" };
        using var c = Controller(homePod, TimeSpan.FromMilliseconds(100));
        c.Start(homePod.DeviceId, "127.0.0.1", 150, 20, trigger: ConnectTrigger.TakeOver); // 仍然连接
        await Until(() => Count(homePod, "RECORD") == 2 && c.State == StreamState.Streaming, () => $"streaming in the second session: {c.StatusText}; {string.Join(", ", Requests(homePod))}");

        var requests = Requests(homePod);
        int firstRecord = requests.IndexOf("RECORD [encrypted]");
        int teardown = requests.IndexOf("TEARDOWN [encrypted]");
        int secondRecord = requests.LastIndexOf("RECORD [encrypted]");
        Assert.True(firstRecord < teardown && teardown < secondRecord, string.Join(", ", requests)); // ended cleanly in between
        Assert.True(c.OwnSession.Live);

        await Task.Delay(500);                                   // five restart periods: only once
        Assert.Equal(2, Count(homePod, "RECORD"));
        Assert.Equal(1, Count(homePod, "TEARDOWN"));
        Assert.Equal(StreamState.Streaming, c.State);
        Assert.Empty(homePod.Errors);
    }

    [Fact]
    public async Task An_ordinary_connect_keeps_its_one_session()
    {
        using var homePod = new FakeAirPlayReceiver(appleTv: false) { Name = "卧室" };
        using var c = Controller(homePod, TimeSpan.FromMilliseconds(100));
        c.Start(homePod.DeviceId, "127.0.0.1", 150, 20, trigger: ConnectTrigger.Manual);
        await Until(() => c.State == StreamState.Streaming, () => $"streaming: {c.StatusText}; {string.Join(", ", Requests(homePod))}");
        await Task.Delay(500);
        Assert.Equal(1, Count(homePod, "RECORD"));
        Assert.Equal(0, Count(homePod, "TEARDOWN"));
    }

    [Fact]
    public async Task Disconnecting_before_the_restart_ends_the_one_session_and_starts_nothing()
    {
        using var homePod = new FakeAirPlayReceiver(appleTv: false) { Name = "卧室" };
        using var c = Controller(homePod, TimeSpan.FromHours(1));
        c.Start(homePod.DeviceId, "127.0.0.1", 150, 20, trigger: ConnectTrigger.TakeOver);
        await Until(() => c.State == StreamState.Streaming, () => $"streaming: {c.StatusText}; {string.Join(", ", Requests(homePod))}");

        c.Stop();                                                // 断开 while the restart is still pending
        Assert.Equal(StreamState.Idle, c.State);
        Assert.False(c.OwnSession.Live);
        await Until(() => Count(homePod, "TEARDOWN") == 1, () => $"the TEARDOWN: {string.Join(", ", Requests(homePod))}");
        await Task.Delay(200);
        Assert.Equal(1, Count(homePod, "RECORD"));
    }
}
