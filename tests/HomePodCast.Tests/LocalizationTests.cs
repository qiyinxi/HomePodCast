using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "HomePodCast.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException($"no src/HomePodCast.csproj above {AppContext.BaseDirectory}");
    }

    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    public static string Json(string s) => JsonSerializer.Serialize(s,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>
/// Guards the translation tables: every text passed to L.T/L.F in src must have an entry in
/// src/i18n/en.json, zh-TW.json and ja.json. Strings added on other branches show up here after a merge.
/// </summary>
public class LocalizationTests(ITestOutputHelper output)
{
    private static readonly string[] Translated = ["en", "zh-TW", "ja"];

    private static List<SourceScanner.Use> ScanSrc() => SourceScanner.Scan(
        SourceScanner.SourceFiles(Path.Combine(Repo.Root, "src"))
            .Select(f => (Repo.Relative(f), File.ReadAllText(f))));

    [Fact]
    public void Every_wrapped_string_has_a_translation_in_every_table()
    {
        var uses = ScanSrc();
        var keys = uses.Where(u => u.Key != null).GroupBy(u => u.Key!).ToDictionary(g => g.Key, g => g.First());
        Assert.True(keys.Count > 40, $"only {keys.Count} L.T/L.F texts found in src: is the scanner broken?");

        var report = new StringBuilder();
        foreach (var u in uses.Where(u => u.Problem != null))
            report.AppendLine($"{u.File}:{u.Line}: {u.Problem}");
        foreach (var lang in Translated)
        {
            var table = L.Table(lang);
            foreach (var (key, use) in keys)
                if (!table.ContainsKey(key))
                    report.AppendLine($"missing in src/i18n/{lang}.json: {Repo.Json(key)}  ({use.File}:{use.Line})");
        }

        var unused = Translated
            .SelectMany(lang => L.Table(lang).Keys.Where(k => !keys.ContainsKey(k)).Select(k => $"unused in src/i18n/{lang}.json: {Repo.Json(k)}"))
            .ToList();
        foreach (var line in unused) output.WriteLine(line);
        output.WriteLine($"{keys.Count} texts, {unused.Count} unused table entries");

        Assert.True(report.Length == 0, report + string.Join("\n", unused));
    }

    [Fact]
    public void Translations_keep_the_format_placeholders()
    {
        var bad = new List<string>();
        foreach (var lang in Translated)
        {
            foreach (var (key, text) in L.Table(lang))
            {
                if (string.IsNullOrWhiteSpace(text)) bad.Add($"{lang}: empty translation for {Repo.Json(key)}");
                var expected = Placeholders(key);
                if (!expected.SequenceEqual(Placeholders(text)))
                    bad.Add($"{lang}: placeholders differ for {Repo.Json(key)}");
                if (expected.Length == 0) continue;
                try { _ = string.Format(text, Enumerable.Repeat<object?>(0, expected.Max() + 1).ToArray()); }
                catch (FormatException) { bad.Add($"{lang}: not a valid format string: {Repo.Json(text)}"); }
            }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    private static int[] Placeholders(string s) =>
        Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{(\d+)[^}]*\}")
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();

    [Fact]
    public void Tables_are_embedded_and_translate()
    {
        foreach (var lang in Translated) Assert.NotEmpty(L.Table(lang));
        Assert.Equal("Connect", L.Table("en")["连接"]);
        Assert.Empty(L.Table(L.Source));
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hans-HK", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("ko-KR", "en")]
    public void Windows_display_language_picks_the_ui_language(string culture, string expected) =>
        Assert.Equal(expected, L.FromCulture(CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData("auto", "zh-HK", "zh-TW")]
    [InlineData("AUTO", "fr-FR", "en")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("zh-TW", "ja-JP", "zh-TW")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("zh_tw", "en-US", "zh-TW")]
    [InlineData("fr", "ja-JP", "ja")]          // unsupported value: follow Windows
    [InlineData("nonsense!", "zh-CN", "zh-CN")]
    public void Config_value_overrides_the_display_language(string? configured, string ui, string expected) =>
        Assert.Equal(expected, L.Resolve(configured, CultureInfo.GetCultureInfo(ui)));

    [Fact]
    public void Missing_entries_fall_back_to_english_then_the_source()
    {
        Assert.Equal(new[] { "ja", "en" }, L.Chain("ja"));
        Assert.Equal(new[] { "en" }, L.Chain("en"));
        Assert.Equal(new[] { "zh-TW" }, L.Chain("zh-TW"));
        Assert.Empty(L.Chain("zh-CN"));
    }
}

public class SourceScannerTests
{
    private static List<SourceScanner.Use> Scan(string code) => SourceScanner.Scan([("x.cs", code)]);

    [Fact]
    public void Finds_literals_concatenations_holes_and_consts_but_not_comments()
    {
        const string code = """"
            // L.T("comment") is ignored
            /* L.T("block comment") too */
            #region L.T("directive")
            class C
            {
                public const string Gone = "音箱结束了会话";
                string a = L.T("连接");
                string b = L.F("音源：{0}", name);
                string c = L.T("第一行\n" +
                               "第二行");
                string d = $"{L.T("HomePod 音响")} · {x:F0} {(y ? "a" : "b")}";
                string e = L.T(EventChannel.Gone);
                string f = L.T(@"C:\路径 ""引号""");
                string g = "L.T(\"inside a string\")";
                char h = '"'; string i = L.T("后面");
                string j = L.F(Gone, x);
            }
            """";
        var uses = Scan(code);
        Assert.All(uses, u => Assert.Null(u.Problem));
        Assert.Equal(
            new[] { "连接", "音源：{0}", "第一行\n第二行", "HomePod 音响", "音箱结束了会话", "C:\\路径 \"引号\"", "后面", "音箱结束了会话" },
            uses.Select(u => u.Key));
        Assert.Equal(7, uses[0].Line);
    }

    [Fact]
    public void Reports_texts_that_cannot_be_looked_up()
    {
        var uses = Scan("""
            var a = L.T($"已连接 · {name}");
            var b = L.T(text);
            var c = L.T("前缀" + name);
            """);
        Assert.Equal(3, uses.Count(u => u.Problem != null));
    }

    [Fact]
    public void Reads_raw_string_literals()
    {
        var uses = Scan("var a = L.T(\"\"\"\n    第一行\n      第二行\n    \"\"\");");
        Assert.Equal("第一行\n  第二行", Assert.Single(uses).Key);
    }
}
