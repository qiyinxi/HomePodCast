using System.Net;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// Group membership from TXT records, and the Apple TV home theater a HomePod belongs to: the user's own scan of 2026-10-12
/// (an Apple TV 4K with the HomePod 「卧室」 as its home-theater speaker), an idle standalone HomePod, a stereo pair and a
/// multi-room group from another sender.
/// </summary>
public class HomeTheaterTests
{
    internal const string TheaterGroup = "AAD51CBC-C288-52E1-885C-4C2DD8E57F9C";

    private static AirPlayDevice Device(string name, string id, string ip, string model, string txt)
    {
        var t = txt.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.OrdinalIgnoreCase);
        t["deviceid"] = id;
        return new AirPlayDevice(name, id, IPAddress.Parse(ip), 7000, model, t);
    }

    /// <summary>The Apple TV of the scan, exactly as advertised (its room name stood where gpn shows 客厅).</summary>
    internal static AirPlayDevice AppleTv(string name = "客厅", string txt =
        "acl=0 cmv=4 features=0x4A7FDFD5,0x3C177FDE flags=0x644 gcgl=1 gid=" + TheaterGroup + " gpn=客厅 igl=1 model=AppleTV14,1 " +
        "osvers=27.0 pgm=2 pgmid=0 psgid=" + TheaterGroup + " psgsz=2 psgtp=1 srcvers=980.77.2") =>
        Device(name, "3E:B7:8E:9F:64:72", "192.168.50.78", "AppleTV14,1", txt);

    /// <summary>The HomePod of the scan, held by the Apple TV (flags 0xa0c04: bits 11 and 17).</summary>
    internal const string HeldTxt = "acl=0 c=1 cmv=4 flags=0xa0c04 gcgl=1 gid=" + TheaterGroup + " gpn=客厅 igl=0 pgcgl=1 pgid=" + TheaterGroup +
                                    " pgm=2 pgmid=1 psgid=" + TheaterGroup + " psgsz=2 psgtp=1 srcvers=980.77.2";

    internal static AirPlayDevice Bedroom(string txt = HeldTxt) => Device("卧室", "02:DC:8E:68:21:FB", "192.168.50.226", "AudioAccessory6,1", txt);

    /// <summary>The same HomePod before it became the home-theater speaker (StereoPairTests' fixture): idle, leading its own group.</summary>
    private static readonly AirPlayDevice Standalone = Device("卧室", "02:DC:8E:68:21:FB", "192.168.50.226", "AudioAccessory6,1",
        "gid=39345B9F-A1C9-417E-AF68-2D950B9CC821+64C15C81-A1AB-4D60-B4AE-7DF42C2DE08F igl=1 gcgl=1 flags=0x80404");

    private const string IPhoneGroup = "A918B6A2-BB3F-4A50-A422-0BB043C9F3BF";

    [Fact]
    public void Parses_every_group_field_of_the_home_theater_scan()
    {
        var homepod = GroupMembership.FromTxt(Bedroom().Txt);
        Assert.Equal(new GroupMembership(TheaterGroup, false, true, TheaterGroup, true, 2, 1, TheaterGroup, 2, 1, SupportsRelay: true), homepod);
        Assert.Equal(TheaterGroup, homepod.CurrentGroupId);

        var tv = GroupMembership.FromTxt(AppleTv().Txt);
        Assert.Equal(new GroupMembership(TheaterGroup, true, true, null, null, 2, 0, TheaterGroup, 2, 1, SupportsRelay: false), tv); // 0x644: no bit 11

        // The TXT record inside /info is read the same way.
        var fromInfo = GroupMembership.FromTxt(TxtRecord.FromInfo(new Dictionary<string, object?>
        {
            ["txtAirPlay"] = SpeakerInfoTests.Txt(HeldTxt.Split(' ')),
        }));
        Assert.Equal(homepod, fromInfo);

        Assert.Equal(GroupMembership.None, GroupMembership.FromTxt(new Dictionary<string, string>()));
        Assert.Null(GroupMembership.FromTxt(new Dictionary<string, string> { ["igl"] = "x", ["psgsz"] = "two" }).IsGroupLeader);
        Assert.Contains("pgmid=1 psgid=" + TheaterGroup + " psgsz=2 psgtp=1 relay=True", homepod.Describe());
    }

    [Fact]
    public void The_HomePod_is_the_home_theater_speaker_of_the_Apple_TV_that_leads_it_and_held_by_it()
    {
        var tv = AppleTv();
        var homepod = Bedroom();
        List<AirPlayDevice> scan = [homepod, tv];

        Assert.Same(tv, HomeTheater.LeaderOf(homepod, scan));
        Assert.Same(tv, HomeTheater.OwnerOf(homepod, scan));
        Assert.Same(tv, HomeTheater.HolderOf(homepod, scan));
        Assert.Null(HomeTheater.OwnerOf(tv, scan));             // an Apple TV is nobody's speaker
        Assert.Equal([homepod], HomeTheater.SpeakersOf(tv, scan));
        Assert.Equal("卧室", HomeTheater.SpeakerNames(tv, scan));
        Assert.True(PairRole.Of(homepod).SupportsRelay);
        Assert.True(PairRole.Of(homepod).SessionActive);         // the Apple TV holds the busy flag
        Assert.Single(StereoPairs.Merge(scan), d => d.Name == "卧室"); // not a stereo pair

        var lines = HomeTheater.Describe(scan).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("home theater: 客厅 [3E:B7:8E:9F:64:72] AppleTV14,1", lines[0]);
        Assert.Contains("held by it now=True", lines[1]);
    }

    [Fact]
    public void An_idle_standalone_HomePod_belongs_to_no_home_theater()
    {
        var tv = AppleTv();
        Assert.Null(HomeTheater.OwnerOf(Standalone, [Standalone, tv]));
        Assert.Null(HomeTheater.HolderOf(Standalone, [Standalone, tv]));
        Assert.Null(GroupMembership.FromTxt(Standalone.Txt).PersistentGroupId);
        Assert.Equal(GroupRole.None, GroupRole.Of("AudioAccessory6,1", Standalone.Txt, [Standalone, tv]));
        Assert.Null(HomeTheater.SpeakerNames(tv, [Standalone, tv])); // an Apple TV whose speaker isn't found
        Assert.Empty(HomeTheater.Describe([Standalone, tv]));
    }

    [Fact]
    public void A_stereo_pair_is_a_stereo_pair_and_no_home_theater()
    {
        var (links, rechts) = StereoPairTests.IdlePair();
        List<AirPlayDevice> scan = [links, rechts, AppleTv()];
        Assert.Null(HomeTheater.OwnerOf(links, scan));
        Assert.Null(HomeTheater.HolderOf(rechts, scan));
        Assert.Equal(new GroupRole(GroupRoleKind.StereoPair, "Büro 2"), GroupRole.Of("AudioAccessory1,1", links.Txt, scan));
    }

    [Fact]
    public void A_HomePod_in_another_senders_multi_room_group_is_not_held_by_the_Apple_TV()
    {
        var tv = AppleTv();
        // OwnTone #1413: while an iPhone plays to several speakers, each advertises the iPhone's group as gid / pgid.
        var plain = Bedroom($"flags=0xa0c04 gid={IPhoneGroup} pgid={IPhoneGroup} pgcgl=0 igl=0 gcgl=0");
        Assert.Null(HomeTheater.OwnerOf(plain, [plain, tv]));
        Assert.Null(HomeTheater.HolderOf(plain, [plain, tv]));

        // The home theater's own HomePod taken over by an iPhone: still the Apple TV's speaker, but held by the iPhone.
        var taken = Bedroom($"flags=0xa0c04 gid={IPhoneGroup} pgid={IPhoneGroup} igl=0 gcgl=0 psgid={TheaterGroup} psgsz=2 psgtp=1");
        Assert.Same(tv, HomeTheater.OwnerOf(taken, [taken, tv]));
        Assert.Null(HomeTheater.HolderOf(taken, [taken, tv]));
        Assert.Equal(new GroupRole(GroupRoleKind.HomeTheaterSpeaker, "客厅"), GroupRole.Of("AudioAccessory6,1", taken.Txt, [taken, tv]));
    }

    [Fact]
    public void Without_its_Apple_TV_found_or_leading_nothing_holds_the_HomePod()
    {
        var homepod = Bedroom();
        Assert.Null(HomeTheater.HolderOf(homepod, [homepod]));
        Assert.Null(HomeTheater.HolderOf(homepod, []));
        Assert.Equal(new GroupRole(GroupRoleKind.HomeTheaterSpeaker, null), GroupRole.Of("AudioAccessory6,1", homepod.Txt, [homepod]));

        // The Apple TV playing in someone else's group (igl=0): it is not the one holding the HomePod now.
        var follower = AppleTv(txt: $"flags=0x644 gid={IPhoneGroup} igl=0 psgid={TheaterGroup} psgsz=2 psgtp=1");
        Assert.Same(follower, HomeTheater.OwnerOf(homepod, [homepod, follower])); // still its home theater
        Assert.Null(HomeTheater.HolderOf(homepod, [homepod, follower]));

        // A HomePod leading its own group is never held.
        var leading = Bedroom(HeldTxt.Replace("igl=0", "igl=1"));
        Assert.Null(HomeTheater.HolderOf(leading, [leading, AppleTv()]));
    }

    [Fact]
    public void An_Apple_TVs_role_names_its_speakers()
    {
        var tv = AppleTv();
        Assert.Equal(new GroupRole(GroupRoleKind.HomeTheaterLeader, "卧室"), GroupRole.Of("AppleTV14,1", tv.Txt, [Bedroom(), tv]));
        Assert.Equal(new GroupRole(GroupRoleKind.HomeTheaterLeader, null), GroupRole.Of("AppleTV14,1", tv.Txt, [tv]));
        var plain = AppleTv(txt: "flags=0x644 gid=11111111-2222-3333-4444-555555555555 igl=1 gcgl=1");
        Assert.Equal(GroupRole.None, GroupRole.Of("AppleTV14,1", plain.Txt, [plain]));
        Assert.Equal(GroupRole.None, GroupRole.Of("AudioAccessory6,1", null, [tv]));
    }

    // ---------------------------------------------------------------- the busy check

    private static SpeakerInfoEntry Ready(Dictionary<string, object?> info) => new(SpeakerInfoState.Ready, info, DateTime.UtcNow, null);

    /// <summary>The HomePod's /info while its Apple TV holds it: statusFlags 0xa0c04, the TXT of the scan inside.</summary>
    internal static Dictionary<string, object?> HeldInfo() => new()
    {
        ["name"] = "卧室",
        ["model"] = "AudioAccessory6,1",
        ["statusFlags"] = 0xA0C04L,
        ["txtAirPlay"] = SpeakerInfoTests.Txt(HeldTxt.Split(' ')),
    };

    private static readonly BusyTarget BedroomTarget = new("02:DC:8E:68:21:FB", "卧室", IPAddress.Parse("192.168.50.226"));

    [Fact]
    public void A_busy_HomePod_its_home_theater_Apple_TV_holds_is_read_as_held_by_it()
    {
        var tv = AppleTv();
        var member = BusyReading.Of(BedroomTarget, Ready(HeldInfo()), [Bedroom(), tv]);
        Assert.Equal((SpeakerBusy.Busy, tv), (member.State, member.HomeTheater));
        Assert.Contains("held by its home-theater Apple TV", member.Detail);

        var reading = BusyReading.Combine([member]);
        Assert.Equal(BusyHolder.HomeTheater, reading.Holder);
        Assert.Same(tv, reading.HomeTheater);

        // Without the scan the Apple TV can't be recognized: another sender, as before.
        var unknown = BusyReading.Combine([BusyReading.Of(BedroomTarget, Ready(HeldInfo()), [])]);
        Assert.Equal((BusyHolder.OtherSender, null), (unknown.Holder, unknown.HomeTheater));

        // /info without its TXT record: the group fields of the scan.
        var noTxt = HeldInfo();
        noTxt.Remove("txtAirPlay");
        Assert.Same(tv, BusyReading.Of(BedroomTarget, Ready(noTxt), [Bedroom(), tv]).HomeTheater);

        // An iPhone took the HomePod: /info says the iPhone's group, whatever the old scan said.
        var iphone = HeldInfo();
        iphone["txtAirPlay"] = SpeakerInfoTests.Txt("flags=0xa0c04", $"gid={IPhoneGroup}", $"pgid={IPhoneGroup}", "igl=0", $"psgid={TheaterGroup}");
        Assert.Null(BusyReading.Of(BedroomTarget, Ready(iphone), [Bedroom(), tv]).HomeTheater);

        // Free: nobody holds it.
        var free = HeldInfo();
        free["statusFlags"] = 0x80404L;
        Assert.Equal(BusyHolder.None, BusyReading.Combine([BusyReading.Of(BedroomTarget, Ready(free), [Bedroom(), tv])]).Holder);
    }

    [Fact]
    public void A_group_counts_as_held_by_the_home_theater_only_when_every_busy_member_is_held_by_the_same_Apple_TV()
    {
        var tv = AppleTv();
        var other = AppleTv("书房") with { DeviceId = "AA:AA:AA:AA:AA:AA" };
        BusyReading.Member Held(string name, AirPlayDevice? by) => new(name, SpeakerBusy.Busy, name, by);

        Assert.Equal(BusyHolder.HomeTheater, BusyReading.Combine([Held("卧室", tv), new("客厅", SpeakerBusy.Free, "")]).Holder);
        Assert.Equal(BusyHolder.HomeTheater, BusyReading.Combine([Held("左", tv), Held("右", tv)]).Holder); // a pair as home theater
        Assert.Equal(BusyHolder.OtherSender, BusyReading.Combine([Held("卧室", tv), Held("客厅", null)]).Holder);
        Assert.Equal(BusyHolder.OtherSender, BusyReading.Combine([Held("卧室", tv), Held("书房", other)]).Holder);
        Assert.Equal(BusyHolder.None, BusyReading.Combine([new("卧室", SpeakerBusy.Unknown, "")]).Holder);
    }

    [Fact]
    public async Task The_info_busy_reader_recognizes_the_home_theater_from_the_last_scan()
    {
        var cache = new SpeakerInfoCache((_, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(HeldInfo()));
        IReadOnlyList<AirPlayDevice> scan = [];
        var reader = new InfoBusyReader(cache, () => scan);
        Assert.Equal(BusyHolder.OtherSender, (await reader.ReadAsync([BedroomTarget], CancellationToken.None)).Holder);

        scan = [Bedroom(), AppleTv()];                              // the scan found the Apple TV
        var reading = await reader.ReadAsync([BedroomTarget], CancellationToken.None);
        Assert.Equal(BusyHolder.HomeTheater, reading.Holder);
        Assert.Equal("客厅", reading.HomeTheater!.Name);
        Assert.Equal(["卧室"], reading.BusyNames);
    }

    // ---------------------------------------------------------------- what the UI shows

    [Fact]
    public void The_device_list_names_an_Apple_TVs_home_theater()
    {
        var tv = AppleTv();
        Assert.Equal("客厅（Apple TV，家庭影院：卧室）", UI.Pages.HomePage.DeviceLabel(tv, [Bedroom(), tv]));
        Assert.Equal("客厅（Apple TV 4K 第三代）", UI.Pages.HomePage.DeviceLabel(tv, [tv]));
        Assert.Equal("卧室（HomePod 第二代）", UI.Pages.HomePage.DeviceLabel(Bedroom(), [Bedroom(), tv]));
        Assert.Equal("书房", UI.Pages.HomePage.DeviceLabel(new AirPlayDevice("书房", "X", IPAddress.None, 7000, "", new Dictionary<string, string>()), []));

        // A stereo pair as the home theater's speakers (assumed: both members carry the Apple TV's psgid): named by its room.
        var (links, rechts) = StereoPairTests.IdlePair();
        AirPlayDevice Member(AirPlayDevice d) =>
            d with { Txt = new Dictionary<string, string>(d.Txt, StringComparer.OrdinalIgnoreCase) { ["psgid"] = TheaterGroup } };
        Assert.Equal("客厅（Apple TV，家庭影院：Büro 2）", UI.Pages.HomePage.DeviceLabel(tv, [Member(links), Member(rechts), tv]));
    }

    [Fact]
    public void A_first_start_prefers_a_HomePod_that_no_Apple_TV_holds_but_still_picks_one()
    {
        var tv = AppleTv();
        var held = Bedroom();
        var kitchen = Device("厨房", "BB:00:00:00:00:01", "192.168.50.20", "AudioAccessory5,1", "flags=0x80404 igl=1");
        Assert.Same(kitchen, UI.TrayApp.FirstHomePod([held, kitchen, tv], [held, kitchen, tv]));
        Assert.Same(held, UI.TrayApp.FirstHomePod([tv, held], [tv, held]));
        Assert.Null(UI.TrayApp.FirstHomePod([tv], [tv]));
    }

    [Fact]
    public void The_ask_first_dialog_names_the_Apple_TV_instead_of_an_iPhone()
    {
        Assert.Equal("卧室现在是 Apple TV「客厅」的家庭影院音箱。连接后 Apple TV 的声音会暂时断开；这台电脑断开连接后，Apple TV 下次播放时会重新连上它。",
            UI.TakeoverDialog.Message("卧室", "客厅"));
        Assert.Contains("比如 iPhone 或 Mac", UI.TakeoverDialog.Message("卧室", null));
    }

    [Fact]
    public void The_setting_says_plainly_what_a_temporary_takeover_does()
    {
        string takeOver = UI.Pages.SettingsPage.HomeTheaterModeDescription(HomeTheaterMode.TakeOver);
        Assert.Contains("Apple TV 的声音不会从这台 HomePod 出来", takeOver);
        Assert.Contains("Apple TV 下次播放时会重新连上它", takeOver);
        Assert.Contains("正在播放的声音会被打断", takeOver);
        Assert.Contains("先问你", UI.Pages.SettingsPage.HomeTheaterModeDescription(HomeTheaterMode.Ask));
    }

    [Fact]
    public void The_setting_defaults_to_a_temporary_takeover_and_survives_a_config_round_trip()
    {
        Assert.Equal(HomeTheaterMode.TakeOver, new AppConfig().HomeTheaterTakeover);
        var path = Path.Combine(Path.GetTempPath(), $"hpc-theater-{Guid.NewGuid():N}.json");
        try
        {
            new AppConfig { HomeTheaterTakeover = HomeTheaterMode.Ask }.Save(path);
            Assert.Equal(HomeTheaterMode.Ask, AppConfig.Load(path).HomeTheaterTakeover);
            File.WriteAllText(path, "{}");                          // a config from before the setting
            Assert.Equal(HomeTheaterMode.TakeOver, AppConfig.Load(path).HomeTheaterTakeover);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
