using System.Text;

namespace HomePodCast.Tests;

/// <summary>
/// Just enough of a C# lexer to find the texts passed to L.T("…") / L.F("…", …): skips comments, decodes
/// regular, verbatim and raw string literals, joins "a" + "b", looks into interpolation holes, and resolves
/// L.T(SomeConst) through `const string SomeConst = "…";` declarations.
/// </summary>
internal static class SourceScanner
{
    public enum Kind { Ident, Punct, Str, Interp }

    public readonly record struct Tok(Kind Kind, string Text, int Line);

    /// <summary>One L.T/L.F call: the looked-up text, or why it can't be determined statically.</summary>
    public sealed record Use(string File, int Line, string? Key, string? Problem);

    public static IEnumerable<string> SourceFiles(string srcDir) =>
        Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);

    public static List<Use> Scan(IEnumerable<(string Name, string Source)> files)
    {
        var lexed = files.Select(f => (f.Name, Tokens: Lex(f.Source))).ToList();
        var consts = new Dictionary<string, string?>();
        foreach (var (_, toks) in lexed)
            foreach (var (name, value) in Consts(toks))
                consts[name] = consts.TryGetValue(name, out var old) && old != value ? null : value; // null: ambiguous

        var uses = new List<Use>();
        foreach (var (file, toks) in lexed)
            uses.AddRange(Calls(file, toks, consts));
        return uses;
    }

    private static IEnumerable<(string, string)> Consts(List<Tok> t)
    {
        for (int i = 0; i + 4 < t.Count; i++)
        {
            if (t[i] is not { Kind: Kind.Ident, Text: "const" } || t[i + 1] is not { Kind: Kind.Ident, Text: "string" } ||
                t[i + 2].Kind != Kind.Ident || !P(t, i + 3, "="))
                continue;
            int j = i + 4;
            if (Literal(t, ref j) is { } value && P(t, j, ";"))
                yield return (t[i + 2].Text, value);
        }
    }

    private static IEnumerable<Use> Calls(string file, List<Tok> t, Dictionary<string, string?> consts)
    {
        for (int i = 0; i + 4 < t.Count; i++)
        {
            if (t[i] is not { Kind: Kind.Ident, Text: "L" } || !P(t, i + 1, ".") ||
                t[i + 2] is not { Kind: Kind.Ident, Text: "T" or "F" } || !P(t, i + 3, "(") || P(t, i - 1, "."))
                continue;
            int line = t[i].Line, j = i + 4;
            string call = $"L.{t[i + 2].Text}";
            if (Literal(t, ref j) is { } key)
            {
                yield return P(t, j, ")") || P(t, j, ",")
                    ? new Use(file, line, key, null)
                    : new Use(file, line, null, $"{call}: the text must be one string literal (no + with variables)");
                continue;
            }
            if (t[j].Kind == Kind.Interp)
            {
                yield return new Use(file, line, null, $"{call}($\"…\") can't be translated: use L.F(\"…{{0}}…\", x)");
                continue;
            }
            // L.T(Name) or L.T(Type.Name) → a const string declared somewhere in src
            string? name = null;
            while (j < t.Count && t[j].Kind == Kind.Ident)
            {
                name = t[j++].Text;
                if (P(t, j, ".")) j++;
                else break;
            }
            if (name != null && (P(t, j, ")") || P(t, j, ",")) && consts.TryGetValue(name, out var value) && value != null)
                yield return new Use(file, line, value, null);
            else
                yield return new Use(file, line, null, $"{call}: the text must be a string literal or a const string");
        }
    }

    private static bool P(List<Tok> t, int i, string punct) =>
        i >= 0 && i < t.Count && t[i].Kind == Kind.Punct && t[i].Text == punct;

    /// <summary>"a" or "a" + "b" + …; advances past it.</summary>
    private static string? Literal(List<Tok> t, ref int j)
    {
        if (j >= t.Count || t[j].Kind != Kind.Str) return null;
        var sb = new StringBuilder(t[j++].Text);
        while (P(t, j, "+") && j + 1 < t.Count && t[j + 1].Kind == Kind.Str)
        {
            sb.Append(t[j + 1].Text);
            j += 2;
        }
        return sb.ToString();
    }

    public static List<Tok> Lex(string source)
    {
        var lexer = new Lexer(source);
        lexer.Code(inHole: false);
        return lexer.Tokens;
    }

    private sealed class Lexer(string s)
    {
        public readonly List<Tok> Tokens = [];
        private int _i;
        private int _line = 1;

        private char At(int k) => _i + k < s.Length ? s[_i + k] : '\0';

        private void Advance(int n = 1)
        {
            for (int k = 0; k < n && _i < s.Length; k++, _i++)
                if (s[_i] == '\n') _line++;
        }

        private void SkipToEol()
        {
            while (_i < s.Length && s[_i] != '\n') _i++;
        }

        /// <summary>Code up to the end of input, or (inside an interpolation hole) up to the hole's closing brace.</summary>
        public void Code(bool inHole)
        {
            int depth = 0;
            bool lineStart = true;
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c == '\n') { lineStart = true; Advance(); continue; }
                if (char.IsWhiteSpace(c)) { Advance(); continue; }
                if (c == '#' && lineStart && !inHole) { SkipToEol(); continue; } // preprocessor directive
                lineStart = false;
                if (c == '/' && At(1) == '/') { SkipToEol(); continue; }
                if (c == '/' && At(1) == '*')
                {
                    int end = s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    Advance((end < 0 ? s.Length : end + 2) - _i);
                    continue;
                }
                if (inHole && depth == 0)
                {
                    if (c == '}') return;
                    if (c == ':') // format specifier: runs to the closing brace
                    {
                        while (_i < s.Length && s[_i] != '}') Advance();
                        return;
                    }
                }
                if (String()) continue;
                if (c == '\'')
                {
                    Advance();
                    while (_i < s.Length && s[_i] != '\'') Advance(s[_i] == '\\' ? 2 : 1);
                    Advance();
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || (c == '@' && (char.IsLetter(At(1)) || At(1) == '_')))
                {
                    int start = _i, line = _line;
                    Advance();
                    while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_')) Advance();
                    Tokens.Add(new Tok(Kind.Ident, s[start.._i].TrimStart('@'), line));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_' || (s[_i] == '.' && char.IsDigit(At(1)))))
                        Advance();
                    continue;
                }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                Tokens.Add(new Tok(Kind.Punct, c.ToString(), _line));
                Advance();
            }
        }

        /// <summary>A string literal of any kind starting here, if there is one.</summary>
        private bool String()
        {
            int k = 0, dollars = 0;
            bool verbatim = false;
            for (; ; k++)
            {
                if (At(k) == '$') dollars++;
                else if (At(k) == '@') verbatim = true;
                else break;
            }
            if (At(k) != '"') return false;
            int line = _line, index = Tokens.Count;
            Tokens.Add(default); // filled in below, so the literal precedes the tokens of its holes
            Advance(k);
            int quotes = 0;
            while (At(quotes) == '"') quotes++;
            var text = quotes >= 3 ? Raw(quotes, dollars) : Quoted(verbatim, dollars);
            Tokens[index] = new Tok(dollars > 0 ? Kind.Interp : Kind.Str, text, line);
            return true;
        }

        private string Quoted(bool verbatim, int dollars)
        {
            var sb = new StringBuilder();
            Advance(); // opening quote
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c == '"')
                {
                    if (verbatim && At(1) == '"') { sb.Append('"'); Advance(2); continue; }
                    Advance();
                    break;
                }
                if (c == '\\' && !verbatim) { sb.Append(Escape()); continue; }
                if (dollars > 0 && c == '{')
                {
                    if (At(1) == '{') { sb.Append('{'); Advance(2); continue; }
                    Advance();
                    Code(inHole: true);
                    Advance(); // closing brace
                    continue;
                }
                if (dollars > 0 && c == '}' && At(1) == '}') { sb.Append('}'); Advance(2); continue; }
                sb.Append(c);
                Advance();
            }
            return sb.ToString();
        }

        private string Raw(int quotes, int dollars)
        {
            var sb = new StringBuilder();
            Advance(quotes);
            while (_i < s.Length)
            {
                if (s[_i] == '"')
                {
                    int q = 0;
                    while (At(q) == '"') q++;
                    Advance(q);
                    if (q >= quotes) break;
                    sb.Append('"', q);
                    continue;
                }
                if (dollars > 0 && s[_i] == '{')
                {
                    int b = 0;
                    while (At(b) == '{') b++;
                    if (b < dollars) { sb.Append('{', b); Advance(b); continue; }
                    sb.Append('{', b - dollars);
                    Advance(b);
                    Code(inHole: true);
                    Advance(dollars);
                    continue;
                }
                sb.Append(s[_i]);
                Advance();
            }
            // Multi-line raw string: drop the first and last line, and the closing line's indentation.
            var raw = sb.ToString().Replace("\r\n", "\n");
            if (!raw.StartsWith('\n')) return raw;
            var lines = raw.Split('\n');
            var indent = lines[^1];
            return string.Join("\n", lines[1..^1].Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l[indent.Length..] : l.TrimStart()));
        }

        private string Escape()
        {
            char e = At(1);
            Advance(2);
            string Hex(int max)
            {
                int n = 0;
                while (n < max && Uri.IsHexDigit(At(n))) n++;
                var h = s.Substring(_i, n);
                Advance(n);
                return char.ConvertFromUtf32(Convert.ToInt32(h, 16));
            }
            return e switch
            {
                'n' => "\n", 't' => "\t", 'r' => "\r", '0' => "\0", 'a' => "\a", 'b' => "\b",
                'f' => "\f", 'v' => "\v", 'e' => "\u001b",
                'u' => Hex(4), 'U' => Hex(8), 'x' => Hex(4),
                _ => e.ToString(), // \\ \" \'
            };
        }
    }
}
