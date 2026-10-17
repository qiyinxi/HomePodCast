using HomePodCast.Audio;

namespace HomePodCast.Net;

internal sealed record ReceiverGroupTarget(AirPlayDevice Device, ReceiverPreferences? Settings);
internal sealed record ReceiverGroupOutputOptions(string Id, string Name,
    Func<CancellationToken, Task<ReceiverGroupTarget>> Resolve, ReceiverConnectOptions Connection)
{
    public bool Required { get; init; } = true;
    public int TrimMs { get; init; }
    public int VolumeOffset { get; init; }
    public ChannelMode Channels { get; init; }
    public Func<ReceiverGroupTarget, CancellationToken, Task>? BeforeConnect { get; init; }
    public Action<Exception>? ActionRequired { get; init; }
    public Action<ReceiverProtocol, int?>? Connected { get; init; }
    /// <summary>Apple independent outputs keep their original prepare path, before third-party protocol trials.</summary>
    public Func<ReceiverGroupTarget, CancellationToken, Task<IPreparedReceiverSession>>? PrepareApple { get; init; }
    public int InitialLatencyMs { get; init; } = 2000;
    public Func<string?>? Password { get; init; }
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentNullException.ThrowIfNull(Resolve);
        ArgumentNullException.ThrowIfNull(Connection);
        if (TrimMs is < -5000 or > 5000 || VolumeOffset is < -100 or > 100 || !Enum.IsDefined(Channels))
            throw new ArgumentOutOfRangeException(nameof(TrimMs));
    }
}
internal sealed record ReceiverGroupOutputStatus(string Id, string Name, OutputState State, string? Error, bool Required,
    bool Retrying, bool NeedsUserAction, ReceiverProtocol? Protocol, bool UsesPtp, int? LatencyMs, long PacketsSent);

/// <summary>Non-Apple receiver transports share one source history, with independent optional recovery.</summary>
internal sealed class ReceiverOutputSet : IDisposable
{
    private sealed class Session : IReceiverAudioSource
    {
        private sealed record AudioState(double Applied, bool Ready);
        private readonly Func<(double Percent, bool Muted)> _desired;
        private readonly object _audio = new();
        private readonly float[] _packet = new float[704];
        private AudioState _state = new(0, false);
        private ReceiverTimelineSource? _reader;
        public IPreparedReceiverSession Transport = null!;
        public readonly SemaphoreSlim Commands = new(1);
        public readonly TaskCompletionSource<string> Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure;
        public int Publishing, Dirty;
        public bool Started;
        private int _cleanup;
        public bool Ready => Volatile.Read(ref _state).Ready;
        public Session(Func<(double Percent, bool Muted)> desired) => _desired = desired;
        public void Bind(TimelineBuffer timeline, int delay, ReceiverGroupOutputOptions options)
        {
            lock (_audio) _reader = new(timeline, delay, options.TrimMs, Transport.LatencyMs, options.Channels);
        }
        public bool Read(long ticks, Span<float> stereo)
        {
            bool complete;
            lock (_audio)
            {
                if (_reader == null) { stereo.Clear(); return false; }
                complete = _reader.Read(ticks, stereo);
            }
            var state = Volatile.Read(ref _state);
            var desired = _desired();
            double gain = !state.Ready || desired.Muted || state.Applied <= 0 || desired.Percent <= 0 ? 0
                : Math.Min(1, Math.Pow(10, (AirPlayClient.PercentToDb(desired.Percent) - AirPlayClient.PercentToDb(state.Applied)) / 20));
            if (gain != 1) for (int i = 0; i < stereo.Length; i++) stereo[i] *= (float)gain;
            return complete;
        }
        public bool ReadPacket(long ticks) => Read(ticks, _packet);
        public void ReadTarget(int index, Span<float> stereo)
        {
            if (index != 0 || stereo.Length != _packet.Length) throw new ArgumentOutOfRangeException(nameof(index));
            _packet.CopyTo(stereo);
        }
        public void SkipPackets(int packets) { lock (_audio) _reader?.SkipPackets(packets); }
        public void Publish(double applied, bool ready) => Volatile.Write(ref _state, new(applied, ready));
        public void Fail(Exception error)
        {
            var remote = Transport?.LostException;
            error = ReceiverGroupFailures.RequiresUserAction(remote) ? remote! : ReceiverGroupFailures.Normalize(error);
            Interlocked.CompareExchange(ref Failure, error, null);
            Stop(); Failed.TrySetResult(error.Message);
        }
        public void Stop() { Publish(0, false); Transport?.StopSending(); }
        public void Cleanup()
        {
            Stop();
            if (Interlocked.Exchange(ref _cleanup, 1) == 0 && Transport != null) _ = Task.Run(Transport.Dispose);
        }
        public void Dispose() => Stop();
    }
    private sealed class Slot(ReceiverGroupOutputOptions options)
    {
        public readonly ReceiverGroupOutputOptions Options = options;
        public Session? Current;
        public Exception? Failure;
        public bool Terminal, Retrying, ActionNotified;
        public Task? Worker;
    }
    private readonly Slot[] _slots;
    private readonly object _commands = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenRegistration _cancel;
    private readonly PtpClockPool _clocks = new();
    private readonly Func<double> _cap;
    private readonly Func<(double? Percent, bool Muted)>? _liveVolume;
    private readonly Func<TimeSpan, CancellationToken, Task> _retry;
    private readonly Func<long> _epoch;
    private readonly ReceiverGroupConnections.Connector? _connect;
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimelineBuffer? _timeline;
    private int _delay, _disposed;
    private long _version;
    private double _volume;
    private bool _muted = true, _volumePublished;
    private GroupMemberException? _prepareFailure;

