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

    // ---- Real pairs, as their TXT records were posted in OwnTone issues (ids and addresses made up) ----

    private const string BueroTsid = "EAFA36AA-9785-54B2-A537-D9EE2A55CF1C";

    /// <summary>OwnTone #1291, idle original HomePods on OS 15.4: gid "&lt;tsid&gt;+index+uuid", bit 13 on "Links" only.</summary>
    internal static (AirPlayDevice Links, AirPlayDevice Rechts) IdlePair()
    {
        // Rechts gets the smaller device id, so ordering by device id would put it first.
        var links = Device("Links", "BB:00:00:00:00:02", "192.168.50.21", "AudioAccessory1,1",
            ("tsid", BueroTsid), ("gpn", "Büro 2"), ("igl", "1"), ("gcgl", "1"), ("flags", "0x9a404"), ("tsm", "0"),
            ("gid", BueroTsid + "+0+6276FFFA-04E1-439E-8139-2C906B34E587"), ("osvers", "15.4"));
        var rechts = Device("Rechts", "BB:00:00:00:00:01", "192.168.50.22", "AudioAccessory1,1",
            ("tsid", BueroTsid), ("gpn", "Büro 2"), ("igl", "1"), ("gcgl", "1"), ("flags", "0x98404"), ("tsm", "0"),
            ("gid", BueroTsid + "+1+4671EEC3-7E13-4DC7-BEC3-C9805D3AB964"), ("osvers", "15.4"));
        return (links, rechts);
    }

    [Fact]
    public void The_leader_is_the_one_member_with_flags_bit_13_and_gid_index_orders_the_pair()
    {
        var (links, rechts) = IdlePair();
        var members = StereoPairs.Members([rechts, Standalone, links], BueroTsid);

        Assert.Equal(["Links", "Rechts"], members.Select(m => m.Name)); // gid index 0, 1 beats device-id order
        Assert.Same(links, StereoPairs.Leader(members));
        Assert.Equal(0, StereoPairs.GidIndex(links));
        Assert.Equal(1, StereoPairs.GidIndex(rechts));

        var role = PairRole.Of(links);
        Assert.True(role.TightSyncLeader);
        Assert.False(role.BuddyNotReachable);
        Assert.False(role.SessionActive);
        Assert.False(PairRole.Of(rechts).TightSyncLeader);

        // igl/gcgl are 1 on both members, so they can't be what tells the leader apart.
        Assert.Equal(links.Txt["igl"], rechts.Txt["igl"]);

        var entry = StereoPairs.Merge([rechts, links]).Single();
        Assert.Equal("Büro 2", entry.Name);
        Assert.Equal(links.DeviceId, entry.Txt["leader"]);
        Assert.Equal("BB:00:00:00:00:02,BB:00:00:00:00:01", entry.Txt["members"]);
    }

    [Fact]
    public void An_older_pair_with_gid_equal_to_tsid_is_ordered_by_device_id_and_still_has_a_leader()
    {
        // OwnTone #704 (HomePod OS 12): both share gid == tsid; the right one had bit 13.
        const string tsid = "6BA31C4B-5D14-57DC-9B4A-8C919D3E4068";
        var right = Device("HomePod Right", "CC:00:00:00:00:02", "192.168.2.102", "AudioAccessory1,1",
            ("tsid", tsid), ("gid", tsid), ("gpn", "Living Room"), ("igl", "1"), ("gcgl", "1"), ("flags", "0x1a404"));
        var left = Device("HomePod Left", "CC:00:00:00:00:01", "192.168.2.101", "AudioAccessory1,1",
            ("tsid", tsid), ("gid", tsid), ("gpn", "Living Room"), ("igl", "1"), ("gcgl", "1"), ("flags", "0x18404"));

        var members = StereoPairs.Members([right, left], tsid);
        Assert.Equal(["HomePod Left", "HomePod Right"], members.Select(m => m.Name)); // by device id
        Assert.Null(StereoPairs.GidIndex(left));
        Assert.Same(right, StereoPairs.Leader(members)); // the leader is not always the left one
    }

    [Fact]
    public void A_pair_that_is_playing_from_another_sender_is_still_found_by_tsid()
    {
        // OwnTone #1413: while in a sender's group both members advertise that group as gid/pgid, igl=gcgl=0,
        // and bits 11 (relay) and 17 (receiver session active).
        const string senderGroup = "A918B6A2-BB3F-4A50-A422-0BB043C9F3BF";
        AirPlayDevice Busy(string name, string id, string flags) => Device(name, id, "192.168.50.2" + id[^1], "AudioAccessory1,1",
            ("tsid", BueroTsid), ("gpn", "Büro 2"), ("gid", senderGroup), ("pgid", senderGroup), ("pgcgl", "0"),
            ("igl", "0"), ("gcgl", "0"), ("flags", flags));
        var links = Busy("Links", "BB:00:00:00:00:02", "0xbac04");
        var rechts = Busy("Rechts", "BB:00:00:00:00:01", "0xb8c04");

        var pair = StereoPairs.Merge([links, rechts]).Single();
        Assert.Equal(StereoPairs.PairId(BueroTsid), pair.DeviceId);
        Assert.Same(links, StereoPairs.Leader([links, rechts]));
        Assert.All(new[] { links, rechts }, d =>
        {
            var r = PairRole.Of(d);
            Assert.True(r.SessionActive);
            Assert.True(r.SupportsRelay);
            Assert.Null(r.GidIndex); // the sender's group id has no index
        });
    }

    [Fact]
    public void Without_exactly_one_bit_13_there_is_no_leader()
    {
        var both = new[] { PairMember("A", "AA:00:00:00:00:01", "192.168.50.11"), PairMember("B", "AA:00:00:00:00:02", "192.168.50.12") };
        Assert.Null(StereoPairs.Leader(both)); // the fixture sets 0x2404 on both

        var none = both.Select(d => d with { Txt = new Dictionary<string, string>(d.Txt) { ["flags"] = "0x18404" } }).ToArray();
        Assert.Null(StereoPairs.Leader(none));

        // A mini pair whose buddy is gone: the leader shows bit 14 as well.
        var lonely = PairMember("Keller (2)", "AA:00:00:00:00:03", "192.168.50.13") with
        {
            Txt = new Dictionary<string, string> { ["tsid"] = Tsid, ["flags"] = "0x9e404" },
        };
        Assert.True(PairRole.Of(lonely).BuddyNotReachable);
        Assert.True(PairRole.Of(lonely).TightSyncLeader);
    }

    [Theory]
    [InlineData(Tsid + "+0+6276FFFA-04E1-439E-8139-2C906B34E587", 0)]
    [InlineData(Tsid + "+1+X", 1)]
    [InlineData("A7D5EDDB-26B0-4D2E-A921-06949E8E8675+0+1D921AC6-B591-4141-87E9-D4CF73033038", null)] // prefix is not the tsid
    [InlineData(Tsid, null)]
    [InlineData(Tsid + "+x+Y", null)]
    [InlineData(Tsid + "+-1+Y", null)]
    [InlineData("", null)]
    public void Gid_index_is_read_only_from_tsid_plus_number_plus_uuid(string gid, int? expected)
    {
        var d = Device("A", "AA:00:00:00:00:01", "192.168.50.11", txt: [("tsid", Tsid), ("gid", gid)]);
        Assert.Equal(expected, StereoPairs.GidIndex(d));
    }

    [Fact]
    public void Scan_report_names_the_leader_the_order_and_every_group_key()
    {
        var (links, rechts) = IdlePair();
        var lines = StereoPairs.Describe([rechts, links]).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains("stereo pair \"Büro 2\"", lines[0]);
        Assert.Contains("leader Links", lines[0]);
        Assert.Contains("order Links, Rechts (by gid index)", lines[0]);
        var linksLine = lines.Single(l => l.Contains("Links 192.168.50.21"));
        foreach (var part in new[] { "tightSyncLeader=True", "bit17 sessionActive=False", "bit11 relay=False", "gidIndex=0", "tsm=0", "igl=1", "pgid=-" })
            Assert.Contains(part, linksLine);

        // A standalone HomePod only shows up with scan --txt.
        Assert.Empty(StereoPairs.Describe([Standalone]));
        var single = Assert.Single(StereoPairs.Describe([Standalone], allHomePods: true));
        Assert.StartsWith("no tsid: ", single);
        Assert.Contains("tightSyncLeader=False", single);

        var unknown = StereoPairs.Describe([PairMember("A", "AA:00:00:00:00:01", "192.168.50.11"),
            PairMember("B", "AA:00:00:00:00:02", "192.168.50.12")]).First();
        Assert.Contains("leader unknown", unknown);
        Assert.Contains("by device id", unknown);
    }
}
