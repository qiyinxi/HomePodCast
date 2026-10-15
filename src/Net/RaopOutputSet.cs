using System.Net;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

internal sealed record RaopGroupOutputOptions(string Id, string Name, IPAddress Address, int Port, RaopOptions Options)
{
    public bool Required { get; init; }
    public int TrimMs { get; init; }
    public int VolumeOffset { get; init; }
    public ChannelMode Channels { get; init; }
    public Func<CancellationToken, Task>? BeforeConnect { get; init; }
    public Action<Exception>? ActionRequired { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentNullException.ThrowIfNull(Address);
        ArgumentNullException.ThrowIfNull(Options);
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (Options.LatencyMs is not (1000 or 1500 or 2000)) throw new ArgumentOutOfRangeException(nameof(Options.LatencyMs));
        if (TrimMs is < -5000 or > 5000) throw new ArgumentOutOfRangeException(nameof(TrimMs));
        if (VolumeOffset is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(VolumeOffset));
        if (!Enum.IsDefined(Channels)) throw new ArgumentOutOfRangeException(nameof(Channels));
    }
}

internal sealed record RaopGroupOutputStatus(string Id, string Name, OutputState State, string? Error, OutputStats Stats,
    bool Required, bool Retrying, bool NeedsUserAction);

/// <summary>Independent classic RAOP sessions reading one capture history; only required failures end the group.</summary>
internal sealed class RaopOutputSet : IDisposable
{
    private sealed class Session : IDisposable
    {
        public RaopClient Client = null!;
        public RaopTimelineReader? Reader;
        public Exception? Failure;
        public readonly TaskCompletionSource<long> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnLost(string reason)
        {
            if (Client.LostException is { } ex) Interlocked.CompareExchange(ref Failure, ex, null);
            Lost.TrySetResult(reason);
        }
        public void Fail(Exception ex)
        {
            Interlocked.CompareExchange(ref Failure, ex, null);
            Lost.TrySetResult(RaopClient.FailureReason(ex));
        }
        public void Dispose()
        {
            Gate.TrySetCanceled();
            if (Client == null) return;
            Client.Lost -= OnLost;
            Client.Dispose();
        }
    }

    private sealed class Slot(RaopGroupOutputOptions options)
    {
        public readonly RaopGroupOutputOptions Options = options;
        public Session? Current;
        public string? Error;
        public bool Terminal;
        public bool ActionNotified;
        public bool Retrying;
        public Task? Worker;
    }

    private readonly Slot[] _slots;
    // Serialize master publication and every remote volume command. A connection awaiting SETUP never retains a
    // copy of the master: when it joins, it publishes the value under this same lock before its audio gate opens.
    private readonly object _commands = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenRegistration _cancel;
    private readonly Func<double> _cap;
    private readonly Func<RaopGroupOutputOptions, RaopOptions, CancellationToken, Task<RaopClient>> _connect;
    private readonly Func<TimeSpan, CancellationToken, Task> _retry;
    private readonly Func<long> _epoch;
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _prepareDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GroupMemberException? _prepareFailure;
    private TimelineBuffer? _timeline;
    private int _delayMs, _disposed;
    private double _volume;
    private bool _volumePublished;

    private RaopOutputSet(IReadOnlyList<RaopGroupOutputOptions> options, Func<double> cap, CancellationToken ct,
        Func<RaopGroupOutputOptions, RaopOptions, CancellationToken, Task<RaopClient>>? connect,
        Func<TimeSpan, CancellationToken, Task>? retry, Func<long>? epoch)
    {
        _slots = options.Select(o => new Slot(o)).ToArray();
        _cap = cap;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connect = connect ?? ((o, transport, token) => RaopClient.ConnectAsync(o.Address, o.Port, transport, token));
        _retry = retry ?? Task.Delay;
        _epoch = epoch ?? (() => MediaClock.Now);
        // Also close prepared sessions if their owner cancels before calling StartAsync.
        _cancel = ct.Register(() => _ = Task.Run(Dispose));
    }

