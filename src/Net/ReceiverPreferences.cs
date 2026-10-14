using System.Net;

namespace HomePodCast.Net;

/// <summary>Per-receiver compatibility settings and last advertisement; shared across usage profiles.</summary>
public sealed record ReceiverPreferences(string Name, string Host, int Port, string Model, Dictionary<string, string> Txt,
    bool HasAirPlay, int? RaopPort)
{
    public int CompatibilityLatencyMs { get; set; } = 2000;
    public AirPlayDevice? Device(string id) => IPAddress.TryParse(Host, out var address)
        ? new AirPlayDevice(Name, id, address, Port, Model, Txt) { HasAirPlay = HasAirPlay, RaopPort = RaopPort, RaopTxt = Txt }
        : null;
    public static ReceiverPreferences Of(AirPlayDevice device) => new(device.Name, device.Address.ToString(), device.Port,
        device.Model, new Dictionary<string, string>(device.RaopTxt ?? device.Txt, StringComparer.OrdinalIgnoreCase), device.HasAirPlay, device.RaopPort);
}
