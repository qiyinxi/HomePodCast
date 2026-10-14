namespace HomePodCast.Net;

public enum ReceiverProtocol { Auto, AirPlay2Realtime, AirPlay2Buffered, Raop }

/// <summary>Advertised capabilities and explicit preferences; protocol attempts are independently disposable.</summary>
public static class ReceiverProtocols
{
    public static IReadOnlyList<ReceiverProtocol> Candidates(AirPlayDevice device, ReceiverPreferences? settings)
    {
        if (device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv) return [ReceiverProtocol.AirPlay2Realtime];
        if (settings?.ProtocolOverride is { } forced && forced != ReceiverProtocol.Auto) return [forced];
        if (device.Kind is ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1) return [ReceiverProtocol.Raop];
        var protocols = new List<ReceiverProtocol> { ReceiverProtocol.AirPlay2Realtime };
        if (device.Kind == ReceiverKind.ThirdPartyAirPlay2 && device.RaopPort != null) protocols.Add(ReceiverProtocol.Raop);
        protocols.Add(ReceiverProtocol.AirPlay2Buffered);
        if (device.Kind == ReceiverKind.Unknown) protocols.Add(ReceiverProtocol.Raop);
        if (settings?.LastSuccessfulProtocol is { } remembered && protocols.Remove(remembered)) protocols.Insert(0, remembered);
        return protocols;
    }

    public static string Label(ReceiverProtocol protocol) => protocol switch
    {
        ReceiverProtocol.Auto => L.T("自动"),
        ReceiverProtocol.AirPlay2Realtime => L.T("AirPlay 2 低延迟（实验性）"), // experimental since 1.1
        ReceiverProtocol.AirPlay2Buffered => L.T("AirPlay 2 缓冲（实验性，未在实机上测试）"), // experimental since 1.1
        _ => L.T("AirPlay 1 兼容（实验性）"), // experimental since 1.1
    };
}

public sealed record ReceiverLatency(int MinimumMs, int RecommendedMinimumMs, int DefaultMs, int MaximumMs)
{
    public int Clamp(int requested) => Math.Clamp(requested, MinimumMs, MaximumMs);
    public bool Warns(int value) => value < RecommendedMinimumMs;
}

/// <summary>Compatibility recommendations never change the original HomePod scenes or processing-time default.</summary>
public static class ReceiverLatencies
{
    public static readonly int[] CompatibilityChoices = [1000, 1500, 2000];
    public static ReceiverLatency For(ReceiverKind kind, ReceiverProtocol protocol, int? processingMs = null, string model = "")
    {
        if (protocol is ReceiverProtocol.Raop or ReceiverProtocol.AirPlay2Buffered)
            return new(1000, model.StartsWith("AirPort", StringComparison.OrdinalIgnoreCase) ? 2000 : 1000, 2000, 2000);
        int processing = Math.Max(0, processingMs ?? (kind is ReceiverKind.HomePod or ReceiverKind.AppleTv ? 85 : 150));
        return kind switch
        {
            ReceiverKind.HomePod => new(processing + 15, processing + 20, 120, 500),
            ReceiverKind.AppleTv => new(processing + 15, processing + 30, 200, 500),
            _ => new(processingMs.HasValue ? Math.Max(120, processing + 30) : 200, Math.Max(150, processing + 50), 250, 500),
        };
    }
}
