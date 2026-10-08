using System.Globalization;
using System.Text.Json;

namespace HomePodCast;

/// <summary>
/// User-visible text. Source strings are written in Simplified Chinese and double as lookup keys:
/// wrap every UI string in L.T("…") (or L.F("…{0}…", x) for formatted ones). The translations live in
/// src/i18n/{en,zh-TW,ja}.json (embedded), each mapping source string → translation; a unit test fails
/// when a wrapped string is missing from any table. Missing entries fall back zh-TW → source,
/// ja → en → source, en → source.
/// </summary>
internal static class L
{
    /// <summary>Config value meaning "follow the Windows display language".</summary>
    public const string Auto = "auto";

    /// <summary>The source language: strings are looked up but never translated.</summary>
    public const string Source = "zh-CN";

    /// <summary>Supported UI languages, as stored in config.json.</summary>
    public static readonly string[] Languages = [Source, "zh-TW", "en", "ja"];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static volatile IReadOnlyDictionary<string, string>[]? _chain;
    private static string _language = Source;

    /// <summary>The language in use (zh-CN, zh-TW, en or ja).</summary>
    public static string Language
    {
        get
        {
            if (_chain == null) Use(Auto);
            return _language;
        }
    }

    public static string T(string zh)
    {
        var chain = _chain;
        if (chain == null)
        {
            Use(Auto);
            chain = _chain!;
        }
        foreach (var table in chain)
            if (table.TryGetValue(zh, out var text)) return text;
        return zh;
    }

    public static string F(string zh, params object?[] args) => string.Format(T(zh), args);

    /// <summary>Switches the text tables; <paramref name="configured"/> is the config value ("auto" or a language).</summary>
    public static void Use(string? configured)
    {
        var lang = Resolve(configured, CultureInfo.CurrentUICulture);
        var chain = Chain(lang).Select(Table).ToArray();
        _language = lang;
        _fontName = null;
        _chain = chain;
    }

    /// <summary>Config value → language: "auto", empty or unsupported values follow the Windows display language.</summary>
    internal static string Resolve(string? configured, CultureInfo ui)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var c = CultureInfo.GetCultureInfo(configured.Trim().Replace('_', '-'));
                if (c.TwoLetterISOLanguageName is "zh" or "en" or "ja") return FromCulture(c);
            }
            catch (CultureNotFoundException) { }
        }
        return FromCulture(ui);
    }

    /// <summary>zh-Hans/zh-CN/zh-SG → zh-CN; zh-Hant/zh-TW/zh-HK/zh-MO → zh-TW; ja → ja; anything else → en.</summary>
    internal static string FromCulture(CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName == "ja") return "ja";
        if (culture.TwoLetterISOLanguageName != "zh") return "en";
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            switch (c.Name)
            {
                case "zh-Hant" or "zh-TW" or "zh-HK" or "zh-MO" or "zh-CHT": return "zh-TW";
                case "zh-Hans" or "zh-CN" or "zh-SG" or "zh-CHS": return "zh-CN";
            }
        }
        return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : "zh-CN";
    }

    /// <summary>Tables consulted, in order, before falling back to the source string.</summary>
    internal static string[] Chain(string language) => language switch
    {
        "zh-TW" => ["zh-TW"],
        "ja" => ["ja", "en"],
        "en" => ["en"],
        _ => [],
    };

    /// <summary>The embedded table for a language (empty when there is none).</summary>
    internal static IReadOnlyDictionary<string, string> Table(string language)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(language, out var table)) return table;
            table = new Dictionary<string, string>();
            try
            {
                using var stream = typeof(L).Assembly.GetManifestResourceStream($"i18n.{language}.json");
                if (stream != null)
                    table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                Log.Warn($"i18n: table {language} unreadable: {ex.Message}");
            }
            return Tables[language] = table;
        }
    }

    /// <summary>A UI font family that has the glyphs (and glyph shapes) of the current language.</summary>
    public static string FontName => _fontName ??= PickFont(Language switch
    {
        "en" => ["Segoe UI"],
        "zh-TW" => ["Microsoft JhengHei UI"],
        "ja" => ["Yu Gothic UI", "Meiryo UI", "MS UI Gothic"],
        _ => [],
    });

    private static string? _fontName;

    private static string PickFont(string[] candidates)
    {
        foreach (var name in candidates)
        {
            try
            {
                using var family = new System.Drawing.FontFamily(name); // throws if not installed
                return name;
            }
            catch (ArgumentException) { }
        }
        return "Microsoft YaHei UI";
    }
}
