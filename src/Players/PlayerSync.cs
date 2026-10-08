namespace HomePodCast.Players;

/// <summary>What PlayerSync needs to know about the app, read on every step.</summary>
/// <param name="Enabled">The setting is on and the 影视 scene is selected.</param>
/// <param name="State">The stream's state.</param>
/// <param name="VideoDelayMs">How late the HomePod is heard (only meaningful while streaming).</param>
internal readonly record struct PlayerSyncInput(bool Enabled, StreamState State, int VideoDelayMs);

/// <summary>
/// 「影视场景自动调整播放器」: while the 影视 scene streams, sets the audio delay of every supported running
/// player to −videoDelayMs so its picture lines up with the HomePod, and puts each player's own value back
/// when the scene changes, the setting goes off, the stream stops or the app exits.
/// <list type="bullet">
/// <item>Players are only touched while enabled and streaming; a short reconnect (Connecting/Retrying)
/// keeps the values for <see cref="RetryGrace"/>, then they are restored as well.</item>
/// <item>A new target (latency or capture path changed) is applied to every player at once.</item>
/// <item>A player back at its own value (VLC opens every item at its default delay) or on a new item is
/// set again; any other value means someone changed it in the player, and it is left alone.</item>
/// <item>A player that exits is forgotten; there is nothing to restore.</item>
/// </list>
/// <see cref="StepAsync"/> is the whole state machine (tests drive it); <see cref="Start"/> runs it every
/// <see cref="PollInterval"/> on the thread pool.
/// </summary>
internal sealed class PlayerSync : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetryGrace = TimeSpan.FromSeconds(15);

    private readonly IPlayerScanner _scanner;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, Tracked> _tracked = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Func<PlayerSyncInput>? _input;
    private System.Threading.Timer? _timer;
    private DateTime _lastStreaming = DateTime.MinValue;
    private volatile bool _disposed;

    private sealed class Tracked(IPlayerEndpoint endpoint, int original, int applied, string? item)
    {
        public IPlayerEndpoint Endpoint { get; set; } = endpoint;
        public int Original { get; } = original;
        public int Applied { get; set; } = applied;
        public string? Item { get; set; } = item;
        public bool UserChanged { get; set; }
    }

    public PlayerSync(IPlayerScanner scanner, Func<DateTime>? now = null)
    {
        _scanner = scanner;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>One line per running player (adjusted, waiting, or why not); empty while not active.</summary>
    public IReadOnlyList<PlayerStatus> Statuses { get; private set; } = [];

    /// <summary>The delay set on the players (ms, negative); null while nothing is applied.</summary>
    public int? TargetMs { get; private set; }

    /// <summary>Players currently holding a value set by us.</summary>
    public int TrackedCount
    {
        get { lock (_tracked) return _tracked.Count; }
    }

    /// <summary>Raised (on the thread pool) after Statuses or TargetMs changed.</summary>
    public event Action? Changed;

    // ---------------------------------------------------------------- loop

    /// <summary>Poll with <paramref name="input"/> every <see cref="PollInterval"/>. A step that has nothing
    /// to do (not enabled, nothing to restore) neither scans nor touches any player.</summary>
    public void Start(Func<PlayerSyncInput> input)
    {
        _input = input;
        _timer = new System.Threading.Timer(_ => Kick(), null, TimeSpan.Zero, PollInterval);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit; // exits that skip TrayApp.Quit (log off, Environment.Exit)
    }

    private void OnProcessExit(object? sender, EventArgs e) => Shutdown(TimeSpan.FromSeconds(1));

    /// <summary>Run a step now (the scene, the setting or the stream changed); skipped if one is running.</summary>
    public void Kick()
    {
        if (_disposed || _input is not { } input) return;
        _ = Task.Run(async () =>
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (!_disposed) await StepAsync(input(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"players: {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Stop polling and put every player back (app exit); waits at most <paramref name="timeout"/>.</summary>
    public void Shutdown(TimeSpan timeout)
    {
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _timer?.Dispose();
        _timer = null;
        if (TrackedCount == 0) return;
        try
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(timeout);
                if (!await _gate.WaitAsync(timeout, cts.Token).ConfigureAwait(false)) return;
                try { await RestoreAllAsync(cts.Token).ConfigureAwait(false); }
                finally { _gate.Release(); }
            }).Wait(timeout + TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Log.Warn($"players: restore on exit: {ex.Message}");
        }
    }

    public void Dispose() => Shutdown(TimeSpan.FromSeconds(2));

    // ---------------------------------------------------------------- state machine

    private enum Mode { Off, Apply, Hold }

    private Mode ModeFor(PlayerSyncInput input)
    {
        if (!input.Enabled || input.State == StreamState.Idle) return Mode.Off;
        if (input.State == StreamState.Streaming)
        {
            _lastStreaming = _now();
            return Mode.Apply;
        }
        // Connecting / Retrying: a reconnect (latency change, Wi-Fi hiccup) keeps the values for a while.
        return TrackedCount > 0 && _now() - _lastStreaming < RetryGrace ? Mode.Hold : Mode.Off;
    }

    public async Task StepAsync(PlayerSyncInput input, CancellationToken ct)
    {
        switch (ModeFor(input))
        {
            case Mode.Off:
                if (TrackedCount > 0) await RestoreAllAsync(ct).ConfigureAwait(false);
                Publish([], null);
                return;
            case Mode.Hold:
                return; // keep everything as it is until the stream is back or the grace runs out
        }

        int target = PlayerDelay.TargetMs(input.VideoDelayMs);
        var scan = await _scanner.ScanAsync(ct).ConfigureAwait(false);
        var statuses = new List<PlayerStatus>();

        lock (_tracked)
        {
            var present = scan.Endpoints.Select(e => e.Key).ToHashSet();
            foreach (var key in _tracked.Keys.Where(k => !present.Contains(k)).ToList())
            {
                Log.Info($"players: {_tracked[key].Endpoint.Name} ({key}) went away");
                _tracked.Remove(key); // exited, or its interface closed: nothing to restore
            }
        }

        foreach (var endpoint in scan.Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            statuses.Add(await ApplyAsync(endpoint, target, ct).ConfigureAwait(false));
        }
        foreach (var note in scan.Notes)
        {
            statuses.Add(new PlayerStatus(note.Kind, note.Name, note.Problem switch
            {
                PlayerProblem.NoPassword => PlayerState.NoPassword,
                PlayerProblem.LoginFailed => PlayerState.LoginFailed,
                PlayerProblem.Manual => PlayerState.Manual,
                _ => PlayerState.NoInterface,
            }, note.Problem == PlayerProblem.Manual ? target : null));
        }
        Publish(statuses, target);
    }

    private async Task<PlayerStatus> ApplyAsync(IPlayerEndpoint endpoint, int target, CancellationToken ct)
    {
        PlayerStatus Status(PlayerState state, int? ms = null) => new(endpoint.Kind, endpoint.Name, state, ms);
        try
        {
            var reading = await endpoint.ReadAsync(ct).ConfigureAwait(false);
            Tracked? t;
            lock (_tracked) _tracked.TryGetValue(endpoint.Key, out t);

            if (t == null)
            {
                if (reading.DelayMs is not { } original) return Status(PlayerState.Waiting);
                if (!PlayerDelay.Same(original, target)) await endpoint.WriteAsync(target, ct).ConfigureAwait(false);
                lock (_tracked) _tracked[endpoint.Key] = new Tracked(endpoint, original, target, reading.Item);
                Log.Info($"players: {endpoint.Name} ({endpoint.Key}) audio delay {original} -> {target} ms");
                return Status(PlayerState.Applied, target);
            }

            t.Endpoint = endpoint;
            if (reading.DelayMs is not { } now) return Status(PlayerState.Waiting);
            bool newItem = reading.Item != null && t.Item != null && reading.Item != t.Item;
            if (reading.Item != null) t.Item = reading.Item;

            bool reverted = !t.UserChanged && PlayerDelay.Same(now, t.Original) && !PlayerDelay.Same(now, t.Applied);
            if (t.Applied != target || newItem || reverted)
            {
                if (!PlayerDelay.Same(now, target)) await endpoint.WriteAsync(target, ct).ConfigureAwait(false);
                if (t.Applied != target) Log.Info($"players: {endpoint.Name} ({endpoint.Key}) audio delay {now} -> {target} ms");
                t.Applied = target;
                t.UserChanged = false;
                return Status(PlayerState.Applied, target);
            }
            if (PlayerDelay.Same(now, t.Applied))
            {
                t.UserChanged = false;
                return Status(PlayerState.Applied, t.Applied);
            }
            if (!t.UserChanged) Log.Info($"players: {endpoint.Name} ({endpoint.Key}) changed by hand to {now} ms; leaving it");
            t.UserChanged = true;
            return Status(PlayerState.UserChanged, now);
        }
        catch (PlayerAccessException ex)
        {
            return Status(ex.Problem switch
            {
                PlayerProblem.NoPassword => PlayerState.NoPassword,
                PlayerProblem.LoginFailed => PlayerState.LoginFailed,
                _ => PlayerState.NoInterface,
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"players: {endpoint.Name} ({endpoint.Key}): {ex.Message}");
            return Status(PlayerState.Failed);
        }
    }

    /// <summary>Put every player we changed back to its own value and forget them.</summary>
    public async Task RestoreAllAsync(CancellationToken ct)
    {
        List<Tracked> all;
        lock (_tracked)
        {
            all = [.. _tracked.Values];
            _tracked.Clear();
        }
        foreach (var t in all)
        {
            try
            {
                await t.Endpoint.WriteAsync(t.Original, ct).ConfigureAwait(false);
                Log.Info($"players: {t.Endpoint.Name} ({t.Endpoint.Key}) audio delay restored to {t.Original} ms");
            }
            catch (Exception ex)
            {
                Log.Warn($"players: restore {t.Endpoint.Name} ({t.Endpoint.Key}): {ex.Message}"); // most likely gone
            }
        }
    }

    private void Publish(IReadOnlyList<PlayerStatus> statuses, int? target)
    {
        if (target == TargetMs && statuses.SequenceEqual(Statuses)) return;
        Statuses = statuses;
        TargetMs = target;
        Changed?.Invoke();
    }
}
