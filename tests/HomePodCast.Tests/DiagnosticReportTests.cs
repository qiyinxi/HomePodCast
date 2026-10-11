using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DiagnosticReportTests
{
    private static DiagnosticSnapshot Sample() => new(DateTimeOffset.Parse("2026-10-11T22:30:00+02:00"),
        "0.2.2", "Windows", ".NET 10", StreamState.Streaming, "AudioAccessory6,1", "27.0", true, true,
        120, 120, 156, 85, 20, 1, 2, 0, 50, 0, 0, 2, 2, 0, AdapterKind.Ethernet, NetworkCategory.Private,
        false, false, new NetworkVerdict(1, 0, false, NetworkHop.Speaker, 30, null, 0),
        [DateTimeOffset.Parse("2026-10-11T22:29:00+02:00")]);

    [Fact]
    public void Redaction_removes_identifiers_paths_addresses_and_credential_lines_but_keeps_measurements()
    {
        var raw = "12:34:56.789 INFO stats: fifo=16ms rtx=2/2\n" +
            "network: BedroomPod 192.168.1.4 AA:BB:CC:DD:EE:FF fe80::1234 12345678-1234-1234-1234-123456789abc ::ffff:192.168.1.7\n" +
            "profile \"Private Wi-Fi name\" at C:\\Users\\Alice\\Desktop\\config.json\n" +
            "12:34:57.000 INFO  update: manual check (running 0.2.2, portable): GET https://api.github.com/repos/qiyinxi/HomePodCast/releases\n" +
            "INFO authorization bearer TOPSECRET\nDEBUG private payload\n";
        string redacted = DiagnosticReports.Redact(raw, ["BedroomPod", "Alice"]);
        foreach (string secret in new[] { "BedroomPod", "192.168.1.4", "AA:BB", "fe80", "12345678", "Alice", "Private Wi-Fi name", "TOPSECRET",
                     "private payload", "C:\\Users", "ffff", "168.1.7", "1.7" })
            Assert.DoesNotContain(secret, redacted);
        Assert.Contains("12:34:56.789", redacted);
        Assert.Contains("fifo=16ms rtx=2/2", redacted);
        Assert.Contains("GET https://api.github.com/repos/qiyinxi/HomePodCast/releases", redacted); // a URL is no path
    }

    [Fact]
    public void Redaction_keeps_every_line_ending()
    {
        string raw = $"a 192.168.1.4\r\nb\nc {Log.Name("Kitchen")}\r\n\r\nd";
        Assert.Equal("a [address]\r\nb\nc [name]\r\n\r\nd", DiagnosticReports.Redact(raw, []));
    }

    [Fact]
    public void Known_names_are_looked_for_last_as_whole_words_and_never_break_a_structured_identifier()
    {
        string raw = "21:00:00.000 INFO  speaker AA:BB:CC:DD:EE:FA at 192.168.1.4: a Den in the den, Golden, 卧室左, HomePodCast, HomePod down to 30%\n";
        string safe = DiagnosticReports.Redact(raw, ["A", "a", "Den", "卧室", "HomePod"]);
        Assert.Contains("speaker [device] at [address]:", safe); // the MAC goes as a whole: no byte of it is left
        Assert.Contains("speaker", safe);                         // a one-letter name is not looked for at all
        Assert.Contains("Golden", safe);                          // "Den" only as a word of its own
        Assert.Contains("a [redacted] in the [redacted],", safe);
        Assert.DoesNotContain("卧室", safe);                      // Chinese has no word boundaries: found inside 卧室左 too
        Assert.Contains("HomePodCast", safe);                     // a speaker called "HomePod" is not cut out of the app's name
        Assert.Contains("[redacted] down to 30%", safe);
    }

    /// <summary>
    /// The formats the app writes now (copied from the call sites, built with their own helpers where they have one):
    /// every name is marked, so it goes whatever it is, and what support needs stays.
    /// </summary>
    [Fact]
    public async Task Marked_names_go_from_every_format_that_names_a_speaker_and_the_numbers_stay()
    {
        var written = new ConcurrentQueue<string>();
        void Capture(string line) => written.Enqueue(line);
        var bedroom = IPAddress.Parse("192.168.50.226");
        Log.Written += Capture;
        try
        {
            // SpeakerInfoCache writes its own lines: one read, one unreadable.
            var cache = new SpeakerInfoCache((address, _) => address.Equals(bedroom)
                ? Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["name"] = "Qz Bedroom", ["statusFlags"] = 0x1A0404L })
                : Task.FromException<IReadOnlyDictionary<string, object?>>(new IOException("refused")));
            await cache.RefreshAsync("02:DC:8E:68:21:FB", "Qz Bedroom", bedroom);
            await cache.RefreshAsync("F0:00:00:00:00:01", "客厅 (2) Qz", IPAddress.Parse("192.168.50.12"));
        }
        finally
        {
            Log.Written -= Capture;
        }
        var infoLines = written.Where(l => l.Contains("speaker info:") && l.Contains("Qz")).ToList();
        Assert.Equal(2, infoLines.Count);

        var busy = BusyReading.Combine([BusyReading.Of(new BusyTarget("02:DC:8E:68:21:FB", "Qz fallback", bedroom),
            new SpeakerInfoEntry(SpeakerInfoState.Ready, new Dictionary<string, object?> { ["name"] = "Qz's HomePod", ["statusFlags"] = 0x1A0404L },
                DateTime.UtcNow, null))]);
        string key = OwnSession.KeyOf(new AppConfig { DeviceId = "02:DC:8E:68:21:FB", MultiRoomDeviceId = "F0:00:00:00:00:01" })!;
        string pairKey = OwnSession.KeyOf("pair:6A1B2C3D-0000-4000-8000-0123456789AB");
        static MemberSetup Setup(string label) => new(label, _ => Task.FromException<IGroupMember>(new InvalidOperationException()));
        var left = Setup("Qz Left 192.168.50.11");
        var right = Setup("Right | Qz ‹odd› 192.168.50.12");
        string raw = string.Join("\n",
            // TrayApp.Takeover.InitTakeover
            $"21:00:00.000 INFO  last session: {key}, last alive 2026-10-11 20:59:00Z",
            $"21:00:00.000 INFO  last session: {pairKey}, ended by us 2026-10-11 20:59:00Z",
            // StreamController.Takeover.WaitUntilFreeAsync, TrayApp.Takeover.ConnectManual
            $"21:00:01.000 INFO  takeover check (Automatic): {busy.Detail}; another sender's session: waiting until the speaker is free (reading every 10 s)",
            $"21:00:02.000 INFO  takeover check (click): {busy.Detail}; another sender's session: asking",
            // AirPlayClient.NegotiateAsync, AirPlayClient.PrepareAsync
            $"21:00:03.000 INFO  connected to {Log.Name("Qz's HomePod")} (AudioAccessory6,1, AirTunes/870.14.1, OS 22A3354)",
            $"21:00:03.000 INFO  group member {Log.Name("Qz Left")} (192.168.50.11): LeftOnly, /info keys: name,model,statusFlags",
            // StreamController.Group.ResolveGroupAsync, GroupPlan.PairTargets
            $"21:00:03.500 INFO  pair 6A1B2C3D-0000-4000-8000-0123456789AB: both members, {Log.Name("Qz Left")} 192.168.50.11 (leader), {Log.Name("Right | Qz")} 192.168.50.12",
            // SpeakerGroup.ConnectAsync, LogStats, OnMemberLost
            $"21:00:04.000 INFO  group streaming: {SpeakerGroup.MemberForLog(left, 0)} [LeftOnly] + {SpeakerGroup.MemberForLog(right, 1)} [RightOnly], rtpBase=12345 latency=5292 frames",
            "21:00:05.000 INFO  group stats: fifo=18ms underruns=3 overflows=0 sent=6000 late=2 maxLate=4.1ms skipped=0 | " +
                $"{SpeakerGroup.MemberForLog(left, 0)}: sent=3000 rtx=4/5 rtxMiss=1 | {SpeakerGroup.MemberForLog(right, 1)}: sent=3000 rtx=0/0 rtxMiss=0",
            $"21:00:06.000 WARN  group: {SpeakerGroup.MemberForLog(right, 1)} lost (the speaker stopped answering); reconnecting all speakers",
            // GroupRunner.RunAsync
            $"21:00:07.000 WARN  group failed: {GroupRunner.DescribeForLog(new GroupMemberException(right.Label, new AirPlayException("RECORD failed: RTSP/1.0 500")))}",
            $"21:00:07.500 WARN  group failed: {GroupRunner.DescribeForLog(new AirPlayException("x") { LogMessage = $"stereo pair {Log.Name("Qz Pair")}: both speakers must be online (found 1)" })}")
            + "\n" + string.Join("\n", infoLines) + "\n";

        string safe = DiagnosticReports.Redact(raw, []);
        foreach (var gone in new[] { "Qz", "odd", "02DC8E6821FB", "F00000000001", "6A1B2C3D", "0123456789AB", "192.168", Log.NameStart.ToString(), Log.NameEnd.ToString() })
            Assert.DoesNotContain(gone, safe);
        Assert.Contains("last session: [id]+[id], last alive 2026-10-11 20:59:00Z", safe);
        Assert.Contains("last session: [id], ended by us", safe);
        Assert.Contains("takeover check (Automatic): [name] [address] statusFlags 0x1A0404 (bit 17 sessionActive=True)", safe);
        Assert.Contains("another sender's session: waiting until the speaker is free (reading every 10 s)", safe);
        Assert.Contains("connected to [name] (AudioAccessory6,1, AirTunes/870.14.1, OS 22A3354)", safe);
        Assert.Contains("group streaming: #1 [name] [LeftOnly] + #2 [name] [RightOnly], rtpBase=12345", safe);
        Assert.Contains("group stats: fifo=18ms underruns=3 overflows=0 sent=6000 late=2 maxLate=4.1ms skipped=0 | " +
                        "#1 [name]: sent=3000 rtx=4/5 rtxMiss=1 | #2 [name]: sent=3000 rtx=0/0 rtxMiss=0", safe);
        Assert.Contains("group: #2 [name] lost (the speaker stopped answering)", safe);
        Assert.Contains("group failed: [name]: RECORD failed: RTSP/1.0 500", safe);
        Assert.Contains("group failed: stereo pair [name]: both speakers must be online (found 1)", safe);
        Assert.Contains("speaker info: [name] [address] read in", safe);
        Assert.Contains("speaker info: [name] [address] unreadable (refused)", safe);
    }

    /// <summary>Lines an older version wrote, before names were marked (still in an updated user's log, or the rotated one).</summary>
    [Fact]
    public void Older_unmarked_formats_lose_their_names_by_their_structure_and_keep_the_numbers()
    {
        string raw = string.Join("\n",
            "21:00:00.000 INFO  connected to Previous Qz Name (AudioAccessory6,1, AirTunes/870.14.1, OS 22A3354)",
            "21:00:01.000 INFO  group member Other Qz Name (2) (192.168.1.3): Stereo, /info keys: name,model",
            "21:00:02.000 INFO  pair 6A1B2C3D-0000-4000-8000-0123456789AB: both members, Other Qz Name 192.168.1.3 (leader), Second Qz 192.168.1.4",
            "21:00:02.000 INFO  group streaming: Left Qz Name 192.168.1.3 [LeftOnly] + Right Qz Name 192.168.1.4 [RightOnly], rtpBase=1 latency=5292 frames",
            "21:00:02.000 WARN  group: Left Qz Name 192.168.1.3 lost (timeout); reconnecting all speakers",
            "21:00:02.000 WARN  group: Right Qz Name 192.168.1.4：RECORD failed",
            "21:00:02.000 WARN  group volume, Right Qz Name: failed",
            "21:00:02.000 INFO  group stats: fifo=18ms underruns=7 overflows=0 sent=10 late=0 maxLate=1.0ms skipped=0 | " +
                "Left | Qz Name 192.168.1.3: sent=10 rtx=1/2 rtxMiss=0 | Right Qz Name 192.168.1.4: sent=10 rtx=0/0 rtxMiss=0",
            "21:00:03.000 INFO  speaker info: Qz Kitchen 192.168.1.5 read in 12 ms, statusFlags 0x80404 (bit 17 sessionActive=False), TXT flags agree",
            "21:00:03.000 INFO  speaker info: Qz Kitchen - unreadable (address unknown)",
            "21:00:04.000 INFO  takeover check (Automatic): Qz Kitchen 192.168.1.5 statusFlags 0x1A0404 (bit 17 sessionActive=True), TXT flags agree → busy; " +
                "another sender's session: waiting until the speaker is free (reading every 10 s)",
            "21:00:04.000 INFO  takeover check (Manual): Qz Kitchen 192.168.1.5 statusFlags 0x80404 (bit 17 sessionActive=False), TXT flags agree → free: connecting",
            "21:00:05.000 INFO  sync calibration applied: residual +7 ms for U0VDUkVU.Q09OVEVYVA==.1.0") + "\n";
        string safe = DiagnosticReports.Redact(raw, []);
        foreach (var gone in new[] { "Qz", "U0VDUkVU", "192.168" })
            Assert.DoesNotContain(gone, safe);
        Assert.Contains("connected to [name] (AudioAccessory6,1, AirTunes/870.14.1, OS 22A3354)", safe);
        Assert.Contains("group member [name] ([address]): Stereo", safe);
        Assert.Contains("group stats: fifo=18ms underruns=7 overflows=0 sent=10 late=0 maxLate=1.0ms skipped=0 | " +
                        "#1 [name]: sent=10 rtx=1/2 rtxMiss=0 | #2 [name]: sent=10 rtx=0/0 rtxMiss=0", safe);
        Assert.Contains("group: [name] lost (timeout); reconnecting all speakers", safe);
        Assert.Contains("speaker info: [name] [address] read in 12 ms, statusFlags 0x80404", safe);
        Assert.Contains("speaker info: [name] - unreadable (address unknown)", safe);
        Assert.Contains("takeover check (Automatic): [speakers]; another sender's session: waiting until the speaker is free", safe);
        Assert.Contains("takeover check (Manual): [speakers]: connecting", safe);
        Assert.Contains("residual +7 ms [context redacted]", safe);
    }

    [Fact]
    public void Id_forms_cover_how_the_log_writes_a_device_or_pair_id()
    {
        Assert.Equal(new[] { "02:DC:8E:68:21:FB", "02DC8E6821FB" }, DiagnosticReports.IdForms("02:DC:8E:68:21:FB"));
        Assert.Equal(new[]
            {
                "pair:6a1b2c3d-0000-4000-8000-0123456789ab", "PAIR6A1B2C3D0000400080000123456789AB",
                "6a1b2c3d-0000-4000-8000-0123456789ab", "6A1B2C3D0000400080000123456789AB",
            },
            DiagnosticReports.IdForms("pair:6a1b2c3d-0000-4000-8000-0123456789ab"));
        Assert.Empty(DiagnosticReports.IdForms(null));
    }

    [Fact]
    public void Export_writes_only_whitelisted_summary_and_redacted_log_and_preserves_local_time_offset()
    {
        WithTemp(folder =>
        {
            string log = Path.Combine(folder, "raw.log"), destination = Path.Combine(folder, "report.zip");
            File.WriteAllText(log, "INFO connected to Kitchen Pod (192.168.1.9)\nINFO stats: fifo=20ms\n");
            DiagnosticReports.Export(destination, Sample(), ["Kitchen Pod"], log, Path.Combine(folder, "absent.log"));
            using var zip = ZipFile.OpenRead(destination);
            Assert.Equal(new[] { "README.txt", "homepodcast.log", "summary.json" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
            using var reader = new StreamReader(zip.GetEntry("homepodcast.log")!.Open());
            string output = reader.ReadToEnd();
            Assert.DoesNotContain("Kitchen Pod", output);
            Assert.DoesNotContain("192.168", output);
            Assert.Contains("fifo=20ms", output);
            using var summary = JsonDocument.Parse(zip.GetEntry("summary.json")!.Open());
            Assert.Equal(TimeSpan.FromHours(2), summary.RootElement.GetProperty("CapturedAt").GetDateTimeOffset().Offset);
            Assert.Single(summary.RootElement.GetProperty("AudibleGlitchMarkers").EnumerateArray());
            Assert.False(summary.RootElement.TryGetProperty("VlcPasswordProtected", out _));
            // Enums by name: readable without the source at hand.
            Assert.Equal("Streaming", summary.RootElement.GetProperty("State").GetString());
            Assert.Equal("Ethernet", summary.RootElement.GetProperty("Adapter").GetString());
            Assert.Equal("Speaker", summary.RootElement.GetProperty("Network").GetProperty("Hop").GetString());
        });
    }

    [Fact]
    public void Large_logs_are_bounded_and_active_writers_can_still_be_read()
    {
        WithTemp(folder =>
        {
            string log = Path.Combine(folder, "raw.log"), destination = Path.Combine(folder, "report.zip");
            using var active = new StreamWriter(log) { AutoFlush = true };
            active.Write("earlier-secret\n" + new string('x', DiagnosticReports.MaxLogBytes) + "\nINFO stats: fifo=16ms\n");
            DiagnosticReports.Export(destination, Sample(), [], log);
            using var zip = ZipFile.OpenRead(destination);
            var entry = zip.GetEntry("homepodcast.log")!;
            Assert.True(entry.Length <= DiagnosticReports.MaxLogBytes);
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("earlier-secret", text);
            Assert.Contains("fifo=16ms", text);
        });
    }

    [Fact]
    public void A_log_that_cannot_be_read_is_left_out_and_the_rest_is_exported()
    {
        WithTemp(folder =>
        {
            string log = Path.Combine(folder, "homepodcast.log"), old = log + ".old", destination = Path.Combine(folder, "report.zip");
            File.WriteAllText(log, "INFO stats: fifo=16ms\n");
            using var locked = new FileStream(old, FileMode.Create, FileAccess.Write, FileShare.None);
            DiagnosticReports.Export(destination, Sample(), [], log, old);
            using var zip = ZipFile.OpenRead(destination);
            Assert.Equal(new[] { "README.txt", "homepodcast.log", "summary.json" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
            using var readme = new StreamReader(zip.GetEntry("README.txt")!.Open());
            Assert.Contains("homepodcast.log.old could not be read", readme.ReadToEnd());
        });
    }

    [Fact]
    public void Failed_export_does_not_replace_existing_report_or_leave_temporary_archive()
    {
        WithTemp(folder =>
        {
            string destination = Path.Combine(folder, "report.zip"), log = Path.Combine(folder, "raw.log");
            File.WriteAllText(destination, "previous report");
            File.WriteAllText(log, "INFO stats: fifo=16ms\n");
            using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None)) // the report can't be replaced
                Assert.NotNull(Record.Exception(() => DiagnosticReports.Export(destination, Sample(), [], log)));
            Assert.Equal("previous report", File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        });
    }

    private static void WithTemp(Action<string> run)
    {
        string folder = Path.Combine(Path.GetTempPath(), "HomePodCast-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { run(folder); }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
