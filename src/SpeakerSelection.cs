using System.Net;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>One speaker of the selection: its device id, name, last known address and the TXT record of the last scan.</summary>
public sealed record SelectedSpeaker(string DeviceId, string Name, IPAddress? Address, IReadOnlyDictionary<string, string>? Txt, int Port = 7000);

/// <summary>
/// The speakers the config selects, for 设置 → 音箱信息 and the takeover check of a 连接 click: the speaker; or both
/// members of a stereo pair (found in the last scan); or the two speakers of a multi-room group. Addresses: the one saved
/// by the last connection, else the scan's.
/// </summary>
public static class SpeakerSelection
{
    public static List<SelectedSpeaker> Of(AppConfig cfg, IReadOnlyList<AirPlayDevice> scan)
    {
        if (cfg.DeviceId is not { } id) return [];
        scan = ManualAirPlayDevice.Include(scan, cfg.ManualDevices);
        if (StereoPairs.TryGetTsid(id, out var tsid))
        {
            var members = StereoPairs.Members(scan, tsid);
            return members.Count > 0
                ? members.Select(d => new SelectedSpeaker(d.DeviceId, d.Name, d.Address, d.Txt, d.Port)).ToList()
                : [new SelectedSpeaker(id, cfg.DeviceName ?? tsid, null, null)]; // not found: shown as unreadable
        }
        var list = new List<SelectedSpeaker> { One(id, cfg.DeviceName, cfg.Host, scan) };
        if (GroupPlan.FromConfig(cfg) is { Kind: GroupKind.MultiRoom } && cfg.MultiRoomDeviceId is { } extra)
            list.Add(One(extra, cfg.MultiRoomDeviceName, cfg.MultiRoomHost, scan));
        return list;
    }

    private static SelectedSpeaker One(string id, string? name, string? host, IReadOnlyList<AirPlayDevice> scan)
    {
        var found = scan.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id));
        var address = IPAddress.TryParse(host, out var saved) && !saved.Equals(IPAddress.None) ? saved : found?.Address;
        return new SelectedSpeaker(id, found?.Name ?? name ?? id, address, found?.Txt, found?.Port ?? 7000);
    }
}
