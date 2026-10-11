using System.IO.Compression;
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
            "network: BedroomPod 192.168.1.4 AA:BB:CC:DD:EE:FF fe80::1234 12345678-1234-1234-1234-123456789abc\n" +
            "profile \"Private Wi-Fi name\" at C:\\Users\\Alice\\Desktop\\config.json\n" +
            "INFO authorization bearer TOPSECRET\nDEBUG private payload\n";
        string redacted = DiagnosticReports.Redact(raw, ["BedroomPod", "Alice"]);
        foreach (string secret in new[] { "BedroomPod", "192.168.1.4", "AA:BB", "fe80", "12345678", "Alice", "Private Wi-Fi name", "TOPSECRET", "private payload", "C:\\Users" })
            Assert.DoesNotContain(secret, redacted);
        Assert.Contains("12:34:56.789", redacted);
        Assert.Contains("fifo=16ms rtx=2/2", redacted);
    }

    [Fact]
    public void Prior_unquoted_speaker_names_and_encoded_calibration_contexts_are_removed_without_config()
    {
        string raw = "21:00:00 INFO  connected to Previous Secret Name (AudioAccessory6,1, 27.0)\n" +
            "21:00:01 INFO  group member Other Secret Name (192.168.1.3): Stereo\n" +
            "21:00:02 INFO  pair SECRETGROUP: both members, Other Secret Name\n" +
            "21:00:02 INFO  group streaming: Left Secret Name + Right Secret Name\n" +
            "21:00:02 WARN  group: Left Secret Name lost (timeout)\n" +
            "21:00:02 WARN  group volume, Right Secret Name: failed\n" +
            "21:00:02 INFO  group stats: Left Secret Name: sent=10 Right Secret Name: sent=10\n" +
            "21:00:03 INFO  sync calibration applied: residual +7 ms for U0VDUkVU.Q09OVEVYVA==.1.0\n";
        string safe = DiagnosticReports.Redact(raw, []);
        foreach (var secret in new[] { "Previous Secret", "Other Secret", "Left Secret", "Right Secret", "SECRETGROUP", "U0VDUkVU" })
            Assert.DoesNotContain(secret, safe);
        Assert.Contains("residual +7 ms", safe);
    }

    [Fact]
    public void Export_writes_only_whitelisted_summary_and_redacted_log_and_preserves_local_time_offset()
    {
        WithTemp(folder =>
        {
            string log = Path.Combine(folder, "raw.log"), destination = Path.Combine(folder, "report.zip");
            File.WriteAllText(log, "INFO connected to Secret Pod (192.168.1.9)\nINFO stats: fifo=20ms\n");
            DiagnosticReports.Export(destination, Sample(), ["Secret Pod"], log, Path.Combine(folder, "absent.log"));
            using var zip = ZipFile.OpenRead(destination);
            Assert.Equal(new[] { "README.txt", "homepodcast.log", "summary.json" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
            using var reader = new StreamReader(zip.GetEntry("homepodcast.log")!.Open());
            string output = reader.ReadToEnd();
            Assert.DoesNotContain("Secret Pod", output);
            Assert.DoesNotContain("192.168", output);
            Assert.Contains("fifo=20ms", output);
            using var summary = JsonDocument.Parse(zip.GetEntry("summary.json")!.Open());
            Assert.Equal(TimeSpan.FromHours(2), summary.RootElement.GetProperty("CapturedAt").GetDateTimeOffset().Offset);
            Assert.Single(summary.RootElement.GetProperty("AudibleGlitchMarkers").EnumerateArray());
            Assert.False(summary.RootElement.TryGetProperty("VlcPasswordProtected", out _));
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
    public void Failed_export_does_not_replace_existing_report_or_leave_temporary_archive()
    {
        WithTemp(folder =>
        {
            string destination = Path.Combine(folder, "report.zip"), log = Path.Combine(folder, "locked.log");
            File.WriteAllText(destination, "previous report");
            using var locked = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.None);
            Assert.Throws<IOException>(() => DiagnosticReports.Export(destination, Sample(), [], log));
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
