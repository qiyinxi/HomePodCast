namespace HomePodCast.Net;

/// <summary>The selected receiver's controls. HomePod scenes never pass through this policy.</summary>
internal sealed record ReceiverLatencySelection(AirPlayDevice Device, ReceiverProtocol Protocol,
    ReceiverLatency Policy, int Value)
{
    public bool Compatibility => Protocol is ReceiverProtocol.Raop or ReceiverProtocol.AirPlay2Buffered;
    public bool BelowRecommendation => Value < Policy.RecommendedMinimumMs;

    public bool TakeCompatibilityNotice(ReceiverPreferences preferences)
    {
        if (!Compatibility || preferences.CompatibilityNoticeShown) return false;
        preferences.CompatibilityNoticeShown = true;
        return true;
    }

    public static ReceiverLatencySelection? Create(AirPlayDevice? device, ReceiverPreferences? preferences,
        ReceiverProtocol? active = null, int? processing = null)
    {
        if (device == null || device.Kind == ReceiverKind.HomePod) return null;
        var protocol = device.Kind == ReceiverKind.AppleTv ? ReceiverProtocol.AirPlay2Realtime
            : active ?? (preferences?.ProtocolOverride is { } forced && forced != ReceiverProtocol.Auto ? forced
                : preferences?.LastSuccessfulProtocol ?? (device.Kind is ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1 ? ReceiverProtocol.Raop : ReceiverProtocol.AirPlay2Realtime));
        var policy = ReceiverLatencies.For(device.Kind, protocol, processing ?? preferences?.ArrivalToRenderMs, device.Model);
        bool compatibility = protocol is ReceiverProtocol.Raop or ReceiverProtocol.AirPlay2Buffered;
        int value = compatibility ? preferences?.CompatibilityLatencyMs ?? policy.DefaultMs
            : preferences?.RealtimeLatencyMs ?? Math.Max(policy.DefaultMs, policy.RecommendedMinimumMs);
        if (compatibility && !ReceiverLatencies.CompatibilityChoices.Contains(value)) value = 2000;
        return new(device, protocol, policy, policy.MinimumMs > policy.MaximumMs ? policy.DefaultMs : policy.Clamp(value));
    }

    public void Save(ReceiverPreferences preferences, int milliseconds)
    {
        if (Compatibility)
        {
            if (!ReceiverLatencies.CompatibilityChoices.Contains(milliseconds)) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            preferences.CompatibilityLatencyMs = milliseconds;
        }
        else preferences.RealtimeLatencyMs = Policy.Clamp(milliseconds);
    }
}
