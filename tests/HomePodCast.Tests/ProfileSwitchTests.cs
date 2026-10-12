using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// Applying a profile that switches to another speaker (device checklist 「使用配置」), as TrayApp.ApplyUsageProfile does it,
/// on fake speakers (StreamController's group path: no mDNS, no RTSP, no audio device): the previous speaker's volume
/// never becomes the new one's, the picker shows the new speaker, and a busy speaker is waited for.
/// </summary>
public class ProfileSwitchTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeReceiver> _receivers = [];
    private readonly List<FakeMember> _members = [];
    private readonly List<StreamController> _controllers = [];

    public void Dispose()
    {
        foreach (var c in _controllers) c.Dispose();
        foreach (var rx in _receivers) rx.Dispose();
    }

    private sealed class FakeCapture : ICaptureSource
    {
        public string? DeviceName => "fake";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    private StreamController Controller()
    {
        var c = new StreamController
        {
            CaptureFactory = _ => new FakeCapture(),
            ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members
                .Select(m => new StreamController.ResolvedMember(m.DeviceId, m.Name, IPAddress.Loopback, 7000)).ToList()),
        };
        c.PrepareGroupMember = (m, _, channels, _) =>
        {
            var rx = new FakeReceiver();
            var member = new FakeMember(m.DeviceId, rx, _log, channels); // its own volume: -15 dB = 50 %
            lock (_members)
            {
                _receivers.Add(rx);
                _members.Add(member);
            }
            return Task.FromResult<IGroupMember>(member);
        };
        _controllers.Add(c);
        return c;
    }

    private List<FakeMember> Members(string prefix)
    {
        lock (_members) return _members.Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private static List<double> Volumes(IEnumerable<FakeMember> members) =>
        members.SelectMany(m => { lock (m.Volumes) return m.Volumes.ToList(); }).ToList();

    private static async Task Until(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    /// <summary>Speakers A (bedroom + study) and B (kitchen + hall), each a multi-room pair so the fakes can stand in.</summary>
    private static AppConfig SelectingA() => new()
    {
        DeviceId = "AA:00:00:00:00:01", DeviceName = "卧室", Host = "10.0.0.1",
        MultiRoomDeviceId = "AA:00:00:00:00:02", MultiRoomDeviceName = "书房", MultiRoomHost = "10.0.0.2",
    };

    private static UsageSettings ProfileForB(int cap) => new()
    {
        DeviceId = "BB:00:00:00:00:01", DeviceName = "厨房", MultiRoomDeviceId = "BB:00:00:00:00:02", MultiRoomDeviceName = "客厅",
        VolumeCapPercent = cap, LatencyMs = 150,
    };

    /// <summary>What TrayApp.Connect hands the controller: the settings as they are when the connect is asked for.</summary>
    private static void Connect(StreamController c, AppConfig cfg, ConnectTrigger trigger) =>
        c.StartGroup(GroupPlan.FromConfig(cfg)!, cfg.LatencyMs, cfg.Volume, c.VolumeChanges, trigger);

    [Fact]
    public async Task A_volume_dragged_on_the_previous_speaker_is_never_saved_for_or_sent_to_the_speaker_a_profile_switches_to()
    {
        var c = Controller();
        var cfg = SelectingA();
        cfg.LatencyMs = 150;
        Connect(c, cfg, ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "A streaming");
        Assert.True(c.OwnSession.IsLiveFor(OwnSession.KeyOf(cfg)));

        // 首页's slider: PreviewVolume sends 70 % to A at once; applying the profile flushes the slider's debounce, which
        // saves it (TrayApp.SetVolume) for A, still the selected speaker.
        c.SetVolume(70);
        await Until(() => Volumes(Members("AA")).Count(v => v == 70) == 2, "70 % on A");
        cfg.Volume = 70;

        // The profile for B, with a lower cap. Applied while A still streams: the queued restart stops A later.
        UsageProfiles.Apply(cfg, ProfileForB(cap: 60));
        Assert.Null(cfg.Volume);                                   // B plays at its own volume
        c.SetVolumeCap(cfg.VolumeCapPercent);                      // ApplyUsageProfile: the cap applies at once...
        Assert.Equal(60, c.Volume);                                // ...and lowers A, which reports it (Changed)
        Assert.Equal(StreamState.Streaming, c.State);
        // That report must not save A's 60 % as B's volume (it did: a reconnect to B, or the next start, then sent it to B).
        Assert.False(TrayApp.SavesStreamingVolume(c, c.Volume, cfg, pendingVolume: false));

        Connect(c, cfg, ConnectTrigger.Automatic);                 // another speaker: an automatic connect, volume null
        await Until(() => c.State == StreamState.Streaming && Members("BB").Count == 2, "B streaming");
        var b = Volumes(Members("BB"));
        Assert.NotEmpty(b);
        Assert.All(b, v => Assert.Equal(50, v));                   // B's own volume (under the 60 % cap), never 70 or 60
        Assert.True(Members("AA").All(m => m.Disposed));

        // Now B streams: its own volume is what gets saved for it.
        Assert.True(TrayApp.SavesStreamingVolume(c, c.Volume, cfg, pendingVolume: false));
        Assert.Equal(50, c.Volume);
        Assert.False(TrayApp.SavesStreamingVolume(c, c.Volume, cfg, pendingVolume: true)); // a key's debounce saves instead
    }

    [Fact]
    public void Nothing_is_saved_while_not_streaming_or_after_the_session_ended()
    {
        var c = Controller();
        var cfg = SelectingA();
        c.SetVolume(40);
        Assert.False(TrayApp.SavesStreamingVolume(c, c.Volume, cfg, pendingVolume: false)); // idle
        c.OwnSession.Began(OwnSession.KeyOf(cfg)!, DateTime.UtcNow);
        Assert.False(TrayApp.SavesStreamingVolume(c, c.Volume, cfg, pendingVolume: false)); // still not streaming
    }

    [Fact]
    public void The_speaker_picker_follows_the_speaker_a_profile_switches_to()
    {
        static AirPlayDevice Found(string name, string id, string ip) =>
            new(name, id, IPAddress.Parse(ip), 7000, "AudioAccessory6,1", new Dictionary<string, string>());
        var bedroom = Found("卧室", "AA:00:00:00:00:01", "10.0.0.1");
        var kitchen = Found("厨房", "BB:00:00:00:00:01", "10.0.0.5");
        var cfg = SelectingA();
        cfg.MultiRoomDeviceId = null;
        List<AirPlayDevice> shown = [bedroom, kitchen];
        Assert.Equal(0, HomePage.SelectedIndex(shown, cfg.DeviceId));

        // A profile for the kitchen, which the last scan listed: the picker selects it, nothing is added.
        UsageProfiles.Apply(cfg, new UsageSettings { DeviceId = "bb-00-00-00-00-01", DeviceName = "厨房" }, shown);
        var list = HomePage.WithSelected(shown, cfg);
        Assert.Equal(2, list.Count);
        Assert.Equal(1, HomePage.SelectedIndex(list, cfg.DeviceId)); // however the profile wrote its id

        // A profile for a speaker the last scan didn't list: it is added (with no address yet) and selected.
        UsageProfiles.Apply(cfg, new UsageSettings { DeviceId = "CC:00:00:00:00:01", DeviceName = "书房", Host = "10.0.0.99" }, shown);
        list = HomePage.WithSelected(shown, cfg);
        Assert.Equal(3, list.Count);
        Assert.Equal(2, HomePage.SelectedIndex(list, cfg.DeviceId));
        Assert.Equal(("书房", IPAddress.None), (list[2].Name, list[2].Address)); // never the address saved in the profile
        Assert.Equal(2, shown.Count);                              // the list it came from is left as it was

        Assert.Equal(-1, HomePage.SelectedIndex(list, null));
        Assert.Equal(list, HomePage.WithSelected(list, new AppConfig())); // nothing selected: nothing added
    }

    [Fact]
    public async Task A_profile_switching_to_a_speaker_another_device_plays_to_waits_instead_of_taking_it_over()
    {
        // Another speaker while ours plays: like picking a speaker in the list, an automatic connect.
        var trigger = TrayApp.ProfileConnectTrigger(sameSelection: false, live: true);
        Assert.Equal(ConnectTrigger.Automatic, trigger);
        Assert.Equal(TakeoverAction.Wait, TakeoverPolicy.Decide(trigger, SpeakerBusy.Busy, oursRecently: false));

        var c = Controller();
        var reader = new BusyReader("厨房");
        c.BusyReader = reader;
        c.BusyPollInterval = TimeSpan.FromMilliseconds(20);
        var cfg = SelectingA();
        cfg.LatencyMs = 150;
        Connect(c, cfg, ConnectTrigger.Automatic);
        await Until(() => c.State == StreamState.Streaming, "A streaming");

        UsageProfiles.Apply(cfg, ProfileForB(cap: 100));
        Connect(c, cfg, trigger);
        await Until(() => c.StatusText == "厨房正在被其他设备使用，空闲后自动连接", "waiting for the kitchen");
        Assert.True(c.WaitingForSpeaker);
        Assert.Empty(Members("BB"));                               // no session to B: the other device keeps playing
        Assert.True(Members("AA").All(m => m.Disposed));           // and A was let go

        reader.Busy.Clear();                                       // the other device stopped
        await Until(() => c.State == StreamState.Streaming && Members("BB").Count == 2, "B streaming once free");
    }

    private sealed class BusyReader(params string[] busy) : ISpeakerBusyReader
    {
        public readonly ConcurrentDictionary<string, bool> Busy = new(busy.Select(b => KeyValuePair.Create(b, true)));

        public Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct) =>
            Task.FromResult(BusyReading.Combine(targets.Select(t => new BusyReading.Member(t.Name,
                Busy.ContainsKey(t.Name) ? SpeakerBusy.Busy : SpeakerBusy.Free, t.Name)).ToList()));
    }
}
