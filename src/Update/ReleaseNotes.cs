using System.Text;
using System.Text.RegularExpressions;

namespace HomePodCast.Update;

internal sealed record NotesRange(IReadOnlyList<Release> Releases, bool EarlierChanges);
internal sealed record NotesRun(string Text, bool Bold = false, string? Url = null);
internal sealed record NotesBlock(IReadOnlyList<NotesRun> Runs, int Heading = 0, int ListDepth = -1);

/// <summary>Release notes from the check's existing response. No rendering, requests or HTML evaluation.</summary>
internal static class ReleaseNotes
{
    internal const string ChineseBoundary = "## 简体中文";

    public static NotesRange Collect(IEnumerable<Release> feed, ReleaseVersion current, Release target, UpdateChannel channel)
    {
        var all = feed.Where(r => channel == UpdateChannel.Beta || !r.Prerelease).ToArray();
        var range = all.Where(r => r.Version > current && r.Version <= target.Version)
            .Append(target).DistinctBy(r => r.Version).OrderByDescending(r => r.Version).ToArray();
        // A stable announcement already summarizes its betas. Retain only betas of versions not yet stable.
        var stable = range.Where(r => !r.Prerelease).Select(r => Core(r.Version)).ToHashSet();
        return new(range.Where(r => !r.Prerelease || !stable.Contains(Core(r.Version))).ToArray(),
            !all.Any(r => r.Version <= current));
    }

    private static (int, int, int) Core(ReleaseVersion v) => (v.Major, v.Minor, v.Patch);

    public static string ForLanguage(string body, string language)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var boundaries = new List<int>();
        char fence = '\0';
        int fenceLength = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var code = Regex.Match(line, @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (code.Success)
            {
                var run = code.Groups[1].Value;
                if (fence == '\0') { fence = run[0]; fenceLength = run.Length; }
                else if (fence == run[0] && run.Length >= fenceLength && code.Groups[2].Value.Trim().Length == 0) fence = '\0';
            }
            else if (fence == '\0' && line == ChineseBoundary) boundaries.Add(i);
        }
        if (boundaries.Count != 1) return body;
        int at = boundaries[0];
        var en = string.Join('\n', lines[..at]).Trim();
        var zh = string.Join('\n', lines[(at + 1)..]).Trim();
        if (en.Length == 0 || zh.Length == 0) return body;
        return language is "zh-CN" or "zh-TW" ? zh : en;
    }

    /// <summary>
    /// Only HTTPS pages in this exact repository, under either of its names (<see cref="GitHubReleases.RepositoryPaths"/>).
    /// No credentials, ports, escapes or lookalike hosts.
    /// </summary>
    public static bool CanOpen(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '%')) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com"
            && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && GitHubReleases.RepositoryPaths.Any(p => uri.AbsolutePath == p || uri.AbsolutePath.StartsWith(p + "/", StringComparison.Ordinal));
    }

    /// <summary>The small Markdown subset used by announcements. Unsupported syntax stays inert text.</summary>
    public static IReadOnlyList<NotesBlock> Parse(string markdown)
    {
        var blocks = new List<NotesBlock>();
        var paragraph = new StringBuilder();
        int listDepth = -1;
        void Flush()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new(Inline(paragraph.ToString()), ListDepth: listDepth));
            paragraph.Clear();
            listDepth = -1;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            var heading = Regex.Match(line, @"^(#{1,3})\s+(.+)$");
            if (heading.Success)
            {
                Flush();
                blocks.Add(new(Inline(heading.Groups[2].Value), heading.Groups[1].Length));
            }
            else if (line.StartsWith("- "))
            {
                Flush();
                listDepth = Math.Min(3, (raw.Length - raw.TrimStart().Length) / 2);
                paragraph.Append(line[2..]);
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }
        }
        Flush();
        return blocks;
    }

    private static IReadOnlyList<NotesRun> Inline(string text, bool bold = false)
    {
        var result = new List<NotesRun>();
        int at = 0;
        // Parse links before emphasis so a bold label keeps its link. No images or HTML are interpreted.
        foreach (Match match in Regex.Matches(text, @"\[([^\]\r\n]+)\]\(([^\s()]+(?:\([^\s()]*\)[^\s()]*)*)\)|\*\*(.+?)\*\*", RegexOptions.NonBacktracking))
        {
            if (match.Index > at) result.Add(new(text[at..match.Index], bold));
            if (match.Groups[1].Success)
            {
                var url = match.Groups[2].Value;
                foreach (var run in Inline(match.Groups[1].Value, bold)) result.Add(run with { Url = CanOpen(url) ? url : null });
            }
            else result.AddRange(Inline(match.Groups[3].Value, true));
            at = match.Index + match.Length;
        }
        if (at < text.Length) result.Add(new(text[at..], bold));
        return result;
    }
}
