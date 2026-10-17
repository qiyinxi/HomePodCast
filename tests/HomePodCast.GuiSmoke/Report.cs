using System.Net;
using System.Text;

namespace HomePodCast.GuiSmoke;

/// <summary>What the run found: failures (the job fails), warnings and notes, and the screenshots taken.</summary>
internal sealed class Report
{
    public sealed record Entry(string Where, string Text);

    public sealed record Shot(string Scenario, string File, string Caption);

    private readonly HashSet<string> _seen = [];

    /// <summary>Collects without printing (a first look that is checked again).</summary>
    public bool Silent { get; init; }

    public List<Entry> Failures { get; } = [];
    public List<Entry> Warnings { get; } = [];
    public List<Entry> Notes { get; } = [];
    public List<Shot> Shots { get; } = [];
    public List<(string Scenario, string Result)> Scenarios { get; } = [];

    /// <summary>Each message once per place (the same layout problem shows on every screenshot of a page).</summary>
    private bool First(string kind, string where, string text) => _seen.Add($"{kind}|{where}|{text}");

    public void Fail(string where, string text)
    {
        if (!First("fail", where, text)) return;
        Failures.Add(new(where, text));
        if (!Silent) Console.WriteLine($"FAIL  [{where}] {text}");
    }

    public void Warn(string where, string text)
    {
        if (!First("warn", where, text)) return;
        Warnings.Add(new(where, text));
        if (!Silent) Console.WriteLine($"warn  [{where}] {text}");
    }

    public void Note(string where, string text)
    {
        Notes.Add(new(where, text));
        Console.WriteLine($"note  [{where}] {text}");
    }

    public void Pass(string where, string text) => Console.WriteLine($"ok    [{where}] {text}");

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## GUI smoke test");
        sb.AppendLine();
        sb.AppendLine(Failures.Count == 0 ? "**Passed.**" : $"**{Failures.Count} failure(s).**");
        sb.AppendLine();
        sb.AppendLine("Download this job's screenshot artifact and open its `index.html` for the captured pages and dialogs.");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Result |");
        sb.AppendLine("| --- | --- |");
        foreach (var (scenario, result) in Scenarios) sb.AppendLine($"| {scenario} | {result} |");
        void List(string title, List<Entry> entries)
        {
            if (entries.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine($"### {title}");
            sb.AppendLine();
            foreach (var e in entries) sb.AppendLine($"- **{e.Where}**: {e.Text.Replace("|", "\\|")}");
        }
        List("Failures", Failures);
        List("Warnings", Warnings);
        List("Notes", Notes);
        return sb.ToString();
    }

    /// <summary>A contact sheet of the screenshots, by scenario, with the findings on top.</summary>
    public string Html()
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><meta charset=\"utf-8\"><title>HomePodCast GUI smoke test</title>");
        sb.AppendLine("<style>body{font:14px Segoe UI,Microsoft YaHei UI,sans-serif;margin:16px;background:#f3f3f3;color:#1b1b1b}" +
                      "h2{margin-top:32px}figure{display:inline-block;margin:8px;vertical-align:top;background:#fff;padding:8px;border:1px solid #ddd}" +
                      "img{max-width:520px;max-height:420px;display:block;cursor:zoom-in}figcaption{max-width:520px;margin-top:4px;color:#555}" +
                      ".fail{color:#b00020}.warn{color:#8a5a00}</style>");
        sb.AppendLine($"<h1>HomePodCast GUI smoke test</h1><p>{(Failures.Count == 0 ? "Passed." : $"<b class=fail>{Failures.Count} failure(s)</b>")}" +
                      $" · {Shots.Count} screenshots · {Warnings.Count} warning(s)</p>");
        if (Failures.Count > 0)
            sb.AppendLine("<ul>" + string.Concat(Failures.Select(f => $"<li class=fail><b>{E(f.Where)}</b>: {E(f.Text)}</li>")) + "</ul>");
        if (Warnings.Count > 0)
            sb.AppendLine("<details><summary>Warnings</summary><ul>" + string.Concat(Warnings.Select(w => $"<li class=warn><b>{E(w.Where)}</b>: {E(w.Text)}</li>")) + "</ul></details>");
        if (Notes.Count > 0)
            sb.AppendLine("<details><summary>Notes</summary><ul>" + string.Concat(Notes.Select(n => $"<li><b>{E(n.Where)}</b>: {E(n.Text)}</li>")) + "</ul></details>");
        foreach (var group in Shots.GroupBy(s => s.Scenario))
        {
            sb.AppendLine($"<h2>{E(group.Key)}</h2>");
            foreach (var s in group)
                sb.AppendLine($"<figure><a href=\"{E(s.File)}\"><img src=\"{E(s.File)}\" loading=lazy alt=\"{E(s.Caption)}\"></a><figcaption>{E(s.Caption)}</figcaption></figure>");
        }
        return sb.ToString();
    }
}
