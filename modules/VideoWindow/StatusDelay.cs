using System.Text.Json;

namespace HomePodCast.VideoWindow;

/// <summary>What the viewing window reads from HomePodCast's GET /v1/status: the sound lag while streaming, and the version.</summary>
internal readonly record struct LocalStatus(int DelayMs, string? Version);

internal static class StatusDelay
{
    public static LocalStatus Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // "Akoustra": the app's coming name (2026-10-30), so this module keeps working if the app ever reports it.
        if (root.GetProperty("app").GetString() is not ("HomePodCast" or "Akoustra")) throw new InvalidDataException("Unexpected status service.");
        string? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (!root.GetProperty("streaming").GetBoolean()) return new(0, version);
        int value = root.GetProperty("videoDelayMs").GetInt32();
        if (value is < 0 or > 2000) throw new InvalidDataException("Video delay is outside 0–2000 ms.");
        return new(value, version);
    }

    public static int Effective(int sourceMs, int offsetMs) => Math.Clamp(sourceMs + offsetMs, 0, 2000);

    /// <summary>
    /// The module comes in its own zip and is not updated with the app (in-app updates replace only the app): the same
    /// X.Y.Z as the app reports, or the window says to download the matching module.
    /// </summary>
    public static bool SameVersion(string? app, Version? module) =>
        module != null && app != null && Version.TryParse(app.Split('-', '+')[0], out var a) &&
        a.Major == module.Major && a.Minor == module.Minor && Math.Max(0, a.Build) == Math.Max(0, module.Build);
}
