using System.Net;
using System.Reflection;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>What the app wires to a controller the mixer makes for a destination other than the startup one.</summary>
public class MixerControllerWiringTests
{
    private const string NewSpeaker = "speaker:airplay:112233445566";

    [Fact]
    public async Task A_blocked_connection_of_another_speaker_checks_the_firewall_for_that_speaker()
    {
        var config = MixerRoutingV2Tests.Speaker();
        var (app, mixer, created) = await SwitchToAnotherSpeaker(config);
        await using var ownedMixer = mixer;
        var reached = new TaskCompletionSource<IPAddress?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(app, "_firewall", new FirewallAdvisor(() => FirewallRules.Unknown, speaker =>
        {
            reached.TrySetResult(speaker);
            return NetworkProfile.Unknown; // nothing to show: no dialog in a test
        }));
        mixer.DestinationForKey(NewSpeaker)!.Config!.Host = "192.0.2.77";

        Raise(created, nameof(StreamController.FirewallBlocked));

        Assert.Equal(IPAddress.Parse("192.0.2.77"), await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Another_speakers_session_is_saved_when_it_begins_and_when_it_ends()
    {
        var config = MixerRoutingV2Tests.Speaker();
        var (_, mixer, created) = await SwitchToAnotherSpeaker(config);
        await using var ownedMixer = mixer;
        string key = OwnSession.KeyOf(config)!;
        Assert.Null(config.LastOwnSession);

        created.OwnSession.Began(key, DateTime.UtcNow);
        Raise(created, nameof(StreamController.Changed));
        Assert.Equal(key, config.LastOwnSession?.Key);
        Assert.False(config.LastOwnSession!.Clean); // what a crash now leaves: still ours for a while

        created.OwnSession.Ended(DateTime.UtcNow, clean: true); // a disconnect: Controller is the idle startup one again
        Raise(created, nameof(StreamController.Changed));
        Assert.True(config.LastOwnSession!.Clean);

        created.OwnSession.Began(OwnSession.KeyOf("AA:BB:CC:DD:EE:FF"), DateTime.UtcNow); // not the selected speaker
        Raise(created, nameof(StreamController.Changed));
        Assert.Equal(key, config.LastOwnSession?.Key);
    }

    [Fact]
    public async Task Another_sender_taking_an_app_routes_speaker_keeps_the_selected_speakers_session()
    {
        var config = MixerRoutingV2Tests.Speaker();
        var (_, mixer, created) = await SwitchToAnotherSpeaker(config);
        await using var ownedMixer = mixer;
        string key = OwnSession.KeyOf(config)!;
        var selected = new OwnSessionRecord(key, DateTime.UtcNow, Clean: false); // streaming on another controller
        config.LastOwnSession = selected;

        created.OwnSession.Began(OwnSession.KeyOf("AA:BB:CC:DD:EE:FF"), DateTime.UtcNow); // an app route's speaker
        Raise(created, nameof(StreamController.Changed));
        created.OwnSession.Forget(); // an iPhone took that speaker over: the session keeps no key
        Raise(created, nameof(StreamController.Changed));
        Assert.Same(selected, config.LastOwnSession);

        created.OwnSession.Began(key, DateTime.UtcNow); // the selected speaker's own session, taken over too: its record goes
        Raise(created, nameof(StreamController.Changed));
        created.OwnSession.Forget();
        Raise(created, nameof(StreamController.Changed));
        Assert.Null(config.LastOwnSession);
    }

    [Fact]
    public async Task The_startup_controllers_next_change_keeps_another_controllers_session()
    {
        var config = MixerRoutingV2Tests.Speaker();
        var (app, mixer, created) = await SwitchToAnotherSpeaker(config);
        await using var ownedMixer = mixer;
        StartTakeover(app);
        string key = OwnSession.KeyOf(config)!;

        created.OwnSession.Began(key, DateTime.UtcNow); // the selected speaker streams on the controller made for it
        Raise(created, nameof(StreamController.Changed));
        Raise(app.Controller, nameof(StreamController.Changed)); // the startup one, not streaming, says something new

        Assert.Equal(key, config.LastOwnSession?.Key);
        Assert.False(config.LastOwnSession!.Clean);
    }

    /// <summary>The app's own watch of Controller's session (InitTakeover), without its minute's timer.</summary>
    private static void StartTakeover(TrayApp app)
    {
        typeof(TrayApp).GetMethod("InitTakeover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
        ((IDisposable)typeof(TrayApp).GetField("_ownSessionTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!).Dispose();
    }

    /// <summary>The startup speaker connected on the startup controller, then another speaker selected: a controller made for it.</summary>
    private static async Task<(TrayApp App, MixerOutputCoordinator Mixer, StreamController Created)> SwitchToAnotherSpeaker(AppConfig config)
    {
        var app = TestApp.Create(config);
        Set(app, "_ui", new InlineContext());
        var create = typeof(TrayApp).GetMethod("CreateMixerSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mixer = new MixerOutputCoordinator(new(), (d, capture) =>
            new DormantSession((IMixerDestinationSession)create.Invoke(app, [d, capture])!),
            (_, _) => new MixerCoordinatorTests.FakeCapture(), () => new DormantHost());
        Set(app, "<Mixer>k__BackingField", mixer);
        await mixer.ConnectAsync(config, ConnectTrigger.Manual);
        Assert.Same(app.Controller, mixer.SelectedController);
        config.DeviceId = "112233445566"; config.DeviceName = "new Current";
        config.GroupSpeakers!.Add(new() { DeviceId = config.DeviceId, Name = config.DeviceName });
        MixerRoutes.Write(config, new() { Apps = new() { ["old"] = MixerRoutingV2Tests.Named("airplay:AABBCCDDEEFF") } });
        await mixer.RefreshRootsAsync([new(1, "old", false)]);
        await mixer.ConnectAsync(config, ConnectTrigger.Automatic);
        var created = mixer.ControllerForKey(NewSpeaker)!;
        Assert.NotSame(app.Controller, created);
        return (app, mixer, created);
    }

    internal static void Set(TrayApp app, string field, object value) =>
        typeof(TrayApp).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);

    /// <summary>Raises a controller's event as its stream loop would.</summary>
    private static void Raise(StreamController controller, string name) =>
        ((Action?)typeof(StreamController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller))?.Invoke();

    /// <summary>The UI thread's posts run at once.</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class DormantSession(IMixerDestinationSession inner) : IMixerDestinationSession
    {
        public StreamController? Controller => inner.Controller;
        public StreamState State => StreamState.Idle;
        public string Status => "fake";
        public int EffectiveLatencyMs => 0;
        public event Action? Changed { add { } remove { } }
        public void Start(ConnectTrigger trigger) { }
        public Task StopAsync(string? reason = null) => Task.CompletedTask;
        public void Dispose() => inner.Dispose();
    }

    internal sealed class DormantHost : IMixerCaptureHost
    {
        public IReadOnlyList<RouteTarget> Roots => [];
        public event Action? RootsChanged { add { } remove { } }
        public void Start() { }
        public void Update(AppConfig config, MixerRoutingSettings routing) { }
        public ICaptureSource Subscribe(string key, AudioFifo fifo) => new MixerCoordinatorTests.FakeCapture();
        public void Dispose() { }
    }
}
