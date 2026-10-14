using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class ThirdPartyTests
{
    private static AirPlayDevice Device(string model = "ThirdParty", bool legacy = true) => new("Test", "02:11", IPAddress.Loopback,
        7001, model, new Dictionary<string, string> { ["features"] = "0x0,0x300" }) { RaopPort = legacy ? 5001 : null };
    private static ReceiverConnectOptions Options => new(250, 10, 10, null, null, new EffectChain());

    [Fact]
    public void Third_party_auto_prefers_realtime_then_advertised_RAOP_then_buffered()
    {
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.Raop, ReceiverProtocol.AirPlay2Buffered },
            ReceiverProtocols.Candidates(Device(), null));
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.AirPlay2Buffered },
            ReceiverProtocols.Candidates(Device(legacy: false), null));
    }

    [Fact]
    public void Unknown_receiver_retains_buffered_before_RAOP_order()
    {
        var unknown = Device() with { Txt = new Dictionary<string, string>(), RaopPort = null };
        Assert.Equal(ReceiverKind.Unknown, unknown.Kind);
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.AirPlay2Buffered, ReceiverProtocol.Raop }, ReceiverProtocols.Candidates(unknown, null));
    }

    [Fact]
    public void Override_is_exclusive_and_successful_path_is_remembered()
    {
        var preferences = ReceiverPreferences.Of(Device());
        preferences.LastSuccessfulProtocol = ReceiverProtocol.Raop;
        Assert.Equal(ReceiverProtocol.Raop, ReceiverProtocols.Candidates(Device(), preferences)[0]);
        preferences.ProtocolOverride = ReceiverProtocol.AirPlay2Buffered;
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Buffered }, ReceiverProtocols.Candidates(Device(), preferences));
    }

    [Theory]
    [InlineData("AudioAccessory6,1")]
    [InlineData("AppleTV14,1")]
    public void Apple_paths_ignore_third_party_overrides(string model)
    {
        var device = Device(model);
        var preferences = ReceiverPreferences.Of(device);
        preferences.ProtocolOverride = ReceiverProtocol.Raop;
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime }, ReceiverProtocols.Candidates(device, preferences));
    }

    [Theory]
    [InlineData(ReceiverKind.HomePod, ReceiverProtocol.AirPlay2Realtime, 85, "", 100, 105, 120)]
    [InlineData(ReceiverKind.AppleTv, ReceiverProtocol.AirPlay2Realtime, 140, "", 155, 170, 200)]
    [InlineData(ReceiverKind.ThirdPartyAirPlay2, ReceiverProtocol.AirPlay2Realtime, 85, "", 120, 150, 250)]
    [InlineData(ReceiverKind.ThirdPartyAirPlay2, ReceiverProtocol.AirPlay2Realtime, null, "", 180, 200, 250)]
    [InlineData(ReceiverKind.ThirdPartyAirPlay2, ReceiverProtocol.AirPlay2Buffered, null, "", 1000, 1000, 2000)]
    [InlineData(ReceiverKind.AppleAirPlay1, ReceiverProtocol.Raop, null, "AirPort10,115", 1000, 2000, 2000)]
    [InlineData(ReceiverKind.ThirdPartyAirPlay1, ReceiverProtocol.Raop, null, "AppleTV3,2", 1000, 1000, 2000)]
    public void Latency_policy_matches_receiver_protocol_and_processing_time(ReceiverKind kind, ReceiverProtocol protocol,
        int? processing, string model, int minimum, int recommended, int defaultMs)
    {
        var policy = ReceiverLatencies.For(kind, protocol, processing, model);
        Assert.Equal(minimum, policy.MinimumMs); Assert.Equal(recommended, policy.RecommendedMinimumMs); Assert.Equal(defaultMs, policy.DefaultMs);
        Assert.False(policy.Warns(recommended)); Assert.True(policy.Warns(recommended - 1));
    }

    [Fact]
    public async Task Protocol_failure_is_logged_before_fallback_and_forced_mode_does_not_fallback()
    {
        var attempts = new List<ReceiverProtocol>();
        var messages = new List<string>();
        ThirdPartyConnections.Connector? Connect(ReceiverProtocol protocol) => (_, _, _, _, _) =>
        {
            attempts.Add(protocol);
            return protocol == ReceiverProtocol.AirPlay2Realtime
                ? Task.FromException<IReceiverSession>(new AirPlayException("SETUP stream: 415 Unsupported Media Type"))
                : Task.FromResult<IReceiverSession>(new Session(protocol));
        };
        using var session = await ThirdPartyConnections.ConnectAsync(Device(), null, Options with { Log = messages.Add },
            new AudioFifo(44100, 10, 40), CancellationToken.None, overrides: Connect);
        Assert.Equal(ReceiverProtocol.Raop, session.Protocol);
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime, ReceiverProtocol.Raop }, attempts);
        Assert.Contains(messages, m => m.Contains("415 Unsupported Media Type"));
        attempts.Clear();
        var forced = ReceiverPreferences.Of(Device()); forced.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        await Assert.ThrowsAsync<AirPlayException>(() => ThirdPartyConnections.ConnectAsync(Device(), forced, Options,
            new AudioFifo(44100, 10, 40), CancellationToken.None, overrides: Connect));
        Assert.Equal(new[] { ReceiverProtocol.AirPlay2Realtime }, attempts);
    }

    [Fact]
    public async Task Busy_realtime_refusal_is_never_bypassed_with_another_protocol()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<RaopBusyException>(() => ThirdPartyConnections.ConnectAsync(Device(), null, Options,
            new AudioFifo(44100, 10, 40), CancellationToken.None,
            overrides: _ => (_, _, _, _, _) =>
            {
                attempts++;
                return Task.FromException<IReceiverSession>(new AirPlayException("busy") { ResponseStatusCode = 453 });
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Realtime_fake_negotiates_receiver_floor_before_sending_audio()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty", ArrivalToRenderMs = 320 };
        var device = Device() with { Address = IPAddress.Loopback, Port = fake.Port, RaopPort = null };
        var preferences = ReceiverPreferences.Of(device); preferences.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        using var session = await ThirdPartyConnections.ConnectAsync(device, preferences, Options with { TimingPorts = fake.TimingPorts },
            new AudioFifo(44100, 10, 40), CancellationToken.None);
        Assert.InRange(session.LatencyMs, 369, 370);
        Assert.Equal(320, session.ArrivalToRenderMs);
        Assert.Equal(2, fake.Requests.Count(r => r.StartsWith("SETUP stream")));
        Assert.Equal(1, fake.Requests.Count(r => r.StartsWith("RECORD")));
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public void Preferences_round_trip_both_advertisements_and_selected_protocol()
    {
        var device = Device() with { RaopTxt = new Dictionary<string, string> { ["et"] = "1", ["cn"] = "1" } };
        var preferences = ReceiverPreferences.Of(device);
        preferences.ProtocolOverride = ReceiverProtocol.Raop;
        preferences.LastSuccessfulProtocol = ReceiverProtocol.Raop;
        var copy = System.Text.Json.JsonSerializer.Deserialize<ReceiverPreferences>(System.Text.Json.JsonSerializer.Serialize(preferences))!;
        Assert.Equal(ReceiverKind.ThirdPartyAirPlay2, copy.Device(device.DeviceId)!.Kind);
        Assert.Equal("1", copy.Device(device.DeviceId)!.RaopTxt!["et"]);
        Assert.Equal(5001, copy.Device(device.DeviceId)!.RaopPort);
        Assert.Equal(ReceiverProtocol.Raop, copy.ProtocolOverride);
    }

    [Fact]
    public async Task Controller_uses_selected_protocol_and_reports_device_processing_without_changing_HomePod_floor()
    {
        using var fake = new FakeAirPlayReceiver(false) { ModelOverride = "ThirdParty", ArrivalToRenderMs = 180 };
        var device = Device(legacy: false) with { Port = fake.Port };
        var preferences = ReceiverPreferences.Of(device); preferences.ProtocolOverride = ReceiverProtocol.AirPlay2Realtime;
        using var controller = new StreamController
        {
            ReceiverLookup = _ => device, ReceiverSettings = _ => preferences, ArrivalToRenderMs = 85,
            ReceiverTimingPorts = fake.TimingPorts,
            CaptureFactory = _ => new SilentCapture(),
        };
        var connected = new TaskCompletionSource<(ReceiverProtocol Protocol, int? Processing)>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ReceiverProtocolConnected += (_, mode, processing) => connected.TrySetResult((mode, processing));
        controller.Start(device.DeviceId, "127.0.0.1", 120, 10);
        var used = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReceiverProtocol.AirPlay2Realtime, used.Protocol); Assert.Equal(180, used.Processing);
        Assert.Equal(85, controller.ArrivalToRenderMs);
        Assert.Equal(100, controller.SafeLatency(0));
        controller.Stop();
    }

    [Fact]
    public async Task Cancellation_ends_trials_without_starting_fallback()
    {
        int attempts = 0;
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ThirdPartyConnections.ConnectAsync(Device(), null, Options,
            new AudioFifo(44100, 10, 40), cancelled.Token,
            overrides: _ => (_, _, _, _, token) =>
            {
                attempts++; cancelled.Cancel(); token.ThrowIfCancellationRequested();
                return Task.FromResult<IReceiverSession>(new Session(ReceiverProtocol.Raop));
            }));
        Assert.Equal(1, attempts);
    }

    private sealed class SilentCapture : ICaptureSource
    {
        public string? DeviceName => "fake"; public double DriftPpm => 0; public float Peak => 0; public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { } public void Dispose() { }
    }

    private sealed class Session(ReceiverProtocol protocol) : IReceiverSession
    {
        public ReceiverProtocol Protocol => protocol;
        public IReadOnlyDictionary<string, object?> Info { get; } = new Dictionary<string, object?>();
        public int LatencyMs => 2000; public int? ArrivalToRenderMs => null; public double? InitialVolumeDb => null;
        public double VolumeCapPercent { get; set; } public long PacketsSent => 0; public string? LostReason => null;
        public Action<long>? OnsetSent { get; set; }
        public event Action<string>? Lost { add { } remove { } }
        public void SetVolume(double percent) { }
        public void Dispose() { }
    }
}
