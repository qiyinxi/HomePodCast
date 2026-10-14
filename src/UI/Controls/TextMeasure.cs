using System.Runtime.CompilerServices;

namespace HomePodCast.UI.Controls;

/// <summary>
/// <see cref="TextRenderer.MeasureText(string?, Font?, Size, TextFormatFlags)"/> with a cache. A page asks for the same texts
/// many times: each container measures its children to size itself, then again when it places them, at every level of
/// cards and rows. 2026-10-15, on the maintainer's PC (1.1.0, offscreen with the real pages): toggling 设置 →「夜间自动断开」
/// measured 2188 texts, 161 of them different (the two time pickers measure their 48 items each time), about 45 of its
/// 70 ms. A result only depends on the text, the font (Theme's fonts are cached objects, one per style and DPI), the size it
/// may take and the flags.
/// </summary>
internal static class TextMeasure
{
    /// <summary>Status lines change every second (已推送 1:23 …): the cache starts over when it grows past this.</summary>
    private const int Capacity = 4096;

    private static readonly Dictionary<Key, Size> Cache = new();

    /// <summary>Tests: called on this thread for every text that is actually measured (not found in the cache).</summary>
    [ThreadStatic] internal static Action<string, Font, Size, TextFormatFlags>? Measured;

    public static Size Size(string? text, Font font, Size proposed, TextFormatFlags flags)
    {
        text ??= "";
        var key = new Key(text, font, proposed, flags);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var known)) return known;
        }
        var size = TextRenderer.MeasureText(text, font, proposed, flags);
        Measured?.Invoke(text, font, proposed, flags);
        lock (Cache)
        {
            if (Cache.Count >= Capacity) Cache.Clear();
            Cache[key] = size;
        }
        return size;
    }

    /// <summary>The font by reference: Font.GetHashCode asks GDI+ for the family name every time.</summary>
    private readonly struct Key(string text, Font font, Size proposed, TextFormatFlags flags) : IEquatable<Key>
    {
        private readonly string _text = text;
        private readonly Font _font = font;
        private readonly Size _proposed = proposed;
        private readonly TextFormatFlags _flags = flags;

        public bool Equals(Key other) => ReferenceEquals(_font, other._font) && _proposed == other._proposed && _flags == other._flags &&
                                         string.Equals(_text, other._text, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(string.GetHashCode(_text, StringComparison.Ordinal), RuntimeHelpers.GetHashCode(_font), _proposed, _flags);
    }
}
