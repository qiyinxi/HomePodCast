using System.Buffers.Binary;
using System.Text.Json;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public class SrpTests
{
    public record SrpCase(string ClientPrivate, string Salt, string ServerPublic, string ClientPublic, string SessionKey, string Proof);

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));
        foreach (var c in doc.RootElement.GetProperty("srp").EnumerateArray())
            yield return [c.Deserialize<SrpCase>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_srptools_byte_for_byte(SrpCase v)
    {
        var srp = new SrpClient("Pair-Setup", "3939", Convert.FromHexString(v.ClientPrivate));
        Assert.Equal(v.ClientPublic, Convert.ToHexString(srp.PublicKey).ToLowerInvariant());

        var proof = srp.ComputeProof(Convert.FromHexString(v.Salt), Convert.FromHexString(Even(v.ServerPublic)));
        Assert.Equal(v.SessionKey, Convert.ToHexString(srp.SessionKey!).ToLowerInvariant());
        Assert.Equal(v.Proof, Convert.ToHexString(proof).ToLowerInvariant());
    }

    private static string Even(string hex) => hex.Length % 2 == 0 ? hex : "0" + hex;
}

public class BPlistTests
{
    [Fact]
    public void Reads_a_plist_written_by_python_plistlib()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));
        var bytes = Convert.FromBase64String(doc.RootElement.GetProperty("bplist").GetProperty("b64").GetString()!);
        var d = BPlist.ReadDict(bytes);

        Assert.Equal("卧室", d["name"]);
        Assert.Equal("HomePod", d["ascii"]);
        Assert.Equal(7L, d["small"]);
        Assert.Equal(300L, d["u16"]);
        Assert.Equal(70000L, d["u32"]);
        Assert.Equal(1L << 40, d["big"]);
        Assert.Equal(-5L, d["neg"]);
        Assert.Equal(1.5, d["real"]);
        Assert.Equal(true, d["yes"]);
        Assert.Equal(false, d["no"]);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(), (byte[])d["data"]!);
        var list = (List<object?>)d["list"]!;
        Assert.Equal(1L, list[0]);
        Assert.Equal("two", list[1]);
        Assert.Equal(3L, ((Dictionary<string, object?>)list[2]!)["three"]);
        var stream = (Dictionary<string, object?>)((List<object?>)d["streams"]!)[0]!;
        Assert.Equal(96L, stream["type"]);
        Assert.Equal(50683L, stream["dataPort"]);
    }

    [Fact]
    public void Round_trips_everything_the_setup_requests_use()
    {
        var shk = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        var original = new Dictionary<string, object?>
        {
            ["deviceID"] = "02:48:50:43:41:53",
            ["name"] = "电脑",
            ["timingPort"] = 50123,
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["streamConnectionID"] = 4_000_000_000L,
            ["negative"] = -1L,
            ["ratio"] = 0.25,
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = 96, ["shk"] = shk, ["latencyMin"] = 4630 },
            },
        };
        // Force the 2-byte object reference path too.
        for (int i = 0; i < 300; i++) original[$"k{i}"] = i;

        var back = BPlist.ReadDict(BPlist.Write(original));

        Assert.Equal("电脑", back["name"]);
        Assert.Equal(50123L, back["timingPort"]);
        Assert.Equal(true, back["isMultiSelectAirPlay"]);
        Assert.Equal(false, back["groupContainsGroupLeader"]);
        Assert.Equal(4_000_000_000L, back["streamConnectionID"]);
        Assert.Equal(-1L, back["negative"]);
        Assert.Equal(0.25, back["ratio"]);
        Assert.Equal(299L, back["k299"]);
        var s = (Dictionary<string, object?>)((List<object?>)back["streams"]!)[0]!;
        Assert.Equal(96L, s["type"]);
        Assert.Equal(shk, (byte[])s["shk"]!);
        Assert.Equal(4630L, s["latencyMin"]);
    }
}

public class Tlv8Tests
{
    [Fact]
    public void Splits_long_values_into_255_byte_fragments_and_joins_them_back()
    {
        var big = Enumerable.Range(0, 600).Select(i => (byte)i).ToArray();
        var wire = Tlv8.Write((Tlv8.State, [3]), (Tlv8.PublicKey, big), (Tlv8.Proof, []));

        // state (3 bytes) + 3 fragments of the key (2+255, 2+255, 2+90) + empty proof (2)
        Assert.Equal(3 + 257 + 257 + 92 + 2, wire.Length);
        var back = Tlv8.Read(wire);
        Assert.Equal([3], back[Tlv8.State]);
        Assert.Equal(big, back[Tlv8.PublicKey]);
        Assert.Empty(back[Tlv8.Proof]);
    }
}

public class HapSessionTests
{
    [Fact]
    public void Frames_round_trip_across_the_1024_byte_boundary_and_arbitrary_chunking()
    {
        var k1 = Enumerable.Repeat((byte)1, 32).ToArray();
        var k2 = Enumerable.Repeat((byte)2, 32).ToArray();
        using var sender = new HapSession(k1, k2);
        using var receiver = new HapSession(k2, k1);
        var message = Enumerable.Range(0, 2500).Select(i => (byte)(i * 31)).ToArray();

        var wire = sender.Encrypt(message);
        Assert.Equal(2500 + 3 * (2 + 16), wire.Length); // 1024 + 1024 + 452

        var got = new List<byte>();
        for (int i = 0; i < wire.Length; i += 333) // deliver in odd-sized chunks
            got.AddRange(receiver.Decrypt(wire.AsSpan(i, Math.Min(333, wire.Length - i))));
        Assert.Equal(message, got.ToArray());
    }
}

public class SyncPacketTests
{
    [Fact]
    public void Carries_the_same_rtp_in_both_fields_by_default()
    {
        var p = RtpSender.BuildSyncPacket(rtpNow: 123456, ntp: 0x1122334455667788, first: true, latencyFrames: 0);
        Assert.Equal(20, p.Length);
        Assert.Equal(0x90, p[0]);
        Assert.Equal(0xD4, p[1]);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)));
        Assert.Equal(123456u, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)));
        Assert.Equal(0x1122334455667788ul, BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(8)));
        Assert.Equal(123456u, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16)));
    }

    [Fact]
    public void Legacy_mode_subtracts_latency_with_wraparound()
    {
        var p = RtpSender.BuildSyncPacket(rtpNow: 100, ntp: 0, first: false, latencyFrames: 4630);
        Assert.Equal(0x80, p[0]);
        Assert.Equal(unchecked(100u - 4630u), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)));
    }
}
