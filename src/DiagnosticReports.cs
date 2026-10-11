using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>Only support measurements: no config, credentials, audio, speaker names or endpoint identifiers.</summary>
public sealed record DiagnosticSnapshot(DateTimeOffset CapturedAt, string Version, string Os, string Runtime,
    StreamState State, string? SpeakerModel, string? SpeakerSoftware, bool PerAppRouting, bool SelectedCaptureDevice,
    int RequestedLatencyMs, int EffectiveLatencyMs, int VideoDelayMs, int? SpeakerProcessingMs,
    double BufferTargetMs, long Dropouts, long IdleGaps, long Overflows, long? PacketsSent, long? LateWakeups,
    long? SkippedPackets, long? ResendRequests, long? ResentPackets, long? ResendMisses,
    AdapterKind Adapter, NetworkCategory NetworkCategory, bool CaptureDeviceMissing, bool NoOutputDevice,
    Net.NetworkVerdict? Network, DateTimeOffset[] AudibleGlitchMarkers);

/// <summary>
/// The diagnostic report (a ZIP with summary.json, README.txt and the redacted logs). Redaction is best effort, in this
/// order: lines that may hold credentials and debug dumps are left out; names the log marked (<see cref="Log.Name"/>) are
/// removed whatever they are, and older unmarked log formats that name speakers have those names removed by their
/// structure; then paths, quoted text, MAC addresses, GUIDs, device ids (12 or 32 hex digits) and IP addresses; last, the
/// literal names and ids the config knows, as whole words.
/// </summary>
public static class DiagnosticReports
{
    public const int MaxLogBytes = 2_000_000;

    /// <summary>Shorter literal identifiers are not looked for (a one-letter name would hit every such word).</summary>
    public const int MinIdentifierLength = 2;

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A name marked by Log.Name; one that lost its end (a cut line) runs to the end of the line.
    private static readonly Regex Marked = new($"{Log.NameStart}[^{Log.NameEnd}]*(?:{Log.NameEnd}|$)");

    private static readonly Regex Dropped = new(@"password|authorization|secret|private.?key|session.?key|pairing|token", Ci);
    private static readonly Regex Calibration = new(@"(sync calibration applied: residual [-+\d]+ ms) for (?!the selected capture path$).*");

    // A drive letter only where it starts a word ("https://" is not a path), or a UNC path.
    private static readonly Regex Paths = new(@"(?:(?<![\p{L}\p{N}_])[a-z]:[\\/]|\\\\)[^\r\n""<>]*", Ci);
    private static readonly Regex Quoted = new("\"[^\"\\r\\n]*\"");
    private static readonly Regex Mac = new(@"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", Ci);
    private static readonly Regex Guid = new(@"\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b", Ci);

    // Device ids without separators: a MAC as 12 hex digits (AABBCCDDEEFF, the selection keys of the takeover check
    // and the speaker info), a stereo pair's tsid as 32 (with PAIR in front in a selection key).
    private static readonly Regex HexId = new(@"(?<![0-9a-z])(?:pair)?[0-9a-f]{32}(?![0-9a-z])|(?<![0-9a-z])[0-9a-f]{12}(?![0-9a-z])", Ci);

    private static readonly Regex Ip = new(
        @"(?<![\w:.])(?:[0-9a-f]{0,4}:){2,7}(?:\d{1,3}\.){3}\d{1,3}(?:%[\w.-]+)?(?![\w.])" + // IPv6 with an IPv4 inside (::ffff:192.168.1.4)
        @"|\b(?:\d{1,3}\.){3}\d{1,3}\b" +
        @"|(?<![\w:])(?=[0-9a-f:]*::)[0-9a-f:]+(?:%[\w.-]+)?(?![\w:])" +
        @"|(?<![\w:])(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}(?:%[\w.-]+)?(?![\w:])", Ci);

    // Log lines written before names were marked (an older version's log, or the rotated one) that name speakers, by their
    // format. The first that matches applies.
    private static readonly (Regex Pattern, string Replacement)[] Unmarked =
    [
        (new(@"(\bconnected to ).*( \([^()]*AirTunes/[^()]*\))$"), "$1[name]$2"),
        (new(@"(\bconnected to ).*"), "$1[name]"),
        (new(@"(\bgroup member ).*( \([^()]*\): )"), "$1[name]$2"),
        (new(@"(\bgroup member ).*"), "$1[name]"),
        (new(@"(\bgroup streaming: ).*(, rtpBase=)"), "$1[names]$2"),
        (new(@"(\bgroup streaming: ).*"), "$1[details redacted]"),
        (new(@"(\bgroup stats: ).*"), "$1[details redacted]"), // the known format is rebuilt from its numbers (GroupStats)
        (new(@"(\bgroup: ).*?( lost \(.*\); reconnecting all speakers)$"), "$1[name]$2"),
        (new(@"(\bgroup: ).*"), "$1[details redacted]"),
        (new(@"(\bgroup volume, ).*"), "$1[name]: [details redacted]"),
        (new(@"(\bpair )\S+: .*"), "$1[details redacted]"),
        (new(@"(\bspeaker info: ).*?( \S+ (?:read in \d+ ms, .*|unreadable \(.*\)))$"), "$1[name]$2"),
        (new(@"(\bspeaker info: ).*"), "$1[details redacted]"),
        (new(@"(\btakeover check \([^)]*\): ).*?((?:; free after waiting \d+ s)?: connecting|; this PC's last session may still be active there .*|; another sender's session: .*)$"),
            "$1[speakers]$2"),
        (new(@"(\btakeover check \([^)]*\): ).*"), "$1[details redacted]"),
    ];

