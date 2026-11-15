using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// The controller's 5.1 surround path (experimental since 1.4) against the fake HomePod: switched off nothing changes, on
/// it streams 5.1, and every refusal falls back to stereo realtime in the same attempt with the reason in the status.
/// Loopback only, a fake capture and a fake encoder: nothing plays.
/// </summary>
public sealed class SurroundControllerTests
{
    private const ulong Gm = 0x00A1B2C3D4E5F607;
    private const long Offset = 3_000_000_000_000;
    private const string Name = "卧室";

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

    private sealed class Rig : IDisposable
    {
        private readonly TcpListener _data = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<TcpClient> _accepted = new();
        private readonly CancellationTokenSource _stop = new();
        private int _stereoCaptures, _surroundCaptures, _encoderChecks, _arrivals;

        public FakeAirPlayReceiver Fake { get; }
        public StreamController Controller { get; }
        public FakeAacEncoder Encoder { get; } = new();
        public ConcurrentQueue<FakeSurroundCapture> Captures { get; } = new();

        /// <summary>The 6-channel FIFO the controller gave its 5.1 captures, and its depth when each was created.</summary>
        public AudioFifo? SurroundFifo { get; private set; }
        public ConcurrentQueue<int> DepthsAtCreate { get; } = new();

        /// <summary>What every new fake 5.1 capture answers when asked whether it captures 5.1.</summary>
        public string? CaptureReady { get; init; }

        public int StereoCaptures => Volatile.Read(ref _stereoCaptures);
        public int SurroundCaptures => Volatile.Read(ref _surroundCaptures);
        public int EncoderChecks => Volatile.Read(ref _encoderChecks);
        public int Arrivals => Volatile.Read(ref _arrivals);
        public FakeSurroundCapture Capture => Captures.Single();

        public Rig(bool bit39, Func<string, SurroundRequest?>? surroundFor, string? captureReady = null)
        {
            CaptureReady = captureReady;
            _data.Start();
            Fake = new FakeAirPlayReceiver(appleTv: false)
            {
                Name = Name,
                BufferedDataPort = ((IPEndPoint)_data.LocalEndpoint).Port,
                SupportedFormats = new() { ["bufferStream"] = bit39 ? (1L << 39) | (1L << 22) : 1L << 22 },
                RequireAnchorFlags = true,
            };
            Fake.StartGrandmaster(Gm, Offset);
            _ = AcceptAsync();
            Controller = new StreamController
            {
                AirPlayPort = Fake.Port,
                CaptureFactory = _ =>
                {
                    Interlocked.Increment(ref _stereoCaptures);
                    return new SilentCapture();
                },
                SurroundFor = surroundFor,
                SurroundCaptureFactory = (fifo, _) =>
                {
                    Assert.Equal(6, fifo.Channels);
                    Assert.Equal(48000, fifo.SampleRate);
                    SurroundFifo = fifo;
                    DepthsAtCreate.Enqueue(fifo.Depth);
                    Interlocked.Increment(ref _surroundCaptures);
                    var capture = new FakeSurroundCapture { Ready = CaptureReady };
                    Captures.Enqueue(capture);
                    return capture;
                },
                ReceiverTimingPorts = Fake.TimingPorts,
                SurroundEncoder = () => Encoder,
                SurroundEncoderProblem = () =>
                {
                    Interlocked.Increment(ref _encoderChecks);
                    return null;
                },
                TakeoverRestartAfter = TimeSpan.FromHours(1),
            };
            Controller.ArrivalToRenderChanged += _ => Interlocked.Increment(ref _arrivals);
        }

        /// <summary>Holds every 5.1 data connection open, as a HomePod does.</summary>
        private async Task AcceptAsync()
        {
            try
            {
                while (true) _accepted.Enqueue(await _data.AcceptTcpClientAsync(_stop.Token));
            }
            catch (Exception) { } // stopped
        }

