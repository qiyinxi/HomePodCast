using System.Net;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>
/// One speaker of the selection: its device id, name, last known address, and the TXT record, port, model and services of
/// the last scan (or, offline, of the receiver's saved preferences).
/// </summary>
public sealed record SelectedSpeaker(string DeviceId, string Name, IPAddress? Address, IReadOnlyDictionary<string, string>? Txt, int Port = 7000)
{
    /// <summary>The advertised model: a RAOP-only receiver has it in TXT am, not model.</summary>
    public string Model { get; init; } = "";

    public bool HasAirPlay { get; init; } = true;

    public int? RaopPort { get; init; }
}

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
                ? members.Select(d => new SelectedSpeaker(d.DeviceId, d.Name, d.Address, d.Txt, d.Port)
                    { Model = d.Model, HasAirPlay = d.HasAirPlay, RaopPort = d.RaopPort }).ToList()
                : [new SelectedSpeaker(id, cfg.DeviceName ?? tsid, null, null)]; // not found: shown as unreadable
        }
        var list = new List<SelectedSpeaker> { One(id, cfg.DeviceName, cfg.Host, scan, cfg.ReceiverSettings) };
        if (GroupPlan.FromConfig(cfg) is { Kind: GroupKind.MultiRoom } && cfg.MultiRoomDeviceId is { } extra)
            list.Add(One(extra, cfg.MultiRoomDeviceName, cfg.MultiRoomHost, scan, cfg.ReceiverSettings));
        return list;
    }

    /// <summary>
    /// A receiver the last scan didn't find (offline, or no scan yet) is described as its saved preferences last saw it,
    /// as 连接方式 does (TrayApp.ReceiverLookup): its port, model, TXT record and services. Without them, /info was read
    /// from port 7000 and the kind came out unknown (2026-10-15: a third-party AirPlay 1 receiver saved with port 7001,
    /// CI run 38645472098). Only receivers other than HomePods have saved preferences (TrayApp.SelectDevice).
    /// </summary>
    private static SelectedSpeaker One(string id, string? name, string? host, IReadOnlyList<AirPlayDevice> scan,
        IReadOnlyDictionary<string, ReceiverPreferences> receivers)
    {
        var scanned = scan.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id));
        var found = scanned ?? receivers.GetValueOrDefault(RaopPassword.Key(id))?.Device(id);
        var address = IPAddress.TryParse(host, out var saved) && !saved.Equals(IPAddress.None) ? saved : found?.Address;
        return new SelectedSpeaker(id, scanned?.Name ?? name ?? found?.Name ?? id, address, found?.Txt, found?.Port ?? 7000)
            { Model = found?.Model ?? "", HasAirPlay = found?.HasAirPlay ?? true, RaopPort = found?.RaopPort };
    }
}