    // group stats legs ("| <label>: sent=… rtx=…/… rtxMiss=…"): rebuilt from their numbers, so no part of a label survives.
    private static readonly Regex GroupStats = new(@"^(.*?\bgroup stats: .*?skipped=\d+) \| (.*)$");
    private static readonly Regex GroupStatsLeg = new(@": sent=(\d+) rtx=(\d+)/(\d+) rtxMiss=(\d+)");

    public static string Redact(string text, IEnumerable<string?> identifiers)
    {
        var literals = Literals(identifiers);
        var output = new StringBuilder(text.Length);
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline + 1;
            int content = end - (newline < 0 ? 0 : 1);
            if (content > start && text[content - 1] == '\r') content--;
            string line = text[start..content];
            // Debug/protocol dumps and any credential-related line are deliberately omitted from support exports.
            if (!line.Contains("DEBUG", StringComparison.Ordinal) && !Dropped.IsMatch(line))
                output.Append(RedactLine(line, literals)).Append(text, content, end - content); // keeps the line ending
            start = end;
        }
        return output.ToString();
    }

    private static string RedactLine(string line, List<Regex> literals)
    {
        if (line.IndexOf(Log.NameStart) >= 0 || line.IndexOf(Log.NameEnd) >= 0) line = Marked.Replace(line, "[name]");
        else line = RedactUnmarked(line);
        line = Calibration.Replace(line, "$1 [context redacted]");
        line = Paths.Replace(line, "[path]");
        line = Quoted.Replace(line, "\"[redacted]\"");
        line = Mac.Replace(line, "[device]");
        line = Guid.Replace(line, "[id]");
        line = HexId.Replace(line, "[id]");
        line = Ip.Replace(line, "[address]");
        // Literal names last: replaced earlier, a short one could break a structured identifier apart.
        foreach (var literal in literals) line = literal.Replace(line, "[redacted]");
        return line;
    }

    private static string RedactUnmarked(string line)
    {
        if (GroupStats.Match(line) is { Success: true } stats)
        {
            var legs = GroupStatsLeg.Matches(stats.Groups[2].Value).Select((m, i) =>
                $"#{i + 1} [name]: sent={m.Groups[1].Value} rtx={m.Groups[2].Value}/{m.Groups[3].Value} rtxMiss={m.Groups[4].Value}");
            return $"{stats.Groups[1].Value} | {string.Join(" | ", legs)}";
        }
        foreach (var (pattern, replacement) in Unmarked)
            if (pattern.IsMatch(line)) return pattern.Replace(line, replacement, 1);
        return line;
    }

    /// <summary>Known names and ids, longest first, each matched as a whole word where its script separates words.</summary>
    private static List<Regex> Literals(IEnumerable<string?> identifiers) =>
        identifiers.Select(s => s?.Trim()).OfType<string>().Where(s => s.Length >= MinIdentifierLength)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(s => s.Length)
            .Select(s => new Regex((Spaced(s[0]) ? @"(?<![\p{L}\p{N}_])" : "") + Regex.Escape(s) + (Spaced(s[^1]) ? @"(?![\p{L}\p{N}_])" : ""), Ci))
            .ToList();

    // Latin, Greek, Cyrillic and the like: a name there is a word of its own. Chinese and Japanese text runs names into
    // the words around them, so no boundary is asked for there.
    private static bool Spaced(char c) => c == '_' || (c < '⺀' && char.IsLetterOrDigit(c));

    /// <summary>A speaker id as the log may also write it: as given, without separators in upper case, and a pair's tsid.</summary>
    public static IEnumerable<string> IdForms(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) yield break;
        yield return id;
        yield return StreamController.Normalize(id).ToUpperInvariant();
        if (StereoPairs.TryGetTsid(id, out var tsid))
        {
            yield return tsid;
            yield return StreamController.Normalize(tsid).ToUpperInvariant();
        }
    }

    private static readonly JsonSerializerOptions SummaryJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Writes the report. A log that can't be read is left out (README.txt says which); anything else that fails throws, and
    /// the destination is then left as it was.
    /// </summary>
    public static void Export(string destination, DiagnosticSnapshot snapshot, IEnumerable<string?> identifiers, params string[] logPaths)
    {
        var ids = identifiers.ToList();
        // Finish in a sibling temporary file, so a failed export never leaves half a report at the chosen path.
        string temporary = destination + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                Write(archive, "summary.json", JsonSerializer.Serialize(snapshot, SummaryJson));
                var skipped = new List<string>();
                for (int i = 0; i < logPaths.Length; i++)
                {
                    string path = logPaths[i];
                    if (!File.Exists(path)) continue;
                    string log;
                    try { log = ReadTail(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add(Path.GetFileName(path));
                        continue;
                    }
                    Write(archive, i == 0 ? AppConfig.LogFileName : $"previous-{i}.log", Redact(log, ids));
                }
                Write(archive, "README.txt", Readme(skipped));
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Readme(List<string> skipped)
    {
        var text = new StringBuilder()
            .Append("HomePodCast diagnostic report\n")
            .Append("Times include the local UTC offset. Audible glitch markers are user observations, not speaker acknowledgements.\n")
            .Append("Logs are limited to the most recent 2 MB each. Redaction is best effort: speaker and device names the log marked, ")
            .Append("names in older log formats, the names and ids in the current settings, addresses, device ids and paths are replaced; ")
            .Append("debug lines and lines about credentials are left out.\n")
            .Append("No configuration, credentials or audio is included. Review the report before sharing it.\n");
        foreach (string name in skipped) text.Append($"{name} could not be read and is not included.\n");
        return text.ToString();
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
