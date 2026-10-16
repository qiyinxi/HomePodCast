using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class ReceiverGroupConnectionsTests
{
    private static AirPlayDevice Device() => new("Software", "0211", IPAddress.Loopback, 7001, "ThirdParty",
        new Dictionary<string, string> { ["features"] = "0x0,0x300" }) { RaopPort = 5001 };
    private static ReceiverConnectOptions Options => new(250, 80, 25, null, null, new EffectChain());

    private sealed class Session(ReceiverProtocol protocol, Exception? failure = null) : IPreparedReceiverSession
    {
        public ReceiverProtocol Protocol => protocol;
        public bool UsesPtp => protocol == ReceiverProtocol.AirPlay2Buffered;
        public int LatencyMs => UsesPtp || protocol == ReceiverProtocol.Raop ? 2000 : 250;
        public int? ArrivalToRenderMs => null;
        public IReadOnlyDictionary<string, object?> Info => new Dictionary<string, object?>();
        public Task<string> Lost { get; } = failure == null ? new TaskCompletionSource<string>().Task : Task.FromResult(failure.Message);
        public Exception? LostException => failure;
        public long PacketsSent => 0;
        public bool Disposed;
        public void Start(IReceiverAudioSource source, long startAt, double percent, double cap) { }
        public void SetVolume(double percent, double cap) { }
        public void StopSending() { }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Auto_trials_preserve_order_and_forced_mode_never_falls_back()
    {
        using var clocks = new PtpClockPool();
        var attempts = new List<ReceiverProtocol>();
        Task<IPreparedReceiverSession> Connect(ReceiverProtocol protocol, AirPlayDevice _, ReceiverPreferences? __,
            ReceiverConnectOptions ___, PtpClockPool ____, CancellationToken _____)
        {
            attempts.Add(protocol);
            return protocol == ReceiverProtocol.AirPlay2Realtime
                ? Task.FromException<IPreparedReceiverSession>(new AirPlayException("unsupported realtime"))
                : Task.FromResult<IPreparedReceiverSession>(new Session(protocol));
        }
        using var session = await ReceiverGroupConnections.PrepareAsync(Device(), null, Options, clocks, default, Connect);
        Assert.Equal(ReceiverProtocol.Raop, session.Protocol);
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.Raop }, attempts);
        attempts.Clear();
        var settings = ReceiverPreferences.Of(Device()); settings.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        await Assert.ThrowsAsync<ReceiverGroupUnsupportedException>(() =>
            ReceiverGroupConnections.PrepareAsync(Device(), settings, Options, clocks, default, Connect));
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime }, attempts);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("password")]
    [InlineData("stopped")]
    public async Task A_refusal_never_opens_another_protocol(string kind)
    {
        using var clocks = new PtpClockPool();
        int attempts = 0;
        Exception error = kind switch
        {
            "busy" => new AirPlayException("busy") { ResponseStatusCode = 453 },
            "password" => new RaopPasswordRequiredException(),
            _ => new ReceiverGroupStoppedException("taken over"),
        };
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => ReceiverGroupConnections.PrepareAsync(Device(), null, Options,
            clocks, default, (_, _, _, _, _, _) => { attempts++; return Task.FromException<IPreparedReceiverSession>(error); }));
        Assert.True(ReceiverGroupFailures.RequiresUserAction(actual));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Pairing_requirement_is_preserved_and_no_pairing_UI_is_started_by_trials()
    {
        using var clocks = new PtpClockPool();
        var pairing = new PairingRequiredException("Receiver", "0211", PairingReason.NotPaired);
        var actual = await Assert.ThrowsAsync<PairingRequiredException>(() => ReceiverGroupConnections.PrepareAsync(Device(), null,
            Options, clocks, default, (_, _, _, _, _, _) => Task.FromException<IPreparedReceiverSession>(pairing)));
        Assert.Same(pairing, actual);
    }

    [Fact]
    public async Task Already_lost_prepared_session_is_disposed_before_the_next_trial()
    {
        using var clocks = new PtpClockPool();
        var bad = new Session(ReceiverProtocol.AirPlay2Realtime, new IOException("network loss"));
        using var session = await ReceiverGroupConnections.PrepareAsync(Device(), null, Options, clocks, default,
            (protocol, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(protocol == ReceiverProtocol.AirPlay2Realtime
                ? bad : new Session(protocol)));
        Assert.True(bad.Disposed);
        Assert.Equal(ReceiverProtocol.Raop, session.Protocol);
    }

    [Fact]
    public async Task Cancellation_and_Apple_classification_stop_before_protocol_factories()
    {
        using var clocks = new PtpClockPool();
        int attempts = 0;
        Task<IPreparedReceiverSession> Connect(ReceiverProtocol _, AirPlayDevice __, ReceiverPreferences? ___,
            ReceiverConnectOptions ____, PtpClockPool _____, CancellationToken ______)
        { attempts++; throw new InvalidOperationException("Must not connect"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiverGroupConnections.PrepareAsync(Device(), null,
            Options, clocks, cancelled.Token, Connect));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReceiverGroupConnections.PrepareAsync(
            Device() with { Model = "AudioAccessory6,1" }, null, Options, clocks, default, Connect));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Realtime_preparation_renegotiates_the_reported_floor_without_recording()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty", ArrivalToRenderMs = 320 };
        using var clocks = new PtpClockPool();
        var device = Device() with { Port = fake.Port, RaopPort = null, Txt = new Dictionary<string, string> { ["features"] = "0x0,0x100" } };
        var settings = ReceiverPreferences.Of(device); settings.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var session = await ReceiverGroupConnections.PrepareAsync(device, settings, Options, clocks, guard.Token);
        Assert.Equal(370, session.LatencyMs);
        Assert.Equal(2, fake.Requests.Count(r => r.StartsWith("SETUP stream")));
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("RECORD"));
        Assert.Equal(0, session.PacketsSent);
        Assert.Empty(fake.Errors);
    }
}
