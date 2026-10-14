using System.Text;

namespace HomePodCast.Net;

/// <summary>TXT record data: length-prefixed "key=value" strings (mDNS answers; /info's "txtAirPlay" is the same bytes).</summary>
public static class TxtRecord
{
    public static Dictionary<string, string> Parse(byte[] data) => Parse(data, 0, data.Length);

    /// <summary>The entries in <paramref name="data"/>[start..start+length]; stops at an entry that runs past the end.</summary>
    public static Dictionary<string, string> Parse(byte[] data, int start, int length)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int end = Math.Min(data.Length, start + length);
        for (int p = start; p < end;)
        {
            int l = data[p++];
            if (p + l > end) break;
            var kv = Encoding.UTF8.GetString(data, p, l);
            p += l;
            int eq = kv.IndexOf('=');
            if (eq > 0) txt[kv[..eq]] = kv[(eq + 1)..];
        }
        return txt;
    }

    /// <summary>The TXT record a speaker's /info carries ("txtAirPlay"); null when it has none.</summary>
    public static Dictionary<string, string>? FromInfo(IReadOnlyDictionary<string, object?>? info) =>
        info?.GetValueOrDefault("txtAirPlay") is byte[] bytes ? Parse(bytes) : null;
}
