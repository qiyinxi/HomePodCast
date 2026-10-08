using System.Globalization;

namespace HomePodCast.Net;

/// <summary>
/// A speaker's /info plist as sorted "path=value" lines, so the two members of a stereo pair can be diffed
/// (looking for a channel or role key such as a channel layout, tight-sync or leader field).
/// </summary>
public static class InfoDump
{
    private const int MaxBytesShown = 32;

    public static IEnumerable<string> Lines(IReadOnlyDictionary<string, object?> info)
    {
        var lines = new List<string>();
        foreach (var (key, value) in info) Add(lines, key, value);
        return lines.Order(StringComparer.Ordinal);
    }

    private static void Add(List<string> lines, string path, object? value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> dict:
                if (dict.Count == 0) lines.Add($"{path}={{}}");
                foreach (var (k, v) in dict) Add(lines, $"{path}.{k}", v);
                break;
            case IReadOnlyList<object?> list:
                if (list.Count == 0) lines.Add($"{path}=[]");
                for (int i = 0; i < list.Count; i++) Add(lines, $"{path}[{i}]", list[i]);
                break;
            case byte[] bytes:
                lines.Add($"{path}=<{Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxBytesShown)))}" +
                          $"{(bytes.Length > MaxBytesShown ? "…" : "")}> ({bytes.Length} bytes)");
                break;
            case null:
                lines.Add($"{path}=null");
                break;
            default:
                lines.Add($"{path}={Convert.ToString(value, CultureInfo.InvariantCulture)}");
                break;
        }
    }
}
