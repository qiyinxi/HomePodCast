using System.Net;
using System.Net.Sockets;

namespace HomePodCast.Net;

/// <summary>Only manually supplied addresses are persisted; automatic discovery remains live.</summary>
public sealed record ManualAirPlayDevice(string Name, string DeviceId, string Host, int Port, string Model,
    Dictionary<string, string> Txt)
{
    public AirPlayDevice? ToDevice()
    {
        if (!TryEndpoint(Host, Port.ToString(), out var address, out _)) return null;
        var kind = ReceiverKinds.Classify(Model, Txt);
        return new AirPlayDevice(Name, DeviceId, address!, Port, Model, Txt)
        {
            RaopPort = kind is ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1 ? Port : null,
        };
    }

    public static bool TryEndpoint(string host, string portText, out IPAddress? address, out int port)
    {
        port = 7000;
        return IPAddress.TryParse(host.Trim(), out address) && address.AddressFamily == AddressFamily.InterNetwork
            && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None)
            && address.GetAddressBytes()[0] is > 0 and < 224
            && (string.IsNullOrWhiteSpace(portText) || int.TryParse(portText, out port)) && port is >= 1 and <= 65535;
    }

    public static ManualAirPlayDevice FromInfo(IPAddress address, int port, IReadOnlyDictionary<string, object?> info)
    {
        var txt = TxtRecord.FromInfo(info) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[] { "model", "manufacturer", "features", "srcvers" })
            if (info.GetValueOrDefault(key) is { } value) txt[key] = value.ToString() ?? "";
        string id = info.GetValueOrDefault("deviceID")?.ToString() ?? info.GetValueOrDefault("deviceid")?.ToString()
            ?? txt.GetValueOrDefault("deviceid") ?? $"manual:{address}:{port}";
        string name = info.GetValueOrDefault("name")?.ToString() ?? address.ToString();
        return new(name, id, address.ToString(), port, txt.GetValueOrDefault("model", ""), txt);
    }

    public static List<AirPlayDevice> Include(IReadOnlyList<AirPlayDevice> found, IEnumerable<ManualAirPlayDevice> manual)
    {
        var devices = found.ToList();
        foreach (var saved in manual)
        {
            if (saved.ToDevice() is not { } device) continue;
            if (devices.Any(d => d.Address.Equals(device.Address) && d.Port == device.Port
                || Key(d.DeviceId) == Key(device.DeviceId))) continue;
            devices.Add(device);
        }
        return devices.OrderBy(d => ReceiverKinds.SortOrder(d.Kind)).ThenBy(d => d.Name).ToList();
    }

    private static string Key(string id) => id.Replace(":", "").Replace("-", "").ToUpperInvariant();
}