    public int MinimumDelayMs => _slots.Max(s => s.Options.Options.LatencyMs + Math.Max(0, -s.Options.TrimMs));
    public int HistoryMs(int groupDelayMs) => checked(groupDelayMs + _slots.Max(s => Math.Max(0, s.Options.TrimMs)) + 2000);
    public Task<string> Lost => _lost.Task;
    public string? LostReason { get; private set; }
    public string? LostOutputName { get; private set; }
    public bool LostRequiresUserAction { get; private set; }
    public double MasterVolume { get { lock (_commands) return _volume; } }

    public IReadOnlyList<RaopGroupOutputStatus> Status
    {
        get
        {
            lock (_commands)
                return _slots.Select(s => new RaopGroupOutputStatus(s.Options.Id, s.Options.Name,
                    _disposed != 0 ? OutputState.Stopped : s.Current is not { } current ? OutputState.Lost
                        : current.Lost.Task.IsCompleted ? OutputState.Lost
                        : _timeline == null ? OutputState.Preparing : OutputState.Streaming,
                    s.Error ?? s.Current?.Client.LostReason,
                    s.Current is { } session ? new OutputStats(session.Client.PacketsSent, session.Client.RetransmitRequests,
                        session.Client.Retransmitted, session.Client.RetransmitMisses) : default,
                    s.Options.Required, s.Retrying, s.Terminal)).ToArray();
        }
    }

