using System.Net;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>Stereo-pair discovery from synthetic mDNS TXT records.</summary>
public class StereoPairTests
{
    private const string Tsid = "6A1B2C3D-0000-4000-8000-0123456789AB";

    private static AirPlayDevice Device(string name, string id, string ip, string model = "AudioAccessory6,1",
        params (string Key, string Value)[] txt)
    {
        var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["deviceid"] = id, ["model"] = model };
        foreach (var (k, v) in txt) t[k] = v;
        return new AirPlayDevice(name, id, IPAddress.Parse(ip), 7000, model, t);
    }

    // What the user's single HomePod actually advertises (2026-10, HomePod 27.0): no tsid, no gpn.
    private static readonly AirPlayDevice Standalone = Device("卧室", "02:DC:8E:68:21:FB", "192.168.50.226", txt:
        [("gid", "39345B9F-A1C9-417E-AF68-2D950B9CC821+64C15C81-A1AB-4D60-B4AE-7DF42C2DE08F"), ("igl", "1"), ("gcgl", "1"), ("flags", "0x80404")]);

    private static AirPlayDevice PairMember(string name, string id, string ip, string tsid = Tsid, string gpn = "客厅") =>
        Device(name, id, ip, txt: [("tsid", tsid), ("gpn", gpn), ("gid", "G"), ("igl", "0"), ("flags", "0x2404")]);

    [Fact]
    public void Two_homepods_with_the_same_tsid_become_one_entry_named_after_gpn()
    {
        var right = PairMember("客厅 (2)", "AA:00:00:00:00:02", "192.168.50.12", tsid: Tsid.ToLowerInvariant());
        var left = PairMember("客厅", "AA:00:00:00:00:01", "192.168.50.11");
        var list = StereoPairs.Merge([right, Standalone, left]);

        Assert.Equal(2, list.Count);
        var pair = list.Single(d => StereoPairs.IsPairId(d.DeviceId));
        Assert.Equal("客厅", pair.Name);
        Assert.Equal("pair:" + Tsid, pair.DeviceId);
        Assert.True(StereoPairs.TryGetTsid(pair.DeviceId, out var tsid));
        Assert.Equal(Tsid, tsid);
        Assert.Contains("实验性", pair.Model);
        Assert.Same(Standalone, list.Single(d => d.Name == "卧室")); // the single HomePod is untouched

        // Members come back in a stable order (by device id) whatever order mDNS answered in.
        var members = StereoPairs.Members([right, Standalone, left], tsid);
        Assert.Equal(["AA:00:00:00:00:01", "AA:00:00:00:00:02"], members.Select(m => m.DeviceId));
    }

    [Fact]
    public void A_single_homepod_without_tsid_is_left_exactly_as_it_is()
    {
        var list = StereoPairs.Merge([Standalone]);
        Assert.Same(Standalone, Assert.Single(list));
    }

    [Fact]
    public void A_pair_with_one_member_offline_is_not_merged()
    {
        var one = PairMember("客厅", "AA:00:00:00:00:01", "192.168.50.11");
        var list = StereoPairs.Merge([one, Standalone]);
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, d => StereoPairs.IsPairId(d.DeviceId));
        Assert.Single(StereoPairs.Members(list, Tsid));
    }

    [Fact]
    public void Non_homepods_and_empty_or_crowded_tsids_are_not_pairs()
    {
        // Apple TV sharing a home-theater tsid with one HomePod: not a stereo pair.
        var tv = Device("客厅 Apple TV", "BB:00:00:00:00:01", "192.168.50.20", "AppleTV14,1", ("tsid", Tsid), ("gpn", "客厅"));
        var hp = PairMember("客厅", "AA:00:00:00:00:01", "192.168.50.11");
        Assert.Equal(2, StereoPairs.Merge([tv, hp]).Count(d => !StereoPairs.IsPairId(d.DeviceId)));

        // Blank tsid on both: not a pair.
        var blank1 = PairMember("A", "AA:00:00:00:00:03", "192.168.50.13", tsid: " ");
        var blank2 = PairMember("B", "AA:00:00:00:00:04", "192.168.50.14", tsid: "");
        Assert.DoesNotContain(StereoPairs.Merge([blank1, blank2]), d => StereoPairs.IsPairId(d.DeviceId));

        // Three HomePods claiming one tsid: ambiguous, leave them alone.
        var three = Enumerable.Range(1, 3).Select(i => PairMember($"H{i}", $"AA:00:00:00:00:1{i}", $"192.168.50.3{i}")).ToList();
        Assert.Equal(3, StereoPairs.Merge(three).Count(d => !StereoPairs.IsPairId(d.DeviceId)));

        // The same speaker seen twice (two interfaces) is still one member.
        var dup = PairMember("客厅", "AA:00:00:00:00:01", "192.168.50.11");
        Assert.DoesNotContain(StereoPairs.Merge([hp, dup]), d => StereoPairs.IsPairId(d.DeviceId));
    }

    [Fact]
    public void Pair_name_falls_back_to_a_member_name_without_gpn()
    {
        var a = PairMember("书房", "AA:00:00:00:00:01", "192.168.50.11", gpn: "");
        var b = PairMember("书房 (2)", "AA:00:00:00:00:02", "192.168.50.12", gpn: "");
        Assert.Equal("书房", StereoPairs.Merge([b, a]).Single().Name);
    }

    [Fact]
    public void Flags_bits_13_and_14_are_decoded_for_the_scan_report()
    {
        var a = PairMember("客厅", "AA:00:00:00:00:01", "192.168.50.11");
        Assert.Equal(0x2404, StereoPairs.Flags(a));
        Assert.Equal(0x80404, StereoPairs.Flags(Standalone));
        var lines = StereoPairs.Describe([a, PairMember("客厅 (2)", "AA:00:00:00:00:02", "192.168.50.12")]).ToList();
        Assert.Contains("stereo pair", lines[0]);
        Assert.Contains("tightSyncLeader=True", lines[1]);
        Assert.Contains("buddyNotReachable=False", lines[1]);
        Assert.Empty(StereoPairs.Describe([Standalone]));
    }
}
