using System.Net;
using System.Reflection;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>When a connection tries 5.1 surround (experimental since 1.4), and why not.</summary>
public sealed class SurroundPolicyTests
{
    private static AppConfig Single(bool on = true) => new() { DeviceId = "AA:BB:CC:DD:EE:FF", DeviceName = "卧室", Surround51 = on };

    private static SurroundRequest? Request(AppConfig cfg, bool hub = false, bool routed = false) =>
        SurroundPolicy.Request(cfg, hub, routed, () => null, () => null);

    [Fact]
    public void Off_by_default_and_off_asks_for_nothing()
    {
        Assert.False(new AppConfig().Surround51);
        Assert.Null(Request(Single(on: false)));
        Assert.Null(Request(Single(on: false), hub: true, routed: true));
    }

    [Fact]
    public void A_single_speaker_with_the_switch_on_tries_5_1()
    {
        var request = Request(Single());
        Assert.NotNull(request);
        Assert.Null(request.Refusal);
    }

    [Fact]
    public void Groups_and_no_speaker_never_ask()
    {
        var multiRoom = Single();
        multiRoom.MultiRoomDeviceId = "11:22:33:44:55:66";
        Assert.False(SurroundPolicy.Applies(multiRoom));
        Assert.Null(Request(multiRoom));
        var pair = Single();
        pair.DeviceId = Net.StereoPairs.IdPrefix + "6A2C9E14-0F3B-4C51-9D7E-2B8A4F1C3D5E";
        Assert.False(SurroundPolicy.Applies(pair));
        Assert.Null(Request(pair));
        Assert.False(SurroundPolicy.Applies(new AppConfig { Surround51 = true }));
        Assert.Null(Request(new AppConfig { Surround51 = true }));
    }

    [Fact]
    public void Applies_follows_the_switch_the_speaker_and_the_group()
    {
        var cfg = Single();
        Assert.True(SurroundPolicy.Applies(cfg));
        cfg.Surround51 = false;
        Assert.False(SurroundPolicy.Applies(cfg));
        cfg.Surround51 = true;
        cfg.DeviceId = null;
        Assert.False(SurroundPolicy.Applies(cfg));
    }

    [Fact]
    public void The_mixer_hub_refuses_before_per_app_routing()
    {
        Assert.Equal(L.T("多个去处同时播放时不能用"), Request(Single(), hub: true, routed: true)!.Refusal);
        Assert.Equal(L.T("多个去处同时播放时不能用"), Request(Single(), hub: true)!.Refusal);
        Assert.Equal(L.T("按程序分流开启时不能用"), Request(Single(), routed: true)!.Refusal);
    }

    [Fact]
    public void The_capture_reads_the_device_choice_and_the_block_live()
    {
        string? device = null, blocked = null;
        var request = SurroundPolicy.Request(Single(), false, false, () => device, () => blocked)!;
        device = "{0.0.0.00000000}.{output-one}";
        blocked = "routing on";
        Assert.Equal(device, request.CaptureDeviceId());
        Assert.Equal(blocked, request.Blocked());
    }

    /// <summary>
    /// The mixer restarts a destination on a toggle only when <see cref="SurroundPolicy.Reaches"/>: it must name exactly the
    /// speakers StreamController.Start runs in RunAsync, the only path that asks for 5.1, for every protocol setting.
    /// </summary>
    [Fact]
    public void Only_speakers_that_Start_runs_on_the_HomePod_path_ask_for_5_1()
    {
        var address = IPAddress.Parse("192.0.2.30");
        AirPlayDevice[] devices =
        [
            new("HomePod", "02:01", address, 7000, "AudioAccessory6,1", new Dictionary<string, string>()),
            new("Apple TV", "02:02", address, 7000, "AppleTV14,1", new Dictionary<string, string>()),
            new("AirPort", "02:03", address, 7000, "AirPort10,115", new Dictionary<string, string>()),
            new("Sonos", "02:04", address, 7000, "Speaker1,1", new Dictionary<string, string> { ["srcvers"] = "377.40.00" }),
            new("shairport", "02:05", address, 7001, "ShairportSync", new Dictionary<string, string>()) { HasAirPlay = false, RaopPort = 7001 },
            new("Mystery", "02:06", address, 7000, "Mystery1,1", new Dictionary<string, string>()),
        ];
        Assert.Equal(Enum.GetValues<ReceiverKind>().Order(), devices.Select(d => d.Kind).Order());
        var thirdParty = typeof(StreamController).GetMethod("UsesThirdPartyPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var forced in Enum.GetValues<ReceiverProtocol>())
        {
            using var controller = new StreamController();
            controller.ReceiverSettings = id =>
            {
                var preferences = ReceiverPreferences.Of(devices.Single(d => d.DeviceId == id));
                preferences.ProtocolOverride = forced;
                return preferences;
            };
            foreach (var device in devices.Cast<AirPlayDevice?>().Append(null))
            {
                bool homePodPath = !(bool)thirdParty.Invoke(controller, [device])! && !StreamController.UsesRaop(device);
                Assert.True(homePodPath == SurroundPolicy.Reaches(device), $"{device?.Kind.ToString() ?? "not found"}, {forced}");
            }
        }
    }
}
