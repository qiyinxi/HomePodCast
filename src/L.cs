namespace HomePodCast;

/// <summary>
/// User-visible text. Source strings are written in Simplified Chinese and double as lookup keys:
/// wrap every UI string in L.T("…") (or L.F("…{0}…", x) for formatted ones). Until the translation
/// tables land this returns the source string unchanged.
/// </summary>
internal static class L
{
    public static string T(string zh) => zh;

    public static string F(string zh, params object?[] args) => string.Format(T(zh), args);
}