    private ReceiverOutputSet(IReadOnlyList<ReceiverGroupOutputOptions> options, Func<double> cap, CancellationToken ct,
        ReceiverGroupConnections.Connector? connect, Func<TimeSpan, CancellationToken, Task>? retry,
        Func<long>? epoch, Func<(double? Percent, bool Muted)>? liveVolume)
    {
        _slots = options.Select(o => new Slot(o)).ToArray(); _cap = cap;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct); _connect = connect;
        _retry = retry ?? Task.Delay; _epoch = epoch ?? (() => MediaClock.Now); _liveVolume = liveVolume;
        _cancel = ct.Register(() => _ = Task.Run(Dispose));
    }
    public Task<string> Lost => _lost.Task;
    public string? LostReason { get; private set; }
    public string? LostOutputName { get; private set; }
    public Exception? LostException { get; private set; }
    public bool LostRequiresUserAction { get; private set; }
    public bool AllTerminal { get { lock (_commands) return _slots.All(s => s.Terminal && s.Current == null); } }
    internal (string Name, Exception Error)? FirstTerminalFailure
    {
        get { lock (_commands) { var slot = _slots.FirstOrDefault(s => s.Terminal && s.Failure != null); return slot == null ? null : (slot.Options.Name, slot.Failure!); } }
    }
    public int MinimumDelayMs => _slots.Max(s => (s.Current?.Transport.LatencyMs ?? s.Options.InitialLatencyMs) + Math.Max(0, -s.Options.TrimMs));
    public int HistoryMs(int delay) => checked(delay + _slots.Max(s => Math.Max(0, s.Options.TrimMs)) + 2000);
    internal (string Name, Exception Error)? RequiredActionFailure
    {
        get
        {
            if (LostRequiresUserAction && LostException is { } lost) return (LostOutputName ?? "AirPlay", lost);
            foreach (var slot in _slots)
            {
                if (!slot.Options.Required) continue;
                var current = Volatile.Read(ref slot.Current);
                var error = Volatile.Read(ref slot.Failure)
                    ?? (current == null ? null : Volatile.Read(ref current.Failure) ?? current.Transport.LostException);
                if (ReceiverGroupFailures.RequiresUserAction(error)) return (slot.Options.Name, error!);
            }
            return null;
        }
    }
    public IReadOnlyList<ReceiverGroupOutputStatus> Status
    {
        get
        {
            lock (_commands) return _slots.Select(s => new ReceiverGroupOutputStatus(s.Options.Id, s.Options.Name,
                _disposed != 0 ? OutputState.Stopped : s.Current == null || s.Current.Transport.Lost.IsCompleted ? OutputState.Lost
                    : s.Current.Ready ? OutputState.Streaming : OutputState.Preparing,
                (s.Failure ?? s.Current?.Transport.LostException)?.Message, s.Options.Required, s.Retrying, s.Terminal,
                s.Current?.Transport.Protocol, s.Current?.Transport.UsesPtp ?? false, s.Current?.Transport.LatencyMs,
                s.Current?.Transport.PacketsSent ?? 0)).ToArray();
        }
    }

    public static async Task<ReceiverOutputSet> PrepareAsync(IReadOnlyList<ReceiverGroupOutputOptions> options, Func<double> cap,
        CancellationToken ct, ReceiverGroupConnections.Connector? connect = null,
        Func<TimeSpan, CancellationToken, Task>? retry = null, Func<long>? epoch = null,
        Func<(double? Percent, bool Muted)>? liveVolume = null)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(cap);
        if (options.Count == 0) throw new ArgumentException("No third-party outputs", nameof(options));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            option.Validate();
            if (!ids.Add(option.Id.Replace(":", "").Replace("-", ""))) throw new ArgumentException("Duplicate receiver", nameof(options));
        }
        ct.ThrowIfCancellationRequested();
        var set = new ReceiverOutputSet(options, cap, ct, connect, retry, epoch, liveVolume);
        try
        {
            await Task.WhenAll(set._slots.Select(set.PrepareOne)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (set._prepareFailure != null) throw set._prepareFailure;
            set._stop.Token.ThrowIfCancellationRequested();
            foreach (var slot in set._slots) slot.Worker = Task.Run(() => set.RunSlot(slot));
            return set;
        }
        catch { set.Dispose(); throw; }
        finally { set._prepared.TrySetResult(); }
    }
    private async Task PrepareOne(Slot slot)
    {
        try { await ConnectOne(slot).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            RecordFailure(slot, error);
            if (slot.Options.Required)
            {
                lock (_commands) _prepareFailure ??= new(slot.Options.Name, error);
                _stop.Cancel();
            }
        }
    }
    private async Task ConnectOne(Slot slot)
    {
        Session? session = null;
        bool startNow;
        try
        {
            var target = await slot.Options.Resolve(_stop.Token).ConfigureAwait(false);
            if (slot.Options.BeforeConnect != null) await slot.Options.BeforeConnect(target, _stop.Token).ConfigureAwait(false);
            session = new(() => Desired(slot));
            session.Transport = slot.Options.PrepareApple != null && target.Device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv or ReceiverKind.Unknown
                ? await slot.Options.PrepareApple(target, _stop.Token).ConfigureAwait(false)
                : await ReceiverGroupConnections.PrepareAsync(target.Device, target.Settings,
                    slot.Options.Connection with { VolumePercent = 0, VolumeCapPercent = Cap(),
                        Password = slot.Options.Password?.Invoke() ?? slot.Options.Connection.Password }, _clocks, _stop.Token, _connect).ConfigureAwait(false);
            lock (_commands)
            {
                _stop.Token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (session.Transport.Lost.IsCompleted) throw session.Transport.LostException ?? new IOException(session.Transport.Lost.Result);
                slot.Current = session; slot.Failure = null; slot.Terminal = slot.Retrying = false;
                startNow = _timeline != null;
                if (startNow)
                {
                    if (session.Transport.LatencyMs + Math.Max(0, -slot.Options.TrimMs) > _delay)
                        throw slot.Options.Required ? new ReceiverGroupDelayChangedException() : new ReceiverGroupDelayUnavailableException();
                    session.Bind(_timeline!, _delay, slot.Options);
                }
            }
            if (startNow) await StartOne(slot, session, _epoch() + MediaClock.FromMs(250)).ConfigureAwait(false);
        }
        catch { lock (_commands) if (ReferenceEquals(slot.Current, session)) slot.Current = null; session?.Cleanup(); throw; }
    }
    public async Task StartAsync(TimelineBuffer timeline, int groupDelayMs, double volume, bool muted, long startAt, CancellationToken ct)
    {
        (Slot Slot, Session Session)[] sessions;
        try
        {
            lock (_commands)
            {
                _stop.Token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (_timeline != null) throw new InvalidOperationException("Receiver outputs already started");
                if (groupDelayMs < MinimumDelayMs) throw new ReceiverGroupDelayChangedException();
                _timeline = timeline; _delay = groupDelayMs;
                if (!_volumePublished) { _volume = Normalize(volume); _muted = muted; }
                _volumePublished = true; _version++;
                sessions = _slots.Where(s => s.Current != null).Select(s => (s, s.Current!)).ToArray();
                foreach (var pair in sessions) pair.Session.Bind(timeline, groupDelayMs, pair.Slot.Options);
            }
            await Task.WhenAll(sessions.Select(async pair =>
            {
                try { await StartOne(pair.Slot, pair.Session, startAt).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested || ct.IsCancellationRequested) { throw; }
                catch (Exception error) { pair.Session.Fail(error); if (pair.Slot.Options.Required) throw new GroupMemberException(pair.Slot.Options.Name, error); }
            })).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
    }
    private async Task StartOne(Slot slot, Session session, long startAt)
    {
        await session.Commands.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            if (session.Transport.Lost.IsCompleted) throw session.Transport.LostException ?? new IOException(session.Transport.Lost.Result);
            var want = Desired(slot);
            await Task.Run(() => session.Transport.Start(session, startAt, want.Muted ? 0 : want.Percent, Cap()), _stop.Token).ConfigureAwait(false);
            session.Started = true;
            await PublishStable(slot, session).ConfigureAwait(false);
            if (session.Ready)
                try { slot.Options.Connected?.Invoke(session.Transport.Protocol, session.Transport.ArrivalToRenderMs); }
                catch (Exception callback) { Log.Warn(callback.Message); }
        }
        finally { session.Commands.Release(); }
    }
    private static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    private double Cap() { try { return Normalize(_cap()); } catch { return 0; } }
    private (double Percent, bool Muted) Desired(Slot slot)
    {
        lock (_commands)
        {
            var latest = (_volume, _muted);
            if (_liveVolume != null)
                try { var live = _liveVolume(); latest = (Normalize(live.Percent ?? _volume), live.Muted); } catch { latest = (0, true); }
            double master = Math.Min(latest.Item1, Cap());
            return (master <= 0 ? 0 : Math.Clamp(master + slot.Options.VolumeOffset, 0, Cap()), latest.Item2 || _disposed != 0 || _timeline == null);
        }
    }
    private async Task PublishStable(Slot slot, Session session)
    {
        while (true)
        {
            var want = Desired(slot); long version;
            lock (_commands) { if (_disposed != 0 || !ReferenceEquals(slot.Current, session)) return; version = _version; }
            double applied = want.Muted ? 0 : want.Percent;
            await Task.Run(() => session.Transport.SetVolume(applied, Cap()), _stop.Token).ConfigureAwait(false);
            lock (_commands)
            {
                if (_disposed != 0 || !ReferenceEquals(slot.Current, session)) return;
                if (version != _version || want != Desired(slot)) continue;
                if (session.Transport.Lost.IsCompleted) throw session.Transport.LostException ?? new IOException(session.Transport.Lost.Result);
                session.Publish(applied, _timeline != null && session.Started); return;
            }
        }
    }
    public void SetVolume(double volume, bool muted)
    {
        (Slot Slot, Session Session)[] sessions;
        lock (_commands)
        {
            if (_disposed != 0) return;
            _volume = Normalize(volume); _muted = muted; _volumePublished = true; _version++;
            sessions = _slots.Where(s => s.Current != null).Select(s => (s, s.Current!)).ToArray();
        }
        foreach (var pair in sessions) QueuePublish(pair.Slot, pair.Session);
    }
    private void QueuePublish(Slot slot, Session session)
    {
        Volatile.Write(ref session.Dirty, 1);
        if (Interlocked.CompareExchange(ref session.Publishing, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    while (Interlocked.Exchange(ref session.Dirty, 0) == 1)
                    {
                        await session.Commands.WaitAsync(_stop.Token).ConfigureAwait(false);
                        try { await PublishStable(slot, session).ConfigureAwait(false); }
                        finally { session.Commands.Release(); }
                    }
                    Volatile.Write(ref session.Publishing, 0);
                    if (Volatile.Read(ref session.Dirty) == 0 || Interlocked.CompareExchange(ref session.Publishing, 1, 0) != 0) return;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { session.Fail(error); }
        });
    }
    private async Task RunSlot(Slot slot)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var current = Volatile.Read(ref slot.Current);
                if (current != null)
                {
                    var ended = await Task.WhenAny(current.Failed.Task, current.Transport.Lost).WaitAsync(_stop.Token).ConfigureAwait(false);
                    string reason = await ended.ConfigureAwait(false);
                    var remote = current.Transport.LostException;
                    var error = ReceiverGroupFailures.RequiresUserAction(remote) ? remote!
                        : current.Failure ?? remote ?? new IOException(reason);
                    // Publish the reason before detaching the transport: another leg can end the group while
                    // local stop is still finishing, and its finally must still see this terminal refusal.
                    RecordFailure(slot, error);
                    lock (_commands) if (ReferenceEquals(slot.Current, current)) slot.Current = null;
                    current.Cleanup();
                    if (slot.Options.Required)
                    {
                        lock (_commands)
                        {
                            LostReason = reason; LostException = error; LostOutputName = slot.Options.Name; LostRequiresUserAction = slot.Terminal;
                            _lost.TrySetResult(L.F("{0}：{1}", slot.Options.Name, reason));
                        }
                        StopAll(); _stop.Cancel(); return;
                    }
                }
                lock (_commands) { if (slot.Terminal) { slot.Retrying = false; return; } slot.Retrying = true; }
                await _retry(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                try { await ConnectOne(slot).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception error) { RecordFailure(slot, error); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { Session? current; lock (_commands) { current = slot.Current; slot.Current = null; slot.Retrying = false; } current?.Cleanup(); }
    }
    private void RecordFailure(Slot slot, Exception error)
    {
        error = ReceiverGroupFailures.Normalize(error);
        bool notify;
        lock (_commands)
        {
            slot.Failure = error; slot.Terminal = ReceiverGroupFailures.RequiresUserAction(error); slot.Retrying = !slot.Terminal;
            notify = slot.Terminal && !slot.ActionNotified; slot.ActionNotified |= notify;
        }
        if (notify) try { slot.Options.ActionRequired?.Invoke(error); } catch (Exception callback) { Log.Warn(callback.Message); }
    }
    public bool RetryOutput(string id)
    {
        lock (_commands)
        {
            // Only AirPlay ids occur here. Keep the protocol layer independent of the app's group config.
            static string Key(string value) => value.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
            var slot = _slots.FirstOrDefault(s => Key(s.Options.Id) == Key(id));
            if (_disposed != 0 || _stop.IsCancellationRequested || slot == null || !slot.Terminal || slot.Options.Required) return false;
            slot.Terminal = slot.ActionNotified = false; slot.Retrying = true;
            // The old worker may still be finishing its finally. Never let it detach the new session.
            var previous = slot.Worker ?? Task.CompletedTask;
            slot.Worker = Task.Run(async () => { try { await previous.ConfigureAwait(false); } catch { } await RunSlot(slot).ConfigureAwait(false); });
            return true;
        }
    }
    private void StopAll() { foreach (var slot in _slots) Volatile.Read(ref slot.Current)?.Stop(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAll(); _stop.Cancel();
        Session[] sessions; Task[] workers;
        lock (_commands)
        {
            sessions = _slots.Select(s => s.Current).OfType<Session>().ToArray();
            foreach (var slot in _slots) slot.Current = null;
            workers = _slots.Select(s => s.Worker ?? Task.CompletedTask).ToArray();
        }
        foreach (var session in sessions) session.Cleanup();
        _clocks.Dispose();
        _ = Task.WhenAll(workers.Append(_prepared.Task)).ContinueWith(t =>
        { _ = t.Exception; _cancel.Dispose(); _stop.Dispose(); }, TaskScheduler.Default);
    }
}
