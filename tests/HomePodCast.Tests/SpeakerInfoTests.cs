using System.Net;
using System.Text;
using System.Windows.Forms;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// The status flags, the TXT record inside /info, what 设置 → 音箱信息 shows, the selection it shows it for, and the
/// /info cache (timeouts, failures, one read at a time) with a fake reader: no network.
/// </summary>
public class SpeakerInfoTests
{
    /// <summary>TXT record bytes: each entry length-prefixed.</summary>
    internal static byte[] Txt(params string[] entries)
    {
        var ms = new MemoryStream();
        foreach (var e in entries)
        {
            var b = Encoding.UTF8.GetBytes(e);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    /// <summary>The HomePod (2nd generation) as read on 2026-10-11 while a sender streamed to it (abridged).</summary>
    internal static Dictionary<string, object?> BedroomInfo(long statusFlags = 1704964) => new()
    {
        ["name"] = "卧室",
        ["model"] = "AudioAccessory6,1",
        ["osBuildVersion"] = "24J361",
        ["sourceVersion"] = "980.77.2",
        ["macAddress"] = "ac:bc:b5:f3:64:53",
        ["deviceID"] = "02:DC:8E:68:21:FB",
        ["initialVolume"] = -25.2,
        ["statusFlags"] = statusFlags,
        ["senderAddress"] = "192.168.50.200:59922",
        ["txtAirPlay"] = Txt("acl=0", "btaddr=54:13:1D:1F:0F:72", "deviceid=02:DC:8E:68:21:FB", $"flags=0x{statusFlags:x}",
            "model=AudioAccessory6,1", "osvers=27.0", "srcvers=980.77.2"),
    };

    [Fact]
    public void Status_flags_from_TXT_and_info_agree_and_bit_17_is_the_active_session()
    {
        Assert.Equal(0x1A0404, StatusFlags.Parse("0x1a0404"));
        Assert.Equal(0x80404, StatusFlags.Parse("0x80404"));
        Assert.Equal(0x80404, StatusFlags.Parse("80404"));
        Assert.Null(StatusFlags.Parse(""));
        Assert.Null(StatusFlags.Parse(null));
        Assert.Null(StatusFlags.Parse("0xzz"));

        // Read on 2026-10-11: TXT "flags=0x1a0404" and /info statusFlags=1704964 while streaming; 0x80404 / 525316 idle.
        Assert.Equal(StatusFlags.Parse("0x1a0404"), StatusFlags.FromInfo(new Dictionary<string, object?> { ["statusFlags"] = 1704964L }));
        Assert.Equal(StatusFlags.Parse("0x80404"), StatusFlags.FromInfo(new Dictionary<string, object?> { ["statusFlags"] = 525316L }));
        Assert.True(StatusFlags.SessionActive(0x1A0404));
        Assert.False(StatusFlags.SessionActive(0x80404));
        Assert.Null(StatusFlags.FromInfo(new Dictionary<string, object?> { ["statusFlags"] = "1704964" }));
        Assert.Equal("0x1A0404 (bit 17 sessionActive=True)", StatusFlags.Describe(0x1A0404));

        // The stereo-pair code reads the same bit.
        var d = new AirPlayDevice("卧室", "02:DC", IPAddress.Loopback, 7000, "AudioAccessory6,1",
            new Dictionary<string, string> { ["flags"] = "0x1a0404" });
        Assert.True(PairRole.Of(d).SessionActive);
    }

    [Fact]
    public void Describes_whether_the_TXT_inside_info_agrees()
    {
        Assert.Equal("statusFlags 0x1A0404 (bit 17 sessionActive=True), TXT flags agree", SpeakerInfoCache.DescribeFlags(BedroomInfo()));
        var stale = BedroomInfo(0x80404);
        stale["txtAirPlay"] = Txt("flags=0x1a0404");
        Assert.Equal("statusFlags 0x80404 (bit 17 sessionActive=False), TXT flags 0x1A0404 (bit 17 sessionActive=True)",
            SpeakerInfoCache.DescribeFlags(stale));
        Assert.Equal("no statusFlags, no TXT flags", SpeakerInfoCache.DescribeFlags(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Parses_TXT_record_bytes_and_stops_at_an_entry_that_runs_past_the_end()
    {
        var txt = TxtRecord.Parse(Txt("osvers=27.0", "flags=0x80404", "novalue", "=x", "gid=a=b"));
        Assert.Equal("27.0", txt["OSVERS"]); // keys are case-insensitive, as in the scan
        Assert.Equal("0x80404", txt["flags"]);
        Assert.Equal("a=b", txt["gid"]);
        Assert.Equal(3, txt.Count);

        var truncated = Txt("a=1", "b=2")[..^1];
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" }, TxtRecord.Parse(truncated));
        Assert.Empty(TxtRecord.Parse([]));
        Assert.Null(TxtRecord.FromInfo(new Dictionary<string, object?>()));
        Assert.Equal("27.0", TxtRecord.FromInfo(BedroomInfo())!["osvers"]);
    }

    [Theory]
    [InlineData("AudioAccessory6,1", "HomePod 第二代（AudioAccessory6,1）")]
    [InlineData("AudioAccessory5,1", "HomePod mini（AudioAccessory5,1）")]
    [InlineData("AudioAccessory1,1", "HomePod（AudioAccessory1,1）")]
    [InlineData("AppleTV11,1", "Apple TV（AppleTV11,1）")]
    [InlineData("Speaker2,7", "Speaker2,7")]
    public void Models_are_named_in_plain_words(string model, string expected) =>
        Assert.Equal(expected, SpeakerDetails.ModelDescription(model));

    [Theory]
    [InlineData("27.0", "24J361", "27.0 (24J361)")]
    [InlineData("27.0", null, "27.0")]
    [InlineData(null, "24J361", "24J361")]
    [InlineData(" ", "", null)]
    [InlineData(null, null, null)]
    public void The_system_version_is_the_OS_version_with_its_build(string? version, string? build, string? expected) =>
        Assert.Equal(expected, SpeakerDetails.SystemVersionText(version, build));

    [Fact]
    public void Details_combine_info_with_its_TXT_record()
    {
        var d = SpeakerDetails.Build("?", IPAddress.Parse("192.168.50.226"), null, BedroomInfo());
        Assert.Equal("卧室", d.Name);
        Assert.Equal("HomePod 第二代（AudioAccessory6,1）", d.ModelText);
        Assert.Equal("27.0 (24J361)", d.SystemVersion);
        Assert.Equal("980.77.2", d.AirPlayVersion);
        Assert.Equal("192.168.50.226", d.Address);
        Assert.Equal("AC:BC:B5:F3:64:53", d.WifiMac);
        Assert.Equal("54:13:1D:1F:0F:72", d.BluetoothAddress);
        Assert.Equal("02:DC:8E:68:21:FB", d.DeviceId);
        Assert.Equal(0x1A0404, d.StatusFlags);
        Assert.Equal(0x1A0404, d.TxtStatusFlags);
        Assert.True(d.SessionActive);
    }

    [Fact]
    public void Before_the_first_read_the_scan_TXT_fills_in_what_it_has()
    {
        var scan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = "AudioAccessory6,1", ["osvers"] = "27.0", ["srcvers"] = "980.77.2", ["btaddr"] = "64:02:fd:71:0d:5b",
            ["deviceid"] = "02:DC:8E:68:21:FB", ["flags"] = "0x80404",
        };
        var d = SpeakerDetails.Build("卧室", IPAddress.None, scan, null);
        Assert.Equal("卧室", d.Name);
        Assert.Equal("27.0", d.SystemVersion);
        Assert.Equal("64:02:FD:71:0D:5B", d.BluetoothAddress);
        Assert.Null(d.WifiMac);           // only /info has it
        Assert.Null(d.Address);           // IPAddress.None: not known
        Assert.False(d.SessionActive);

        // /info wins over the scan: it is fresher.
        var both = SpeakerDetails.Build("卧室", null, scan, BedroomInfo());
        Assert.Equal("54:13:1D:1F:0F:72", both.BluetoothAddress);
        Assert.True(both.SessionActive);

        var nothing = SpeakerDetails.Build("卧室", null, null, null);
        Assert.Equal("卧室", nothing.Name);
        Assert.Null(nothing.ModelText);
        Assert.Null(nothing.SystemVersion);
        Assert.Null(nothing.SessionActive);
    }

    // ---------------------------------------------------------------- the selection

    private static readonly AirPlayDevice Left = Device("卧室左", "AA:00:00:00:00:01", "192.168.50.11", ("tsid", "T1"), ("gid", "T1+0+x"));
    private static readonly AirPlayDevice Right = Device("卧室右", "AA:00:00:00:00:02", "192.168.50.12", ("tsid", "T1"), ("gid", "T1+1+y"));
    private static readonly AirPlayDevice Kitchen = Device("厨房", "BB:00:00:00:00:01", "192.168.50.20", ("osvers", "26.1"));

    private static AirPlayDevice Device(string name, string id, string ip, params (string Key, string Value)[] txt) =>
        new(name, id, IPAddress.Parse(ip), 7000, "AudioAccessory6,1",
            txt.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void The_selection_is_the_speaker_the_pairs_members_or_both_multi_room_speakers()
    {
        Assert.Empty(SpeakerSelection.Of(new AppConfig(), [Kitchen]));

        // A single speaker: the address the last connection saved, the scan's TXT.
        var single = Assert.Single(SpeakerSelection.Of(new AppConfig { DeviceId = "bb0000000001", DeviceName = "?", Host = "192.168.50.99" }, [Kitchen]));
        Assert.Equal(("厨房", "192.168.50.99", "26.1"), (single.Name, single.Address?.ToString(), single.Txt?["osvers"]));
        var unseen = Assert.Single(SpeakerSelection.Of(new AppConfig { DeviceId = "CC", DeviceName = "书房" }, [Kitchen]));
        Assert.Equal("书房", unseen.Name);
        Assert.Null(unseen.Address);
        Assert.Null(unseen.Txt);

        // A stereo pair: both members from the scan, in their order.
        var pair = SpeakerSelection.Of(new AppConfig { DeviceId = "pair:T1", DeviceName = "卧室" }, [Right, Kitchen, Left]);
        Assert.Equal(["卧室左", "卧室右"], pair.Select(s => s.Name));
        Assert.Equal("192.168.50.12", pair[1].Address?.ToString());
        var missing = Assert.Single(SpeakerSelection.Of(new AppConfig { DeviceId = "pair:T1", DeviceName = "卧室" }, []));
        Assert.Equal("卧室", missing.Name);
        Assert.Null(missing.Address);

        // Multi-room: the selected speaker and the second one.
        var multi = SpeakerSelection.Of(new AppConfig
        {
            DeviceId = "BB:00:00:00:00:01", Host = "192.168.50.20", MultiRoomDeviceId = "AA:00:00:00:00:01", MultiRoomDeviceName = "卧室左",
        }, [Kitchen, Left]);
        Assert.Equal(["厨房", "卧室左"], multi.Select(s => s.Name));
        Assert.Equal("192.168.50.11", multi[1].Address?.ToString()); // no saved host: the scan's
    }

    // ---------------------------------------------------------------- the cache

    private sealed class FakeInfo
    {
        public int Reads;
        public Func<IPAddress, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> Answer =
            (_, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(BedroomInfo());

        public Task<IReadOnlyDictionary<string, object?>> Read(IPAddress address, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Answer(address, ct);
        }
    }

    private static readonly IPAddress Bedroom = IPAddress.Parse("192.168.50.226");

    [Fact]
    public async Task A_read_is_cached_by_device_id_and_announced()
    {
        var fake = new FakeInfo();
        var at = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var cache = new SpeakerInfoCache(fake.Read) { UtcNow = () => at };
        var changes = new List<string>();
        cache.Changed += key => { lock (changes) changes.Add(key); };
        Assert.Null(cache.Get("02:DC:8E:68:21:FB"));

        var entry = await cache.RefreshAsync("02:DC:8E:68:21:FB", "卧室", Bedroom);
        Assert.Equal(SpeakerInfoState.Ready, entry.State);
        Assert.Equal(at, entry.ReadUtc);
        Assert.Equal("卧室", entry.Info!["name"]);
        Assert.Same(entry, cache.Get("02dc8e6821fb"));            // the same speaker however its id is written
        lock (changes) Assert.Equal(["02DC8E6821FB", "02DC8E6821FB"], changes); // reading, then read
    }

    [Fact]
    public async Task A_speaker_that_does_not_answer_in_time_fails_and_keeps_the_last_good_reading()
    {
        var fake = new FakeInfo();
        var cache = new SpeakerInfoCache(fake.Read, TimeSpan.FromMilliseconds(100));
        var good = await cache.RefreshAsync("A", "卧室", Bedroom);

        fake.Answer = (_, _) => new TaskCompletionSource<IReadOnlyDictionary<string, object?>>().Task; // never answers, ignores ct
        var failed = await cache.RefreshAsync("A", "卧室", Bedroom).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SpeakerInfoState.Failed, failed.State);
        Assert.Equal("no answer in 0.1 s", failed.Error);
        Assert.Same(good.Info, failed.Info);                      // still shown, marked as old
        Assert.Equal(good.ReadUtc, failed.ReadUtc);

        fake.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);              // honours the timeout's cancellation
            return BedroomInfo();
        };
        Assert.Equal("no answer in 0.1 s", (await cache.RefreshAsync("A", "卧室", Bedroom).WaitAsync(TimeSpan.FromSeconds(5))).Error);

        fake.Answer = (_, _) => throw new IOException("connection refused");
        var refused = await cache.RefreshAsync("A", "卧室", Bedroom);
        Assert.Equal(("connection refused", SpeakerInfoState.Failed), (refused.Error, refused.State));
    }

    [Fact]
    public async Task Without_an_address_nothing_is_read()
    {
        var fake = new FakeInfo();
        var cache = new SpeakerInfoCache(fake.Read);
        foreach (var address in new[] { null, IPAddress.None, IPAddress.Any })
        {
            var entry = await cache.RefreshAsync("A", "卧室", address);
            Assert.Equal((SpeakerInfoState.Failed, "address unknown"), (entry.State, entry.Error));
        }
        Assert.Equal(0, fake.Reads);
    }

    [Fact]
    public async Task A_second_request_while_one_reads_gets_the_same_read()
    {
        var fake = new FakeInfo();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Answer = async (_, _) =>
        {
            await release.Task;
            return BedroomInfo();
        };
        var cache = new SpeakerInfoCache(fake.Read, TimeSpan.FromSeconds(5));
        var first = cache.RefreshAsync("A", "卧室", Bedroom);
        var second = cache.RefreshAsync("a", "卧室", Bedroom);
        Assert.Same(first, second);
        Assert.Equal(SpeakerInfoState.Reading, cache.Get("A")!.State);
        release.SetResult();
        Assert.Equal(SpeakerInfoState.Ready, (await first).State);
        Assert.Equal(1, fake.Reads);

        await cache.RefreshAsync("A", "卧室", Bedroom);           // once done, a new request reads again
        Assert.Equal(2, fake.Reads);
    }

    [Fact]
    public void The_settings_section_shows_each_speaker_and_what_could_not_be_read() => OnSta(() =>
    {
        var cache = new SpeakerInfoCache((address, _) => address.Equals(Bedroom)
            ? Task.FromResult<IReadOnlyDictionary<string, object?>>(BedroomInfo(0x80404))
            : throw new IOException("connection refused"));
        var study = IPAddress.Parse("192.168.50.99");
        List<SelectedSpeaker> speakers = [new("02:DC:8E:68:21:FB", "卧室", Bedroom, null), new("02:BB", "书房", study, null)];
        bool streaming = false;
        using var panel = new UI.Pages.SpeakerInfoPanel(cache, () => speakers, () => streaming, () => 85);

        panel.PageShown();
        Assert.Equal(2, Texts(panel).Count(t => t == "读取中…"));
        Task.WaitAll(cache.RefreshAsync("02:DC:8E:68:21:FB", "", Bedroom), cache.RefreshAsync("02:BB", "", study)); // the reads in flight
        panel.PageShown();
        var texts = Texts(panel);
        foreach (var expected in new[]
                 {
                     "卧室", "HomePod 第二代（AudioAccessory6,1）", "空闲", "27.0 (24J361)", "980.77.2", "192.168.50.226",
                     "AC:BC:B5:F3:64:53", "54:13:1D:1F:0F:72（会定期变化）", "02:DC:8E:68:21:FB",
                     "书房", "无法读取（音箱不在线？）", "192.168.50.99", "上次连接时的音箱处理时间：85 ms",
                 })
            Assert.Contains(expected, texts);
        Assert.DoesNotContain("读取中…", texts);

        streaming = true;
        panel.PageShown();
        Assert.Contains("这台电脑正在推送", Texts(panel));

        speakers = [];
        panel.PageShown();
        Assert.Contains("还没有选择音箱", Texts(panel));
        Assert.DoesNotContain("卧室", Texts(panel));
    });

    /// <summary>Every non-empty text of the panel's text blocks (hidden lines have none).</summary>
    private static List<string> Texts(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => (c is UI.Controls.TextBlock { Text.Length: > 0 } t ? [t.Text] : Array.Empty<string>()).Concat(Texts(c))).ToList();

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
