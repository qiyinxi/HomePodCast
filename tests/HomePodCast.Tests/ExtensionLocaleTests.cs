using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomePodCast.Tests;

/// <summary>The browser extension's chrome.i18n tables (extension/_locales/*/messages.json).</summary>
public class ExtensionLocaleTests
{
    private static readonly string Ext = Path.Combine(Repo.Root, "extension");
    private static readonly string[] Locales = ["en", "zh_CN", "zh_TW", "ja"];

    private static Dictionary<string, JsonElement> Messages(string locale) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(Ext, "_locales", locale, "messages.json")))!;

    private static string Text(JsonElement m) => m.GetProperty("message").GetString()!;

    /// <summary>$NAME$ references in a message, and the $n substitutions its placeholders map them to.</summary>
    private static (string[] Names, string[] Contents) Placeholders(JsonElement m)
    {
        var names = Regex.Matches(Text(m), @"\$([A-Za-z0-9_@]+)\$").Select(x => x.Groups[1].Value.ToLowerInvariant()).Distinct().Order().ToArray();
        var defined = m.TryGetProperty("placeholders", out var p)
            ? p.EnumerateObject().ToDictionary(x => x.Name.ToLowerInvariant(), x => x.Value.GetProperty("content").GetString()!)
            : [];
        return (names, names.Select(n => defined.GetValueOrDefault(n, "<undefined>")).ToArray());
    }

    [Fact]
    public void Every_locale_has_the_same_messages_and_placeholders()
    {
        var en = Messages("en");
        var bad = new List<string>();
        foreach (var locale in Locales)
        {
            var messages = Messages(locale);
            bad.AddRange(en.Keys.Except(messages.Keys).Select(k => $"{locale}: missing {k}"));
            bad.AddRange(messages.Keys.Except(en.Keys).Select(k => $"{locale}: {k} is not in en"));
            foreach (var (key, m) in messages)
            {
                if (string.IsNullOrWhiteSpace(Text(m))) bad.Add($"{locale}: {key} is empty");
                if (!Regex.IsMatch(key, "^[A-Za-z0-9_]+$")) bad.Add($"{locale}: bad message name {key}");
                var (names, contents) = Placeholders(m);
                if (contents.Contains("<undefined>")) bad.Add($"{locale}: {key} uses an undefined placeholder");
                if (en.TryGetValue(key, out var e) && !Placeholders(e).Contents.Order().SequenceEqual(contents.Order()))
                    bad.Add($"{locale}: {key} has different placeholders than en");
            }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Every_message_the_extension_uses_exists()
    {
        var manifest = File.ReadAllText(Path.Combine(Ext, "manifest.json"));
        Assert.Equal("en", JsonDocument.Parse(manifest).RootElement.GetProperty("default_locale").GetString());

        var used = new List<(string Key, string Where)>();
        void Find(string file, string pattern) =>
            used.AddRange(Regex.Matches(File.ReadAllText(file), pattern).Select(m => (m.Groups[1].Value, Repo.Relative(file))));
        Find(Path.Combine(Ext, "manifest.json"), @"__MSG_(\w+)__");
        foreach (var html in Directory.GetFiles(Path.Combine(Ext, "src"), "*.html"))
            Find(html, @"data-i18n(?:-title|-aria-label)?=""(\w+)""");
        foreach (var js in Directory.GetFiles(Path.Combine(Ext, "src"), "*.js"))
        {
            Find(js, @"\bt\(\s*'(\w+)'");
            Find(js, @"getMessage\(\s*'(\w+)'");
        }
        Assert.Contains(used, u => u.Key == "extName");

        var en = Messages("en");
        var missing = used.Where(u => !en.ContainsKey(u.Key)).Select(u => $"{u.Where}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "not in _locales/en/messages.json:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Name_and_description_fit_the_store_limits()
    {
        foreach (var locale in Locales)
        {
            var m = Messages(locale);
            Assert.InRange(Text(m["extName"]).Length, 1, 75);
            Assert.InRange(Text(m["extDescription"]).Length, 1, 132);
        }
    }
}
