using System.Text.Json;

namespace HomePodCast.VideoWindow;

internal static class StatusDelay
{
    public static int Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("app").GetString() != "HomePodCast") throw new InvalidDataException("Unexpected status service.");
        if (!root.GetProperty("streaming").GetBoolean()) return 0;
        int value = root.GetProperty("videoDelayMs").GetInt32();
        if (value is < 0 or > 2000) throw new InvalidDataException("Video delay is outside 0–2000 ms.");
        return value;
    }
    public static int Effective(int sourceMs, int offsetMs) => Math.Clamp(sourceMs + offsetMs, 0, 2000);
}
