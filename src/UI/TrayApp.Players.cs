using HomePodCast.Players;

namespace HomePodCast.UI;

// 「影视场景自动调整播放器」: local players' audio delay follows the HomePod while the 影视 scene streams.
internal sealed partial class TrayApp
{
    private PlayerSync? _players;

    /// <summary>Called once from the constructor, after the main window exists.</summary>
    private void InitPlayers()
    {
        _players = new PlayerSync(new PlayerScanner());
        _players.Changed += () => _ui.Post(_ => _form.ShowPlayers(), null);
        Controller.Changed += _players.Kick;
        _players.Start(PlayerInput);
    }

    /// <summary>Status of the local players while the 影视 scene streams (empty otherwise).</summary>
    public IReadOnlyList<PlayerStatus> PlayerStatuses => _players?.Statuses ?? [];

    /// <summary>How late the HomePod is heard now, as the local API reports it (0 when not streaming).</summary>
    public int VideoDelayMs => Controller.State == StreamState.Streaming
        ? LatencyTuner.SoundLagMs(Controller.EffectiveLatencyMs, Config, Controller.Capture?.ExtraLatencyMs ?? 0)
        : 0;

    private PlayerSyncInput PlayerInput() =>
        new(Config.MoviePlayerSync && Config.Scene == Scene.Movie, Controller.State, VideoDelayMs);

    /// <summary>The scene or the setting changed: adjust or restore the players now instead of at the next poll.</summary>
    public void KickPlayers() => _players?.Kick();

    public void SetMoviePlayerSync(bool on)
    {
        Config.MoviePlayerSync = on;
        Config.Save();
        Log.Info($"movie player sync {(on ? "on" : "off")}");
        KickPlayers();
        _form.ShowPlayers();
    }

    /// <summary>Puts every player back to its own delay (app exit).</summary>
    private void DisposePlayers()
    {
        if (_players == null) return;
        Controller.Changed -= _players.Kick;
        _players.Shutdown(TimeSpan.FromSeconds(2));
    }
}
