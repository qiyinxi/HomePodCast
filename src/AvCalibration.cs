using System.Text;
using System.Text.Json.Serialization;

namespace HomePodCast;

/// <summary>The speaker and actual capture path that a subjective sync measurement belongs to.</summary>
public readonly record struct AvCalibrationContext(string SpeakerId, string CaptureEndpointId, bool PerAppRouting,
    bool FollowsDefault = false)
{
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(SpeakerId) && !string.IsNullOrWhiteSpace(CaptureEndpointId)
        && SpeakerId.Length <= 2048 && CaptureEndpointId.Length <= 2048;

    /// <summary>Case-independent ids, length-delimited so device ids cannot collide with the separators.</summary>
    [JsonIgnore]
    public string Key => IsValid
        ? $"{Part(SpeakerId)}.{Part(CaptureEndpointId)}.{(PerAppRouting ? 1 : 0)}.{(FollowsDefault ? 1 : 0)}"
        : "";

    private static string Part(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));

    public static AvCalibrationContext? For(AppConfig config, string? capturedEndpointId, bool perAppRouting)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceId) || string.IsNullOrWhiteSpace(capturedEndpointId)) return null;
        // Include the group topology: a measurement for one speaker must not silently become a multi-room one.
        string speaker = StreamController.Normalize(config.DeviceId.Trim());
        if (GroupPlan.FromConfig(config) is { } group)
            speaker += $"|{group.Kind}|{string.Join(",", group.Members.Select(m => GroupPlan.Key(m.DeviceId)).Order())}|{group.PairLeaderOnly}";
        return new AvCalibrationContext(speaker, capturedEndpointId.Trim(), perAppRouting,
            Audio.CaptureEndpoint.FollowsDefault(config.CaptureDeviceId));
    }
}

/// <summary>
/// A correction to the estimate, rather than a fixed total delay. Requested latency and the adaptive FIFO
/// remain live inputs after calibration, so switching scenes and recovering from Wi-Fi jitter still work.
/// </summary>
public sealed record AvCalibration
{
    public AvCalibrationContext Context { get; init; }
    public int ResidualMs { get; init; }
    public int MeasuredOffsetMs { get; init; }
    public int EstimatedOffsetMs { get; init; }
    public DateTime MeasuredAtUtc { get; init; }

    [JsonIgnore]
    public bool IsValid => Context.IsValid && MeasuredOffsetMs is >= 0 and <= AvCalibrations.MaxMeasuredMs
        && EstimatedOffsetMs is >= 0 and <= AvCalibrations.MaxEstimatedMs
        && ResidualMs is >= -AvCalibrations.MaxResidualMs and <= AvCalibrations.MaxResidualMs
        && ResidualMs == MeasuredOffsetMs - EstimatedOffsetMs && MeasuredAtUtc != default;
}

public static class AvCalibrations
{
    public const int MaxMeasuredMs = 2000, MaxEstimatedMs = 4000, MaxResidualMs = 2000;

    public static AvCalibration? Get(AppConfig config, AvCalibrationContext? context)
    {
        if (context is not { IsValid: true } c || config.SyncCalibrations == null) return null;
        return config.SyncCalibrations.TryGetValue(c.Key, out var calibration)
            && calibration is { IsValid: true } && calibration.Context.Key == c.Key ? calibration : null;
    }

    /// <summary>Records a measured offset against the estimate from the same unchanged test path.</summary>
    public static bool Apply(AppConfig config, AvCalibrationContext context, int measuredOffsetMs, int estimatedOffsetMs,
        DateTime? measuredAtUtc = null)
    {
        var value = new AvCalibration
        {
            Context = context,
            MeasuredOffsetMs = measuredOffsetMs,
            EstimatedOffsetMs = estimatedOffsetMs,
            ResidualMs = measuredOffsetMs - estimatedOffsetMs,
            MeasuredAtUtc = measuredAtUtc ?? DateTime.UtcNow,
        };
        if (!value.IsValid) return false;
        var updated = new Dictionary<string, AvCalibration>(config.SyncCalibrations ?? [], StringComparer.Ordinal)
        {
            [context.Key] = value,
        };
        config.SyncCalibrations = updated; // readers on the local API/player threads keep their immutable snapshot
        return true;
    }

    public static bool Reset(AppConfig config, AvCalibrationContext? context)
    {
        if (context is not { IsValid: true } c || Get(config, c) == null) return false;
        var updated = new Dictionary<string, AvCalibration>(config.SyncCalibrations ?? [], StringComparer.Ordinal);
        updated.Remove(c.Key);
        config.SyncCalibrations = updated;
        return true;
    }

    /// <summary>Old files, null collections and malformed entries are safe to load and never supply a delay.</summary>
    public static void Normalize(AppConfig config)
    {
        config.SyncCalibrations = (config.SyncCalibrations ?? [])
            .Where(kv => kv.Value is { IsValid: true } && kv.Key == kv.Value.Context.Key)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
