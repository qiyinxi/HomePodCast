using System.Globalization;
using System.Text.Json;

namespace HomePodCast;

/// <summary>
/// User-visible text. Source strings are written in Simplified Chinese and double as lookup keys:
/// wrap every UI string in L.T("…") (or L.F("…{0}…", x) for formatted ones). The translations live in
/// src/i18n/&lt;language&gt;.json (embedded), each mapping source string → translation; every embedded table is a
/// supported language, and a unit test fails when a wrapped string is missing from any of them. Missing entries
/// fall back zh-TW → source, en → source, and any other language → en → source.
/// </summary>
internal static class L
{
    /// <summary>Config value meaning "follow the Windows display language".</summary>
    public const string Auto = "auto";

    /// <summary>The source language: strings are looked up but never translated.</summary>
    public const string Source = "zh-CN";

    /// <summary>Embedded tables are named i18n.&lt;language&gt;.json (see HomePodCast.csproj).</summary>
    private const string ResourcePrefix = "i18n.", ResourceSuffix = ".json";

    /// <summary>Supported UI languages, as stored in config.json: the source and one per embedded table.</summary>
    public static readonly string[] Languages = Discover();

    // Language names are shown in their own language, never translated.
    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-CN"] = "简体中文",
        ["zh-TW"] = "繁體中文",
        ["en"] = "English",
        ["ja"] = "日本語",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["it"] = "Italiano",
        ["nl"] = "Nederlands",
        ["pl"] = "Polski",
        ["pt"] = "Português",
        ["sv"] = "Svenska",
        ["da"] = "Dansk",
        ["nb"] = "Norsk",
        ["fi"] = "Suomi",
    };

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static volatile IReadOnlyDictionary<string, string>[]? _chain;
    private static string _language = Source;

    /// <summary>The language in use (one of <see cref="Languages"/>).</summary>
    public static string Language
    {
        get
        {
            if (_chain == null) Use(Auto);
            return _language;
        }
    }

    /// <summary>The UI language is written in Latin script (shown in Segoe UI); false for Chinese and Japanese.</summary>
    public static bool LatinScript => IsLatinScript(Language);

    internal static bool IsLatinScript(string language) => language is not (Source or "zh-TW" or "ja");

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

    /// <summary>The language's name in that language ("Deutsch", "日本語").</summary>
    public static string NativeName(string language)
    {
        if (NativeNames.TryGetValue(language, out var name)) return name;
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var native = culture.NativeName;
            return native.Length == 0 ? language : char.ToUpper(native[0], culture) + native[1..];
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    /// <summary>Config value → language: "auto", empty or unsupported values follow the Windows display language.</summary>
    internal static string Resolve(string? configured, CultureInfo ui)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var c = CultureInfo.GetCultureInfo(configured.Trim().Replace('_', '-'));
                var lang = FromCulture(c);
                // FromCulture answers "en" for anything it doesn't know; only take that when English was asked for.
                if (lang != "en" || c.TwoLetterISOLanguageName == "en") return lang;
            }
            catch (CultureNotFoundException) { }
        }
        return FromCulture(ui);
    }

    /// <summary>
    /// Windows culture → UI language: zh-Hans/zh-CN/zh-SG → zh-CN; zh-Hant/zh-TW/zh-HK/zh-MO → zh-TW;
    /// nb/nn/no → nb; otherwise the two-letter language (sv-SE → sv, pt-BR → pt) when it has a table; anything else → en.
    /// </summary>
    internal static string FromCulture(CultureInfo culture) => FromCulture(culture, Languages);

    /// <summary><see cref="FromCulture(CultureInfo)"/> for a given set of supported languages.</summary>
    internal static string FromCulture(CultureInfo culture, IReadOnlyCollection<string> supported)
    {
        var iso = culture.TwoLetterISOLanguageName;
        if (iso == "zh") return Chinese(culture);
        var lang = iso switch
        {
            "nb" or "nn" or "no" => "nb",
            _ => iso,
        };
        return supported.Contains(lang) ? lang : "en";
    }

    private static string Chinese(CultureInfo culture)
    {
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
        Source => [],
        "zh-TW" => ["zh-TW"],
        "en" => ["en"],
        _ => [language, "en"],
    };

    /// <summary>The source language, then every embedded table (i18n.&lt;language&gt;.json), sorted by code.</summary>
    private static string[] Discover()
    {
        var embedded = typeof(L).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^ResourceSuffix.Length])
            .Where(lang => lang.Length > 0 && lang != Source)
            .Order(StringComparer.Ordinal);
        return [Source, .. embedded];
    }

    /// <summary>The embedded table for a language (empty when there is none).</summary>
    internal static IReadOnlyDictionary<string, string> Table(string language)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(language, out var table)) return table;
            table = new Dictionary<string, string>();
            try
            {
                using var stream = typeof(L).Assembly.GetManifestResourceStream(ResourcePrefix + language + ResourceSuffix);
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
        Source => [],
        "zh-TW" => ["Microsoft JhengHei UI"],
        "ja" => ["Yu Gothic UI", "Meiryo UI", "MS UI Gothic"],
        _ => ["Segoe UI"],
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
