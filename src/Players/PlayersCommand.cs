namespace HomePodCast.Players;

/// <summary>
/// <c>HomePodCast players [--apply MS [--seconds N]]</c>: the players the 影视 scene would adjust, and (--apply)
/// a dry run without a speaker: hold their audio delay at −MS for --seconds (default 6), polling like the app,
/// then put their own value back. Uses the VLC password typed in 设置 like the app does. The output says which
/// password VLC accepted, never the password.
/// </summary>
internal static class PlayersCommand
{
    /// <param name="option">The value after a command-line option, or null.</param>
    public static int Run(Func<string, string?> option)
    {
        var config = AppConfig.Load();
        var scanner = new PlayerScanner(VlcPassword.From(() => config));
        int? lag = option("--apply") is { } apply ? int.Parse(apply) : null;
        var duration = TimeSpan.FromSeconds(int.Parse(option("--seconds") ?? "6"));
        Run(scanner, lag, duration, PlayerSync.PollInterval);
        return 0;
    }

    internal static void Run(IPlayerScanner scanner, int? lagMs, TimeSpan duration, TimeSpan poll)
    {
        var scan = scanner.ScanAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var e in scan.Endpoints)
        {
            string reading;
            try { reading = e.ReadAsync(CancellationToken.None).GetAwaiter().GetResult().ToString(); }
            catch (Exception ex) { reading = ex.Message; }
            Log.Info($"{e.Name} {e.Key}: {reading}{PasswordNote(e.PasswordInUse)}");
        }
        foreach (var n in scan.Notes) Log.Info($"{n.Name}: {n.Problem}");
        if (scan.Endpoints.Count == 0 && scan.Notes.Count == 0) Log.Info("no supported player running");
        if (lagMs is not { } lag) return;

        var until = DateTime.UtcNow + duration;
        var sync = new PlayerSync(scanner);
        var input = new PlayerSyncInput(true, StreamState.Streaming, lag);
        while (true)
        {
            sync.StepAsync(input, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var s in sync.Statuses) Log.Info(PlayerText.Line(s) + PasswordNote(s.Password));
            if (DateTime.UtcNow >= until) break;
            Thread.Sleep(poll);
        }
        sync.StepAsync(input with { Enabled = false }, CancellationToken.None).GetAwaiter().GetResult();
        Log.Info("restored");
    }

    private static string PasswordNote(PasswordSource? source) => source switch
    {
        PasswordSource.Player => " (password from the player's settings)",
        PasswordSource.Manual => " (password entered in HomePodCast)",
        _ => "",
    };
}
