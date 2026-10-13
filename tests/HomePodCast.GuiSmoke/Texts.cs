using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomePodCast.GuiSmoke;

/// <summary>
/// The app's UI texts in one language, from the same tables (src/i18n, copied next to the runner) and with the same
/// fallbacks as src/L.cs: the source strings are Simplified Chinese and double as keys.
/// </summary>
internal sealed class Texts
{
    private readonly List<IReadOnlyDictionary<string, string>> _chain = [];

    public Texts(string language)
    {
        Language = language;
        string[] chain = language switch
        {
            "zh-CN" => [],
            "zh-TW" or "en" => [language],
            _ => [language, "en"],
        };
        foreach (var lang in chain)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "i18n", lang + ".json");
            _chain.Add(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []);
        }
    }

    public string Language { get; }

    /// <summary>Chinese and Japanese, where any CJK character in the UI is expected.</summary>
    public bool Cjk => Language is "zh-CN" or "zh-TW" or "ja";

    public string T(string zh)
    {
        foreach (var table in _chain)
            if (table.TryGetValue(zh, out var text)) return text;
        return zh;
    }

    public string F(string zh, params object?[] args) => string.Format(T(zh), args);

    /// <summary>A regex for a formatted text: the literal parts escaped, every {n} or {n:F0} any text.</summary>
    public Regex Pattern(string zh) =>
        new("^" + Regex.Replace(Regex.Escape(T(zh)), @"\\\{\d+(:[^}]*)?}", ".+?") + "$", RegexOptions.Singleline);

    /// <summary>The native names of the languages, which the language picker shows in their own script.</summary>
    public static readonly string[] NativeLanguageNames = ["简体中文", "繁體中文", "日本語"];

    private static readonly Regex CjkChars = new(@"[぀-ヿ㐀-䶿一-鿿！-～　-〿]");

    public static bool HasCjk(string text) => CjkChars.IsMatch(text);
}
