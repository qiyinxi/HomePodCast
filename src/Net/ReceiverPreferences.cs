using System.Net;

namespace HomePodCast.Net;

/// <summary>Per-receiver compatibility settings and last advertisement; shared across usage profiles.</summary>
public sealed record ReceiverPreferences(string Name, string Host, int Port, string Model, Dictionary<string, string> Txt,
    bool HasAirPlay, int? RaopPort)
{
    public ReceiverProtocol ProtocolOverride { get; set; }
    public ReceiverProtocol? LastSuccessfulProtocol { get; set; }
    public int? RealtimeLatencyMs { get; set; }
    public int? ArrivalToRenderMs { get; set; }
    public Dictionary<string, string>? RaopTxt { get; set; }
    public int CompatibilityLatencyMs { get; set; } = 2000;
    public bool CompatibilityNoticeShown { get; set; }
    public AirPlayDevice? Device(string id) => IPAddress.TryParse(Host, out var address)
        ? new AirPlayDevice(Name, id, address, Port, Model, Txt) { HasAirPlay = HasAirPlay, RaopPort = RaopPort, RaopTxt = RaopTxt ?? Txt }
        : null;
    public static ReceiverPreferences Of(AirPlayDevice device) => new(device.Name, device.Address.ToString(), device.Port,
        device.Model, new Dictionary<string, string>(device.Txt, StringComparer.OrdinalIgnoreCase), device.HasAirPlay, device.RaopPort)
        { RaopTxt = device.RaopTxt != null ? new Dictionary<string, string>(device.RaopTxt, StringComparer.OrdinalIgnoreCase) : null };
}
