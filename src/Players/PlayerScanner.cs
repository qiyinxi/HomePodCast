namespace HomePodCast.Players;

/// <summary>
/// The running players: mpv (JSON IPC pipe) and VLC (web interface) become endpoints when their interface
/// is on; PotPlayer, MPC-HC and MPC-BE have no interface for the audio delay (MPC's web interface only
/// steps it by 10 ms and cannot report it) and are listed for a manual setting.
/// </summary>
internal sealed class PlayerScanner : IPlayerScanner
{
    private readonly MpvFinder _mpv = new();

    public async Task<PlayerScan> ScanAsync(CancellationToken ct)
    {
        var mpv = new Dictionary<int, string>();
        var vlc = new List<int>();
        var manual = new SortedDictionary<PlayerKind, string>();
        foreach (var p in Audio.ProcessTree.Snapshot().Values)
        {
            if (!PlayerProcesses.TryIdentify(p.Key, out var kind, out var name)) continue;
            switch (kind)
            {
                case PlayerKind.Mpv: mpv[(int)p.Pid] = name; break;
                case PlayerKind.Vlc: vlc.Add((int)p.Pid); break;
                default: manual[kind] = name; break;
            }
        }

        var (mpvEndpoints, mpvNotes) = await _mpv.FindAsync(mpv, ct).ConfigureAwait(false);
        var (vlcEndpoints, vlcNotes) = VlcFinder.Find(vlc);
        var notes = mpvNotes.Concat(vlcNotes).ToList();
        notes.AddRange(manual.Select(m => new PlayerNote(m.Key, m.Value, PlayerProblem.Manual)));
        return new PlayerScan([.. mpvEndpoints, .. vlcEndpoints], notes);
    }
}
