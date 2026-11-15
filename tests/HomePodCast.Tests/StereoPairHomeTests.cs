using System.Net;
using System.Reflection;
using System.Windows.Forms;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// 首页 shows a stereo pair as one speaker (2026-11-03: 「2 个输出」, a line per member and 各去处状态 read as two, and the
/// member lines moved the volume card up and down).
/// </summary>
[Collection("Layout work")]
public class StereoPairHomeTests
{
    private const string Tsid = "F3C9C800-4B95-52DB-960A-606EB8785DCE";

    private static AirPlayDevice Member(string name, string mac, string address) =>
        new(name, mac, IPAddress.Parse(address), 7000, "AudioAccessory6,1",
            new Dictionary<string, string> { ["model"] = "AudioAccessory6,1", ["deviceid"] = mac, ["tsid"] = Tsid });

    private static readonly AirPlayDevice[] Scan =
        [Member("卧室", "AA:00:00:00:00:01", "192.0.2.11"), Member("卧室 (2)", "AA:00:00:00:00:02", "192.0.2.12")];

    private static TrayApp PairApp()
    {
        var config = TestApp.Config();
        config.DeviceId = StereoPairs.PairId(Tsid);
        var app = TestApp.Create(config);
        typeof(TrayApp).GetField("<LastScan>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, Scan.ToList());
        return app;
    }

    [Fact]
    public void A_pair_is_one_speaker_but_a_pair_with_other_outputs_is_still_a_group()
    {
        var config = TestApp.Config();
        config.DeviceId = StereoPairs.PairId(Tsid);
        var pair = GroupPlan.FromConfig(config)!;
        Assert.True(HomePage.IsOneSpeaker(pair));
        Assert.Equal("立体声对 · HomePod 第二代", HomePage.PairModelText(pair, Scan));
        Assert.Equal("立体声对", HomePage.PairModelText(pair, [])); // before the scan found its members

        config.DeviceId = Scan[0].DeviceId;
        config.MultiRoomDeviceId = "AA:00:00:00:00:09";
        Assert.False(HomePage.IsOneSpeaker(GroupPlan.FromConfig(config)!));
    }

    [Fact]
    public void Home_shows_no_output_count_member_lines_or_unknown_kind_for_a_pair() => Sta.Run(() =>
    {
        var app = PairApp();
        using var page = new HomePage(app);
        page.SetDevices(StereoPairs.Merge(Scan));
        page.UpdateState();

        Assert.Equal("立体声对 · HomePod 第二代", TestApp.Field<TextBlock>(page, "_model").Text);
        var status = TestApp.Field<TextBlock>(page, "_groupStatus");
        Assert.True(status.Collapsed);
        Assert.Equal("", status.Text);
        var picker = TestApp.Field<FluentComboBox>(page, "_device");
        string entry = Assert.Single(picker.Items.Cast<string>());
        Assert.DoesNotContain("AirPlay", entry);
        Assert.Equal("场景", TestApp.Field<TextBlock>(page, "_sceneHeader").Text);
    });
}