    public static async Task<RaopOutputSet> PrepareAsync(IReadOnlyList<RaopGroupOutputOptions> options, Func<double> cap,
        CancellationToken ct,
        Func<RaopGroupOutputOptions, RaopOptions, CancellationToken, Task<RaopClient>>? connect = null,
        Func<TimeSpan, CancellationToken, Task>? retry = null, Func<long>? epoch = null)
    {
        ArgumentNullException.ThrowIfNull(cap);
        if (options.Count == 0) throw new ArgumentException("No RAOP outputs", nameof(options));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            option.Validate();
            if (!ids.Add(option.Id)) throw new ArgumentException("Duplicate RAOP output", nameof(options));
        }
        var set = new RaopOutputSet(options, cap, ct, connect, retry, epoch);
        try
        {
            await Task.WhenAll(set._slots.Select(set.PrepareOne));
            ct.ThrowIfCancellationRequested();
            if (set._prepareFailure is { } failure) throw failure;
            lock (set._commands)
            {
                set._stop.Token.ThrowIfCancellationRequested();
                foreach (var slot in set._slots) slot.Worker = Task.Run(() => set.RunSlot(slot));
            }
            return set;
        }
        catch { set.Dispose(); throw; }
        finally { set._prepareDone.TrySetResult(); }
    }

    private async Task PrepareOne(Slot slot)
    {
        try { await ConnectOne(slot); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_commands)
            {
                slot.Error = RaopClient.FailureReason(ex);
                slot.Terminal = RequiresUserAction(ex, slot.Error);
                slot.Retrying = !slot.Terminal && !slot.Options.Required;
                if (slot.Options.Required)
                    _prepareFailure ??= new GroupMemberException(slot.Options.Name, RequiredFailure(ex));
            }
            if (slot.Options.Required) _stop.Cancel();
            NotifyActionRequired(slot, ex);
        }
    }

    private async Task ConnectOne(Slot slot)
    {
        var session = new Session();
        try
        {
            if (slot.Options.BeforeConnect is { } preflight) await preflight(_stop.Token);
            _stop.Token.ThrowIfCancellationRequested();
            // Preserve every advertised TXT/password/encryption/latency choice. Only group-owned audio and initial
            // silence replace the corresponding single-output settings.
            var options = slot.Options.Options with
            {
                VolumePercent = 0, VolumeCapPercent = Cap(), GroupStart = session.Gate.Task,
                ReadAudio = null,
                ReadTimedAudio = (ticks, pcm) => session.Reader?.Read(ticks, pcm),
            };
            session.Client = await _connect(slot.Options, options, _stop.Token);
            session.Client.Lost += session.OnLost;
            lock (_commands)
            {
                _stop.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (session.Lost.Task.IsCompleted)
                    throw session.Client.LostException ?? new IOException(session.Lost.Task.Result);
                if (_timeline != null)
                {
                    BindReader(slot, session);
                    PublishVolume(slot, session);
                }
                long epoch = _timeline == null ? 0 : _epoch();
                slot.Current = session;
                slot.Error = null;
                slot.Terminal = slot.Retrying = false;
                if (_timeline != null) session.Gate.TrySetResult(epoch);
            }
        }
        catch { session.Dispose(); throw; }
    }

    public Task StartAsync(TimelineBuffer timeline, int groupDelayMs, double volume, CancellationToken ct)
    {
        List<(Slot Slot, Exception Error)> actions = [];
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_commands)
            {
                _stop.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (_timeline != null) throw new InvalidOperationException("RAOP outputs already started");
                if (timeline.SampleRate != RaopAudio.SampleRate) throw new ArgumentException("RAOP requires a 44100 Hz timeline", nameof(timeline));
                if (groupDelayMs < MinimumDelayMs) throw new ArgumentOutOfRangeException(nameof(groupDelayMs));
                _timeline = timeline;
                _delayMs = groupDelayMs;
                if (!_volumePublished) _volume = NormalizeVolume(volume);
                _volumePublished = true;
                foreach (var slot in _slots)
                {
                    if (slot.Current is not { } session) continue;
                    try
                    {
                        if (session.Lost.Task.IsCompleted)
                            throw session.Client.LostException ?? new IOException(session.Lost.Task.Result);
                        BindReader(slot, session);
                        PublishVolume(slot, session);
                    }
                    catch (Exception ex)
                    {
                        slot.Current = null;
                        slot.Error = RaopClient.FailureReason(ex);
                        slot.Terminal = RequiresUserAction(ex, slot.Error);
                        session.Fail(ex);
                        actions.Add((slot, ex));
                        session.Dispose();
                        if (slot.Options.Required) throw new GroupMemberException(slot.Options.Name, RequiredFailure(ex));
                    }
                }
                ct.ThrowIfCancellationRequested();
                // No prepared leg sends until every surviving leg has received the current volume safely.
                long epoch = _epoch();
                foreach (var slot in _slots) slot.Current?.Gate.TrySetResult(epoch);
            }
            return Task.CompletedTask;
        }
        catch { Dispose(); throw; }
        finally { foreach (var (slot, ex) in actions) NotifyActionRequired(slot, ex); }
    }

    private void BindReader(Slot slot, Session session) => session.Reader = new RaopTimelineReader(
        _timeline!, _delayMs, slot.Options.TrimMs, slot.Options.Options.LatencyMs, slot.Options.Channels);

    private static double NormalizeVolume(double volume) => double.IsNaN(volume) ? 0 : Math.Clamp(volume, 0, 100);
    private double Cap() => NormalizeVolume(_cap());

    private void PublishVolume(Slot slot, Session session)
    {
        double cap = Cap();
        session.Client.VolumeCapPercent = cap;
        double master = Math.Min(_volume, cap);
        session.Client.SetVolume(master <= 0 ? 0 : Math.Clamp(master + slot.Options.VolumeOffset, 0, cap));
    }

    public void SetVolume(double volume)
    {
        List<(Slot Slot, Exception Error)> actions = [];
        lock (_commands)
        {
            if (_disposed != 0) return;
            _volume = NormalizeVolume(volume);
            _volumePublished = true;
            foreach (var slot in _slots)
            {
                if (slot.Current is not { } session) continue;
                try { PublishVolume(slot, session); }
                catch (Exception ex)
                {
                    // Feed the same loss path as transport failures; recovery takes its volume under this lock.
                    slot.Error = RaopClient.FailureReason(ex);
                    slot.Terminal = RequiresUserAction(ex, slot.Error);
                    session.Fail(ex);
                    actions.Add((slot, ex));
                }
            }
        }
        foreach (var (slot, ex) in actions) NotifyActionRequired(slot, ex);
    }

    private async Task RunSlot(Slot slot)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Session? current;
                lock (_commands) current = slot.Current;
                if (current != null)
                {
                    string reason = await current.Lost.Task.WaitAsync(_stop.Token);
                    lock (_commands)
                    {
                        if (ReferenceEquals(slot.Current, current)) slot.Current = null;
                        slot.Error = reason;
                        slot.Terminal |= RequiresUserAction(current.Failure ?? current.Client.LostException, reason);
                        if (slot.Options.Required) SignalRequiredLoss(slot, reason);
                    }
                    current.Dispose();
                    if (slot.Options.Required) _stop.Cancel();
                    NotifyActionRequired(slot, current.Failure ?? current.Client.LostException);
                    if (slot.Options.Required) return;
                }
                lock (_commands)
                {
                    if (slot.Terminal) return;
                    slot.Retrying = true;
                }
                await _retry(TimeSpan.FromSeconds(2), _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                try { await ConnectOne(slot); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (_commands)
                    {
                        slot.Error = RaopClient.FailureReason(ex);
                        slot.Terminal = RequiresUserAction(ex, slot.Error);
                        slot.Retrying = !slot.Terminal;
                    }
                    Log.Warn($"RAOP output {Log.Name(slot.Options.Name)}: {ex.Message}");
                    NotifyActionRequired(slot, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            Session? current;
            lock (_commands)
            {
                current = slot.Current;
                slot.Current = null;
                slot.Retrying = false;
            }
            current?.Dispose();
        }
    }

    private void SignalRequiredLoss(Slot slot, string reason)
    {
        if (_lost.Task.IsCompleted || _disposed != 0) return;
        LostReason = reason;
        LostOutputName = slot.Options.Name;
        LostRequiresUserAction = slot.Terminal;
        _lost.TrySetResult(L.F("{0}：{1}", slot.Options.Name, reason));
    }

    private static bool RequiresUserAction(Exception? ex, string reason) =>
        reason == RaopClient.ClosedByReceiver || ex is RaopUnsupportedException or RaopPasswordRequiredException
            or RaopBusyException or RaopRefusedException or ArgumentException;

    // Keep an orderly RAOP close typed at this transport boundary. The shared RTSP exception text can also
    // occur on AirPlay 2; GroupRunner must not mistake that path for a RAOP takeover and change its retry rules.
    private static Exception RequiredFailure(Exception error) => RaopClient.FailureReason(error) == RaopClient.ClosedByReceiver
        ? new RaopRefusedException(RaopClient.ClosedByReceiver) : error;

    private void NotifyActionRequired(Slot slot, Exception? ex)
    {
        if (ex == null) return;
        string reason = RaopClient.FailureReason(ex);
        if (!RequiresUserAction(ex, reason) || reason == RaopClient.ClosedByReceiver) return;
        Action<Exception>? notify;
        lock (_commands)
        {
            // A takeover always stays paused. Asking to reclaim it from here would fight the new sender.
            if (!slot.Terminal || slot.ActionNotified || slot.Error == RaopClient.ClosedByReceiver) return;
            slot.ActionNotified = true;
            notify = slot.Options.ActionRequired;
        }
        try { notify?.Invoke(ex); }
        catch (Exception consumer) { Log.Warn($"RAOP output action {Log.Name(slot.Options.Name)}: {consumer.Message}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        Session[] sessions;
        Task[] workers;
        lock (_commands)
        {
            sessions = _slots.Select(s => s.Current).OfType<Session>().ToArray();
            foreach (var slot in _slots) slot.Current = null;
            workers = _slots.Select(s => s.Worker ?? Task.CompletedTask).ToArray();
        }
        foreach (var session in sessions) session.Dispose();
        _ = Task.WhenAll(workers.Append(_prepareDone.Task)).ContinueWith(t =>
        {
            _ = t.Exception;
            _cancel.Dispose();
            _stop.Dispose();
        }, TaskScheduler.Default);
    }
}
