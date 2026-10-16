using System.Text.Json.Serialization;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>Saved choices use UDN identity; discovery resolves current addresses and service URLs for each connection.</summary>
public sealed record DlnaOutputSettings(string Udn, string Name)
{
    public int EstimatedLatencyMs { get; init; } = 2000;
    public int TrimMs { get; init; }
    public int VolumeOffset { get; init; }
    public bool Required { get; init; }
    public ChannelMode Channels { get; init; }
    public string? PreferredFormat { get; init; }
    public string Framing { get; init; } = "chunked";
    [JsonIgnore] public string Id => "dlna:" + Udn;
}

public static class DlnaGroupSettings
{
    public static string Key(string udn) => udn.Trim().ToUpperInvariant();

    public static List<DlnaOutputSettings> Copy(IEnumerable<DlnaOutputSettings>? outputs) => (outputs ?? [])
        .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Udn)
            && o.Udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) && o.Udn.Length is > 5 and <= 256
            && !o.Udn.Any(char.IsControl))
        .DistinctBy(o => Key(o.Udn)).Take(8).Select(o => o with
        {
            Udn = o.Udn.Trim(), Name = string.IsNullOrWhiteSpace(o.Name) ? o.Udn : o.Name,
            EstimatedLatencyMs = Math.Clamp(o.EstimatedLatencyMs, 0, 20000), TrimMs = Math.Clamp(o.TrimMs, -5000, 5000),
            VolumeOffset = Math.Clamp(o.VolumeOffset, -100, 100), Channels = Enum.IsDefined(o.Channels) ? o.Channels : ChannelMode.Stereo,
            PreferredFormat = o.PreferredFormat?.ToLowerInvariant() is "l16" or "wav" or "mp3" or "aac" ? o.PreferredFormat.ToLowerInvariant() : null,
            Framing = string.Equals(o.Framing, "length", StringComparison.OrdinalIgnoreCase) ? "length" : "chunked",
        }).ToList();

    internal static IReadOnlyList<DlnaOutputSettings> Selected(AppConfig cfg)
    {
        if (cfg.GroupCaptureDlnaUdns == null) return [];
        var selected = cfg.GroupCaptureDlnaUdns.Where(u => !string.IsNullOrWhiteSpace(u)).Select(Key).ToHashSet();
        return Copy(cfg.GroupDlnaOutputs).Where(o => selected.Contains(Key(o.Udn))).ToArray();
    }
}
