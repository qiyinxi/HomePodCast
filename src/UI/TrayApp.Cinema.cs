using HomePodCast.Audio;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private CinemaFileSource? _cinemaSource;
    public bool CinemaStreaming => _cinemaSource != null && Controller.State == StreamState.Streaming;
    internal void StartCinema(CinemaFileSource source, IReadOnlyList<CinemaSpeaker> speakers)
    {
        CinemaMatrix.Validate(speakers);
        _wantConnected = false; // retry within the cinema group, never restart unrelated system capture on a network change
        _fx!.MicOn = false;
        _cinemaSource = source;
        var members = speakers.Select(s => new GroupMemberRef(s.DeviceId, s.Name, s.Host)).ToArray();
        var plan = new GroupPlan(GroupKind.MultiRoom, L.T("无线影院"), null, members, false, false, new Dictionary<string, int>());
        double volume = Math.Min(Config.Volume ?? 20, Config.VolumeCapPercent);
        int latency = Math.Max(Config.LatencyMs, 200);
        _connection.Post(() => Controller.StartGroup(plan, latency, volume, trigger: ConnectTrigger.Automatic, source: source));
    }
    internal void StopCinema(CinemaFileSource source)
    {
        if (!ReferenceEquals(_cinemaSource, source)) return;
        _cinemaSource = null; source.Dispose(); Disconnect();
    }
}
