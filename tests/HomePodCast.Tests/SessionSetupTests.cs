using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>The session SETUP plist: unchanged for a single speaker, plus the shared groupUUID for group members.</summary>
public class SessionSetupTests
{
    private const string SessionUuid = "11111111-2222-3333-4444-555555555555";

    // Exactly what the single-speaker session SETUP sent before groups existed, in this order.
    private static readonly string[] SingleSpeakerKeys =
    [
        "deviceID", "macAddress", "sessionUUID", "timingPort", "timingProtocol", "isMultiSelectAirPlay",
        "groupContainsGroupLeader", "senderSupportsRelay", "statsCollectionEnabled", "name", "model", "osName",
        "osVersion", "sourceVersion",
    ];

    [Fact]
    public void A_single_speaker_session_setup_is_unchanged()
    {
        var body = AirPlayClient.SessionSetupBody(SessionUuid, 50123, null);
        Assert.Equal(SingleSpeakerKeys, body.Keys);
        Assert.Equal("02:48:50:43:41:53", body["deviceID"]);
        Assert.Equal("02:48:50:43:41:53", body["macAddress"]);
        Assert.Equal(SessionUuid, body["sessionUUID"]);
        Assert.Equal(50123, body["timingPort"]);
        Assert.Equal("NTP", body["timingProtocol"]);
        Assert.Equal(true, body["isMultiSelectAirPlay"]);
        Assert.Equal(false, body["groupContainsGroupLeader"]);
        Assert.Equal(false, body["senderSupportsRelay"]);
        Assert.Equal(false, body["statsCollectionEnabled"]);
        Assert.Equal("HomePodCast", body["model"]);
        Assert.Equal("Windows", body["osName"]);
        Assert.Equal("690.7.1", body["sourceVersion"]);
        Assert.DoesNotContain("groupUUID", body.Keys);

        // Same bytes on the wire as the plist written key by key (BPlist keeps insertion order).
        Assert.Equal(BPlist.Write(body), BPlist.Write(AirPlayClient.SessionSetupBody(SessionUuid, 50123, null)));
    }

    [Fact]
    public void Group_members_add_the_shared_group_uuid_and_keep_everything_else()
    {
        var plan = new GroupPlan(GroupKind.StereoPair, "Büro 2", "T", [], false, false, new Dictionary<string, int>());
        var extras = plan.SessionExtras(Guid.Parse("A918B6A2-BB3F-4A50-A422-0BB043C9F3BF"));
        var a = AirPlayClient.SessionSetupBody("AAAAAAAA-0000-0000-0000-000000000001", 50001, extras);
        var b = AirPlayClient.SessionSetupBody("AAAAAAAA-0000-0000-0000-000000000002", 50002, extras);

        Assert.Equal([.. SingleSpeakerKeys, "groupUUID"], a.Keys);
        Assert.Equal("A918B6A2-BB3F-4A50-A422-0BB043C9F3BF", a["groupUUID"]);
        Assert.Equal(a["groupUUID"], b["groupUUID"]);       // one group
        Assert.NotEqual(a["sessionUUID"], b["sessionUUID"]); // two sessions
        Assert.Equal(false, a["groupContainsGroupLeader"]);  // as the iOS Music app sends it
        Assert.Equal("NTP", a["timingProtocol"]);

        // Extras replace keys they name, in place.
        var replaced = AirPlayClient.SessionSetupBody(SessionUuid, 1, new Dictionary<string, object?> { ["senderSupportsRelay"] = true });
        Assert.Equal(SingleSpeakerKeys, replaced.Keys);
        Assert.Equal(true, replaced["senderSupportsRelay"]);
    }

    [Fact]
    public void Info_dump_flattens_nested_plists_into_sorted_lines()
    {
        var info = BPlist.ReadDict(BPlist.Write(new Dictionary<string, object?>
        {
            ["name"] = "Links",
            ["initialVolume"] = -20.0,
            ["audioLatencies"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = 96L, ["outputLatencyMicros"] = 400000L },
            },
            ["pk"] = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray(),
            ["supportedFormats"] = new Dictionary<string, object?>(),
            ["tightSyncUUID"] = "EAFA36AA-9785-54B2-A537-D9EE2A55CF1C",
            ["isLeader"] = true,
        }));

        var lines = InfoDump.Lines(info).ToList();
        Assert.Equal(lines.Order(StringComparer.Ordinal), lines);
        Assert.Contains("audioLatencies[0].outputLatencyMicros=400000", lines);
        Assert.Contains("audioLatencies[0].type=96", lines);
        Assert.Contains("initialVolume=-20", lines);
        Assert.Contains("isLeader=True", lines);
        Assert.Contains("supportedFormats={}", lines);
        Assert.Contains("tightSyncUUID=EAFA36AA-9785-54B2-A537-D9EE2A55CF1C", lines);
        Assert.Contains(lines, l => l.StartsWith("pk=<000102", StringComparison.Ordinal) && l.EndsWith("…> (40 bytes)", StringComparison.Ordinal));
    }
}
