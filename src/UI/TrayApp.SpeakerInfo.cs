using HomePodCast.Net;

namespace HomePodCast.UI;

// 设置 → 音箱信息 (Pages/SpeakerInfoPanel.cs): the selected speakers' /info, read-only, cached per device. The same reads
// serve the busy check before connecting (TrayApp.Takeover.cs).
internal sealed partial class TrayApp
{
    /// <summary>The speakers' GET /info by device id (no pairing, no session: nothing on the speaker changes).</summary>
    public SpeakerInfoCache SpeakerInfo { get; } = new(SpeakerInfoReader.ReadAsync) { ReadPort = SpeakerInfoReader.ReadAsync };

    /// <summary>The last scan as found (pairs not merged): the TXT records, and the members of a stereo pair.</summary>
    public IReadOnlyList<AirPlayDevice> LastScan { get; private set; } = [];

    /// <summary>What the config selects now: the speaker, both members of a pair, or the two speakers of a multi-room group.</summary>
    public void AddManualAirPlayDevice(IWin32Window? owner)
    {
        using var dialog = new ManualAirPlayDialog();
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Device is not { } device) return;
        Config.ManualDevices.RemoveAll(d => d.Host == device.Host && d.Port == device.Port || d.DeviceId == device.DeviceId);
        Config.ManualDevices.Add(device);
        Config.Save();
        LastScan = ManualAirPlayDevice.Include(LastScan.Where(d => !(d.Address.ToString() == device.Host && d.Port == device.Port)).ToList(), Config.ManualDevices);
        _form.SetDevices(StereoPairs.Merge(LastScan));
    }

    public List<SelectedSpeaker> SelectedSpeakers() => SpeakerSelection.Of(Config, LastScan);
}
