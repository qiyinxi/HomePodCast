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
/// Guards the translation tables: every src/i18n/*.json is an embedded UI language, has exactly the entries of
/// en.json (the reference), and keeps their placeholders and line breaks; every text passed to L.T/L.F in src has
/// an entry in every table. Strings added on other branches show up here after a merge.
/// </summary>
public class LocalizationTests(ITestOutputHelper output)
{
    private static readonly string I18n = Path.Combine(Repo.Root, "src", "i18n");

    /// <summary>Every language with a table (all of L.Languages except the source).</summary>
    public static readonly string[] Translated = L.Languages.Where(l => l != L.Source).ToArray();

    public static TheoryData<string> Tables => new(Translated);

    private static List<SourceScanner.Use> ScanSrc() => SourceScanner.Scan(
        SourceScanner.SourceFiles(Path.Combine(Repo.Root, "src"))
            .Select(f => (Repo.Relative(f), File.ReadAllText(f))));

    /// <summary>A table as written in the file: entries in order, duplicates kept.</summary>
    private static List<(string Key, string Text)> Entries(string lang)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(I18n, lang + ".json")));
        return doc.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString() ?? "")).ToList();
    }

    [Fact]
    public void Every_table_file_is_an_embedded_language()
    {
        var files = Directory.GetFiles(I18n, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(files, Translated.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(L.Source, L.Languages[0]);
        Assert.Contains("en", Translated);
        Assert.Contains("zh-TW", Translated);
        foreach (var lang in Translated)
            Assert.Equal(Entries(lang).Count, L.Table(lang).Count);
        output.WriteLine(string.Join(", ", L.Languages));
    }

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

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_table_has_the_entries_of_en(string lang)
    {
        var entries = Entries(lang);
        var reference = Entries("en").Select(e => e.Key).ToHashSet();
        var keys = entries.Select(e => e.Key).ToList();
        var bad = new List<string>();
        bad.AddRange(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"duplicate: {Repo.Json(g.Key)}"));
        bad.AddRange(reference.Except(keys).Select(k => $"missing: {Repo.Json(k)}"));
        bad.AddRange(keys.Except(reference).Select(k => $"not in en.json: {Repo.Json(k)}"));
        Assert.True(bad.Count == 0, $"src/i18n/{lang}.json:\n" + string.Join("\n", bad));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Translations_keep_placeholders_and_line_breaks(string lang)
    {
        var en = L.Table("en");
        var bad = new List<string>();
        foreach (var (key, text) in Entries(lang))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                bad.Add($"empty translation for {Repo.Json(key)}");
                continue;
            }
            var expected = Placeholders(key);
            if (!expected.SequenceEqual(Placeholders(text)))
                bad.Add($"placeholders differ for {Repo.Json(key)}: {Repo.Json(text)}");
            else if (expected.Length > 0)
            {
                int args = expected.Max(p => int.Parse(Regex.Match(p, @"\d+").Value)) + 1;
                try { _ = string.Format(text, Enumerable.Repeat<object?>(0, args).ToArray()); }
                catch (FormatException) { bad.Add($"not a valid format string: {Repo.Json(text)}"); }
            }
            if (key.Count(c => c == '\n') != text.Count(c => c == '\n'))
                bad.Add($"line breaks differ for {Repo.Json(key)}");
            // Texts joined to others (" · {0} dropouts") keep en's leading/trailing spaces in Latin-script languages.
            if (L.IsLatinScript(lang) && en.TryGetValue(key, out var e)
                && (char.IsWhiteSpace(e[0]) != char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(e[^1]) != char.IsWhiteSpace(text[^1])))
                bad.Add($"leading/trailing spaces differ from en for {Repo.Json(key)}: {Repo.Json(text)}");
        }
        Assert.True(bad.Count == 0, $"src/i18n/{lang}.json:\n" + string.Join("\n", bad));
    }

    /// <summary>The distinct format items, with their format strings: "{0}", "{1:F0}".</summary>
    private static string[] Placeholders(string s) =>
        Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{\d+[^}]*\}")
            .Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Tables_are_embedded_and_translate()
    {
        foreach (var lang in Translated) Assert.NotEmpty(L.Table(lang));
        Assert.Equal("Connect", L.Table("en")["连接"]);
        Assert.Empty(L.Table(L.Source));
    }

    /// <summary>Everything this project ships or is about to ship; FromCulture is tested against it directly.</summary>
    private static readonly string[] AllPlanned =
        ["zh-CN", "zh-TW", "en", "ja", "de", "fr", "es", "it", "nl", "pl", "pt", "sv", "da", "nb", "fi"];

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
    [InlineData("zh-Hant-HK", "zh-TW")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("en-SE", "en")]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("de-CH", "de")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CH", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("it-IT", "it")]
    [InlineData("nl-NL", "nl")]
    [InlineData("nl-BE", "nl")]
    [InlineData("pl-PL", "pl")]
    [InlineData("pt-PT", "pt")]
    [InlineData("pt-BR", "pt")]
    [InlineData("sv-SE", "sv")]
    [InlineData("sv-FI", "sv")]
    [InlineData("da-DK", "da")]
    [InlineData("nb-NO", "nb")]
    [InlineData("nn-NO", "nb")]
    [InlineData("no", "nb")]
    [InlineData("fi-FI", "fi")]
    [InlineData("se-NO", "en")]   // Northern Sami
    [InlineData("ko-KR", "en")]
    [InlineData("ru-RU", "en")]
    public void Windows_display_language_picks_the_ui_language(string culture, string expected)
    {
        var c = CultureInfo.GetCultureInfo(culture);
        Assert.Equal(expected, L.FromCulture(c, AllPlanned));
        // With the tables actually embedded: the same, or English while a language has no table yet.
        Assert.Equal(L.Languages.Contains(expected) ? expected : "en", L.FromCulture(c));
    }

    [Theory]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData("auto", "zh-HK", "zh-TW")]
    [InlineData("AUTO", "ko-KR", "en")]
    [InlineData("auto", "ko-KR", "en")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("zh-TW", "ja-JP", "zh-TW")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("en", "de-DE", "en")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("zh_tw", "en-US", "zh-TW")]
    [InlineData("ko", "ja-JP", "ja")]          // unsupported value: follow Windows
    [InlineData("xx", "ja-JP", "ja")]
    [InlineData("nonsense!", "zh-CN", "zh-CN")]
    public void Config_value_overrides_the_display_language(string? configured, string ui, string expected) =>
        Assert.Equal(expected, L.Resolve(configured, CultureInfo.GetCultureInfo(ui)));

    [Fact]
    public void Missing_entries_fall_back_to_english_then_the_source()
    {
        Assert.Equal(new[] { "ja", "en" }, L.Chain("ja"));
        Assert.Equal(new[] { "de", "en" }, L.Chain("de"));
        Assert.Equal(new[] { "sv", "en" }, L.Chain("sv"));
        Assert.Equal(new[] { "en" }, L.Chain("en"));
        Assert.Equal(new[] { "zh-TW" }, L.Chain("zh-TW"));
        Assert.Empty(L.Chain("zh-CN"));
        foreach (var lang in Translated) Assert.Equal(lang, L.Chain(lang)[0]);
    }

    [Fact]
    public void Every_language_has_its_own_name_and_the_picker_lists_them_all()
    {
        foreach (var lang in L.Languages) Assert.NotEqual(lang, L.NativeName(lang));
        foreach (var (lang, name) in new[]
                 {
                     ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("en", "English"), ("ja", "日本語"), ("de", "Deutsch"),
                     ("fr", "Français"), ("es", "Español"), ("it", "Italiano"), ("nl", "Nederlands"), ("pl", "Polski"),
                     ("pt", "Português"), ("sv", "Svenska"), ("da", "Dansk"), ("nb", "Norsk"), ("fi", "Suomi"),
                 })
            Assert.Equal(name, L.NativeName(lang));

        var choices = UI.LanguageMenu.Choices;
        Assert.Equal(L.Languages.Order(StringComparer.Ordinal), choices.Select(c => c.Value).Order(StringComparer.Ordinal));
        var names = choices.Select(c => c.Name).ToList();
        Assert.Equal(names.Order(StringComparer.InvariantCultureIgnoreCase), names);
        output.WriteLine(string.Join(", ", names));
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
