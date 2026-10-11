using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomePodCast;

/// <summary>Only support measurements: no config, credentials, audio, speaker names or endpoint identifiers.</summary>
public sealed record DiagnosticSnapshot(DateTimeOffset CapturedAt, string Version, string Os, string Runtime,
    StreamState State, string? SpeakerModel, string? SpeakerSoftware, bool PerAppRouting, bool SelectedCaptureDevice,
    int RequestedLatencyMs, int EffectiveLatencyMs, int VideoDelayMs, int? SpeakerProcessingMs,
    double BufferTargetMs, long Dropouts, long IdleGaps, long Overflows, long? PacketsSent, long? LateWakeups,
    long? SkippedPackets, long? ResendRequests, long? ResentPackets, long? ResendMisses,
    AdapterKind Adapter, NetworkCategory NetworkCategory, bool CaptureDeviceMissing, bool NoOutputDevice,
    Net.NetworkVerdict? Network, DateTimeOffset[] AudibleGlitchMarkers);

public static class DiagnosticReports
{
    public const int MaxLogBytes = 2_000_000;
    private static readonly Regex Ip = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?<![\w:])(?=[0-9a-f:]*::)[0-9a-f:]+(?:%[\w.-]+)?(?![\w:])|(?<![\w:])(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}(?![\w:])", RegexOptions.IgnoreCase);
    private static readonly Regex Mac = new(@"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.IgnoreCase);
    private static readonly Regex Guid = new(@"\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b", RegexOptions.IgnoreCase);
    private static readonly Regex Quoted = new("\"[^\"\\r\\n]*\"");
    private static readonly Regex Paths = new(@"(?:[a-z]:[\\/]|\\\\)[^\r\n\""<>]*", RegexOptions.IgnoreCase);

    public static string Redact(string text, IEnumerable<string?> identifiers)
    {
        // Debug/protocol dumps and any credential-related line are deliberately omitted from support exports.
        var lines = text.Split('\n').Where(line => !line.Contains("DEBUG", StringComparison.Ordinal) &&
            !Regex.IsMatch(line, @"password|authorization|secret|private.?key|session.?key|pairing|token", RegexOptions.IgnoreCase));
        // Old/group speaker names and base64 calibration context keys are not known to the current config.
        // Remove those record payloads structurally, including from rotated logs.
        text = string.Join('\n', lines.Select(line =>
        {
            line = Regex.Replace(line, @"^(.*?\b(?:INFO|WARN|ERROR)\s+)(?:connected to|group|pair)\b.*$", "$1[speaker connection details redacted]");
            return Regex.Replace(line, @"(sync calibration applied: residual [-+\d]+ ms) for .*", "$1 [context redacted]");
        }));
        foreach (var id in identifiers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(s => s!.Length))
            text = text.Replace(id!, "[redacted]", StringComparison.OrdinalIgnoreCase);
        text = Paths.Replace(text, "[path]");
        text = Quoted.Replace(text, "\"[redacted]\"");
        text = Mac.Replace(text, "[device]");
        text = Guid.Replace(text, "[id]");
        return Ip.Replace(text, "[address]");
    }

    public static void Export(string destination, DiagnosticSnapshot snapshot, IEnumerable<string?> identifiers, params string[] logPaths)
    {
        // Finish in a sibling temporary file, so a failed export never leaves half a report at the chosen path.
        string temporary = destination + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                Write(archive, "summary.json", JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                Write(archive, "README.txt", "HomePodCast diagnostic report\nTimes include the local UTC offset. Audible glitch markers are user observations, not speaker acknowledgements.\nLogs are limited to the most recent 2 MB each and redact known device/network names, addresses, identifiers and paths.\nNo configuration, credentials or audio is included. Review the report before sharing it.\n");
                for (int i = 0; i < logPaths.Length; i++)
                {
                    string path = logPaths[i];
                    if (!File.Exists(path)) continue;
                    Write(archive, i == 0 ? "homepodcast.log" : $"previous-{i}.log", Redact(ReadTail(path), identifiers));
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ReadTail(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool trimmed = file.Length > MaxLogBytes;
        if (trimmed) file.Seek(-MaxLogBytes, SeekOrigin.End);
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (trimmed) reader.ReadLine(); // don't export a partial UTF-8 character or log line
        return reader.ReadToEnd();
    }

    private static void Write(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }
}