        public void Start(double volume = 10) => Controller.Start(Fake.DeviceId, "127.0.0.1", 150, volume);

        public int Requests(string what) => Fake.Requests.Count(r => r == what);

        public async Task Until(Func<bool> condition, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException($"{what}: {Controller.State} ({Controller.StatusText}); {string.Join(", ", Fake.Requests)}");
                await Task.Delay(10);
            }
        }

        public Task Streaming() => Until(() => Controller.State == StreamState.Streaming, "streaming");

        public void Dispose()
        {
            Controller.Dispose();
            _stop.Cancel();
            _data.Stop();
            foreach (var tcp in _accepted) tcp.Dispose();
            Fake.Dispose();
            _stop.Dispose();
        }
    }

    private static SurroundRequest On(string? refusal = null) => new(() => null, () => null) { Refusal = refusal };

    private static string NotUsed(string reason) => L.F("已连接 · {0} · 立体声。未用 5.1 环绕声：{1}", Name, reason);

    private static string StereoEndpoint => L.F("Windows 输出设备不是 5.1 或 7.1：现在是 {0} 声道", 2);

    private static double LastVolumeDb(FakeAirPlayReceiver fake) =>
        double.Parse(fake.Parameters.Last().Split(':')[1], CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(false)] // no SurroundFor at all: the CLI, the tests, the wiring before 1.4
    [InlineData(true)]  // the switch off: SurroundPolicy gives no request
    public async Task Switched_off_the_realtime_session_is_exactly_as_before(bool asked)
    {
        using var baseline = new FakeAirPlayReceiver(appleTv: false) { Name = Name };
        using (var plain = new StreamController { AirPlayPort = baseline.Port, CaptureFactory = _ => new SilentCapture() })
        {
            plain.Start(baseline.DeviceId, "127.0.0.1", 150, 10);
            var sw = Stopwatch.StartNew();
            while (plain.State != StreamState.Streaming && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
            Assert.Equal(StreamState.Streaming, plain.State);
        }

        Func<string, SurroundRequest?>? off = asked ? _ => null : null;
        using var rig = new Rig(bit39: true, off);
        rig.Start();
        await rig.Streaming();
        var c = rig.Controller;
        Assert.Equal(L.F("已连接 · {0}", Name), c.StatusText);
        Assert.False(c.SurroundActive);
        Assert.Equal(2, c.Fifo.Channels);
        Assert.Equal(44100, c.Fifo.SampleRate);
        Assert.NotNull(c.Client!.Sender);
        Assert.Equal(1, rig.StereoCaptures);
        Assert.Equal(0, rig.SurroundCaptures);
        Assert.Equal(0, rig.EncoderChecks);
        c.Stop();

        static IEnumerable<string> Sequence(FakeAirPlayReceiver fake) => fake.Requests.Where(r => r != "POST /feedback [encrypted]");
        Assert.Equal(Sequence(baseline), Sequence(rig.Fake));
        Assert.Equal(baseline.SessionSetup!.Keys.Order(), rig.Fake.SessionSetup!.Keys.Order());
        Assert.Equal(baseline.StreamSetup!.Keys.Order(), rig.Fake.StreamSetup!.Keys.Order());
        foreach (var key in new[] { "type", "audioFormat", "ct", "sr", "spf", "latencyMin", "latencyMax" })
            Assert.Equal(baseline.StreamSetup[key], rig.Fake.StreamSetup[key]);
        Assert.Equal(96L, rig.Fake.StreamSetup["type"]);
        Assert.Null(rig.Fake.RateAnchor);
        Assert.Equal(0, rig.Fake.GrandmasterSyncs); // no PTP follower ever spoke to the fake HomePod's clock
    }

    [Fact]
    public async Task On_with_a_5_1_output_it_streams_5_1()
    {
        using var rig = new Rig(bit39: true, _ => On());
        var c = rig.Controller;
        c.SetVolumeCap(30);
        rig.Start(volume: 10);
        await rig.Streaming();

        Assert.Equal(L.F("已连接 · {0} · 实验性 5.1 环绕声", Name), c.StatusText);
        Assert.True(c.SurroundActive);
        Assert.Equal(6, c.Fifo.Channels);
        Assert.Equal(48000, c.Fifo.SampleRate);
        Assert.Equal(SurroundPolicy.LatencyMs + SurroundPolicy.EncoderDelayMs, c.EffectiveLatencyMs);
        Assert.Equal(1843, c.EffectiveLatencyMs);
        Assert.Null(c.Client!.Sender);
        Assert.Same(c.Client.SurroundSender, c.AudioSessionIdentity);
        Assert.Equal(103L, rig.Fake.StreamSetup!["type"]);
        Assert.Equal((long)SurroundPolicy.LatencyMs * 48, rig.Fake.StreamSetup["latencyMin"]);
        Assert.NotNull(rig.Fake.RateAnchor);
        Assert.Equal(0, rig.Arrivals); // the realtime floor is never overwritten from a 5.1 session
        Assert.Null(c.ArrivalToRenderMs);
        Assert.Equal(0, rig.StereoCaptures);
        Assert.Equal(1, rig.SurroundCaptures);
        Assert.True(rig.Capture.Started);
        Assert.Equal(1, rig.EncoderChecks);
        Assert.Equal(AirPlayClient.PercentToDb(10), LastVolumeDb(rig.Fake), 4);

        int volumes = rig.Fake.Parameters.Count;
        c.SetVolume(80);
        await rig.Until(() => rig.Fake.Parameters.Count > volumes, "the new volume");
        Assert.Equal(AirPlayClient.PercentToDb(30), LastVolumeDb(rig.Fake), 4); // within the cap

        c.Stop();
        Assert.Contains("TEARDOWN [encrypted]", rig.Fake.Requests);
        Assert.True(rig.Capture.Disposed);
        Assert.False(c.SurroundActive);
        Assert.Equal(2, c.Fifo.Channels);
        Assert.Empty(rig.Fake.Errors);
    }

    [Fact]
    public async Task The_picture_delay_of_a_5_1_session_stays_under_the_extensions_2000_ms_clamp()
    {
        using var rig = new Rig(bit39: true, _ => On());
        rig.Start();
        await rig.Streaming();
        var c = rig.Controller;
        Assert.True(c.SurroundActive);
        // What the local API (browser extension) and the players get, computed as TrayApp does from this controller: its
        // latency, capture and FIFO. 5.1 has no calibration (CalibrationContextFor gives null for a 5.1 capture).
        int delay = TrayApp.VideoDelayFor(new AppConfig(), c.State, c.EffectiveLatencyMs, c.Capture?.ExtraLatencyMs ?? 0,
            c.Fifo.TargetMs, null);
        Assert.True(delay is > SurroundPolicy.LatencyMs and < 2000, $"{delay} ms");
    }

    [Fact]
    public async Task A_new_5_1_capture_never_sends_what_an_earlier_one_left()
    {
        using var rig = new Rig(bit39: true, _ => On());
        var c = rig.Controller;
        rig.Start();
        await rig.Streaming();
        Assert.True(c.SurroundActive);
        c.Stop();

        var leftover = new float[100 * 48 * 6]; // 100 ms the old capture wrote after the last block went out
        leftover.AsSpan().Fill(.5f);
        rig.SurroundFifo!.Write(leftover);
        Assert.True(rig.SurroundFifo.Depth > 0);
        rig.Start();
        await rig.Streaming();
        Assert.True(c.SurroundActive);
        Assert.Equal(new[] { 0, 0 }, rig.DepthsAtCreate);
        Assert.Same(rig.SurroundFifo, c.Fifo); // the same FIFO, emptied
    }

    [Fact]
    public async Task A_stereo_output_falls_back_to_stereo_in_the_same_attempt()
    {
        using var rig = new Rig(bit39: true, _ => On(), captureReady: StereoEndpoint);
        rig.Start();
        await rig.Streaming();
        var c = rig.Controller;

        Assert.Equal(NotUsed(StereoEndpoint), c.StatusText);
        Assert.False(c.SurroundActive);
        Assert.Equal(96L, rig.Fake.StreamSetup!["type"]);
        Assert.Equal(1, rig.Requests("SETUP stream [encrypted]"));
        Assert.Equal(1, rig.Requests("GET /info")); // refused before any network traffic
        Assert.True(rig.Capture.Disposed);
        Assert.Equal(1, rig.StereoCaptures);
        Assert.Equal(2, c.Fifo.Channels);
        Assert.NotNull(c.Client!.Sender);
    }

    [Fact]
    public async Task A_HomePod_without_5_1_streams_stereo_and_its_reconnects_go_straight_to_stereo()
    {
        using var rig = new Rig(bit39: false, _ => On());
        rig.Start();
        await rig.Streaming();
        var c = rig.Controller;
        string refused = NotUsed(L.T("这台音箱不支持 5.1"));
        Assert.Equal(refused, c.StatusText);
        Assert.Equal(2, rig.Requests("GET /info")); // the 5.1 check, then the stereo session's
        Assert.Equal(96L, rig.Fake.StreamSetup!["type"]);
        Assert.True(rig.Capture.Disposed);
        Assert.Equal(1, rig.StereoCaptures);

        rig.Fake.EndSessions();
        await rig.Until(() => c.State == StreamState.Retrying, "a retry");
        await rig.Until(() => c.State == StreamState.Streaming && rig.Requests("GET /info") >= 3, "the stereo reconnect");
        Assert.Equal(3, rig.Requests("GET /info")); // no second 5.1 check in this Start
        Assert.Equal(refused, c.StatusText);
        Assert.Equal(1, rig.SurroundCaptures);
        Assert.Equal(1, rig.EncoderChecks);
    }

    [Fact]
    public async Task A_refusal_known_up_front_never_tries_5_1()
    {
        string hub = L.T("多个去处同时播放时不能用");
        using var rig = new Rig(bit39: true, _ => On(hub));
        rig.Start();
        await rig.Streaming();

        Assert.Equal(NotUsed(hub), rig.Controller.StatusText);
        Assert.Equal(0, rig.SurroundCaptures);
        Assert.Equal(0, rig.EncoderChecks);
        Assert.Equal(1, rig.Requests("GET /info"));
        Assert.Equal(96L, rig.Fake.StreamSetup!["type"]);
    }

    [Fact]
    public async Task A_capture_that_stops_being_5_1_ends_the_session_and_the_retry_is_stereo()
    {
        using var rig = new Rig(bit39: true, _ => On());
        rig.Start();
        await rig.Streaming();
        var c = rig.Controller;
        Assert.True(c.SurroundActive);

        rig.Capture.Problem = StereoEndpoint; // Windows' speaker setup changed to stereo
        await rig.Until(() => c.State == StreamState.Retrying, "the lost session");
        Assert.Equal(L.F("{0}，{1:F0} 秒后重试", L.F("5.1 环绕声中断：{0}", StereoEndpoint), 2), c.StatusText);
        Assert.Contains("TEARDOWN [encrypted]", rig.Fake.Requests);

        await rig.Streaming();
        Assert.Equal(NotUsed(StereoEndpoint), c.StatusText);
        Assert.False(c.SurroundActive);
        Assert.Equal(96L, rig.Fake.StreamSetup!["type"]);
        Assert.True(rig.Capture.Disposed);
        Assert.Equal(1, rig.SurroundCaptures);
        Assert.Equal(1, rig.StereoCaptures);
        Assert.Equal(2, c.Fifo.Channels);
    }
}
