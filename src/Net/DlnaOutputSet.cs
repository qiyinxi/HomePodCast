using System.Net;
using HomePodCast.Audio;

namespace HomePodCast.Net;

internal sealed record DlnaConnectionTarget(DlnaDevice Device, IPAddress LocalAddress, Func<bool> IsPrivateNetwork);

internal sealed record DlnaGroupOutputOptions(string Udn, string Name,
    Func<CancellationToken, Task<DlnaConnectionTarget>> Resolve)
{
    public string Id => "dlna:" + Udn;
    public bool Required { get; init; }
    public int EstimatedLatencyMs { get; init; } = 2000;
    public int TrimMs { get; init; }
    public int VolumeOffset { get; init; }
    public ChannelMode Channels { get; init; }
    public DlnaAudioFormat? PreferredFormat { get; init; }
    public DlnaHttpFraming Framing { get; init; } = DlnaHttpFraming.Chunked;
    public DlnaTransferPacing Pacing { get; init; } = DlnaTransferPacing.Receiver;
    public bool InitialTakeOverOnce { get; init; }
    public Action<Exception>? ActionRequired { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Udn);
        ArgumentNullException.ThrowIfNull(Resolve);
        if (EstimatedLatencyMs is < 0 or > 20000) throw new ArgumentOutOfRangeException(nameof(EstimatedLatencyMs));
        if (TrimMs is < -5000 or > 5000) throw new ArgumentOutOfRangeException(nameof(TrimMs));
        if (VolumeOffset is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(VolumeOffset));
        if (!Enum.IsDefined(Channels) || !Enum.IsDefined(Framing) || !Enum.IsDefined(Pacing)
            || PreferredFormat is { } format && !Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(Channels));
    }
}

internal sealed record DlnaGroupOutputStatus(string Id, string Name, OutputState State, string? Error, OutputStats Stats,
    bool Required, bool Retrying, bool NeedsUserAction)
{
    public DlnaAudioFormat? Format { get; init; }
    public int EstimatedLatencyMs { get; init; }
    public double DriftPpm { get; init; }
    public bool HasClockEstimate { get; init; }
    public double ClockConfidence { get; init; }
    public DlnaFailureKind FailureKind { get; init; }
}

/// <summary>Independent HTTP-pull outputs over the group's already processed history.</summary>
internal sealed class DlnaOutputSet : IDisposable
{
    private sealed class Session
    {
        public DlnaClient Client = null!;
        public readonly object Audio = new();
        public readonly SemaphoreSlim Commands = new(1);
        public readonly DlnaReadClock Clock = new();
        public readonly TaskCompletionSource<string> Lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DlnaTimelineReader? Reader;
        public Exception? Failure;
        public long AnchorTicks;
        public bool Ready;
        public void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref Failure, error, null);
            Volatile.Write(ref Ready, false);
            Client?.StopLocal();
            Lost.TrySetResult(error.Message);
        }
        public void Stop()
        {
            Volatile.Write(ref Ready, false);
            Client?.StopLocal();
        }
        public void Cleanup()
        {
            Stop();
            if (Client != null) _ = Client.DisposeAsync().AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private sealed class Slot(DlnaGroupOutputOptions options)
    {
        public readonly DlnaGroupOutputOptions Options = options;
        public Session? Current;
        public string? Error;
        public Exception? Failure;
        public bool Terminal, Retrying, Attempted, ActionNotified;
        public Task? Worker;
    }

    // Covers a bounded TCP burst and one encoder's priming block. Receiver latency remains a user estimate,
    // never an acoustic measurement inferred from a successful HTTP write.
    internal const int PrimingMarginMs = 250;
    private readonly Slot[] _slots;
    private readonly object _commands = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenRegistration _cancel;
    private readonly Func<double> _cap;
    private readonly Func<DlnaGroupOutputOptions, DlnaConnectionTarget, DlnaClientOptions, CancellationToken, Task<DlnaClient>> _connect;
    private readonly Func<TimeSpan, CancellationToken, Task> _retry;
    private readonly Func<long> _epoch;
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _prepareDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GroupMemberException? _prepareFailure;
    private TimelineBuffer? _timeline;
    private int _delayMs, _disposed;
    private long _version;
    private double _volume;
    private bool _muted = true, _volumePublished;

    private DlnaOutputSet(IReadOnlyList<DlnaGroupOutputOptions> options, Func<double> cap, CancellationToken ct,
        Func<DlnaGroupOutputOptions, DlnaConnectionTarget, DlnaClientOptions, CancellationToken, Task<DlnaClient>>? connect,
        Func<TimeSpan, CancellationToken, Task>? retry, Func<long>? epoch)
    {
        _slots = options.Select(o => new Slot(o)).ToArray();
        _cap = cap;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connect = connect ?? ((_, target, transport, token) => DlnaClient.ConnectAsync(target.Device, transport, token));
        _retry = retry ?? Task.Delay;
        _epoch = epoch ?? (() => MediaClock.Now);
        _cancel = ct.Register(() => _ = Task.Run(Dispose));
    }

    public int MinimumDelayMs => _slots.Max(s => s.Options.EstimatedLatencyMs + Math.Max(0, -s.Options.TrimMs) + PrimingMarginMs);
    public int HistoryMs(int groupDelayMs) => checked(groupDelayMs + _slots.Max(s => Math.Max(0, s.Options.TrimMs)) + 2000);
    public Task<string> Lost => _lost.Task;
    public string? LostReason { get; private set; }
    public string? LostOutputName { get; private set; }
    public bool LostRequiresUserAction { get; private set; }
    public Exception? LostException { get; private set; }
    public bool AllTerminal { get { lock (_commands) return _slots.All(s => s.Current == null && s.Terminal); } }
    public double MasterVolume { get { lock (_commands) return _volume; } }

    public IReadOnlyList<DlnaGroupOutputStatus> Status
    {
        get
        {
            lock (_commands)
                return _slots.Select(s =>
                {
                    var session = s.Current;
                    lock (session?.Audio ?? new object())
                        return new DlnaGroupOutputStatus(s.Options.Id, s.Options.Name,
                            _disposed != 0 ? OutputState.Stopped : session == null || session.Lost.Task.IsCompleted
                                || session.Client.Lost.IsCompleted ? OutputState.Lost
                                : _timeline == null || !session.Ready ? OutputState.Preparing : OutputState.Streaming,
                            s.Error ?? session?.Client.LostException?.Message, default, s.Options.Required, s.Retrying, s.Terminal)
                        {
                            Format = session?.Client.Format, EstimatedLatencyMs = s.Options.EstimatedLatencyMs,
                            DriftPpm = session?.Clock.DriftPpm ?? 0, HasClockEstimate = session?.Clock.HasEstimate ?? false,
                            ClockConfidence = session?.Clock.Confidence ?? 0,
                            FailureKind = DlnaFailures.Kind(s.Failure ?? session?.Client.LostException),
                        };
                }).ToArray();
        }
    }

    public static async Task<DlnaOutputSet> PrepareAsync(IReadOnlyList<DlnaGroupOutputOptions> options, Func<double> cap,
        CancellationToken ct,
        Func<DlnaGroupOutputOptions, DlnaConnectionTarget, DlnaClientOptions, CancellationToken, Task<DlnaClient>>? connect = null,
        Func<TimeSpan, CancellationToken, Task>? retry = null, Func<long>? epoch = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cap);
        if (options.Count == 0) throw new ArgumentException("No DLNA outputs", nameof(options));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            option.Validate();
            if (!ids.Add(option.Id)) throw new ArgumentException("Duplicate DLNA output", nameof(options));
        }
        ct.ThrowIfCancellationRequested();
        var set = new DlnaOutputSet(options, cap, ct, connect, retry, epoch);
        try
        {
            await Task.WhenAll(set._slots.Select(set.PrepareOne)).ConfigureAwait(false);
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
        try { await ConnectOne(slot).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            RecordFailure(slot, error);
            if (slot.Options.Required)
            {
                lock (_commands) _prepareFailure ??= new GroupMemberException(slot.Options.Name, error);
                _stop.Cancel();
            }
        }
    }

    private async Task ConnectOne(Slot slot)
    {
        var session = new Session();
        try
        {
            bool takeOver;
            lock (_commands)
            {
                takeOver = slot.Options.InitialTakeOverOnce && !slot.Attempted;
                slot.Attempted = true;
            }
            // A saved UDN is resolved afresh for every attempt; a renderer's address and control URLs are not identity.
            var target = await slot.Options.Resolve(_stop.Token).ConfigureAwait(false);
            _stop.Token.ThrowIfCancellationRequested();
            var transport = new DlnaClientOptions(target.LocalAddress, (ticks, pcm) => Read(slot, session, ticks, pcm), target.IsPrivateNetwork)
            {
                PreferredFormat = slot.Options.PreferredFormat, Framing = slot.Options.Framing, Pacing = slot.Options.Pacing,
                VolumePercent = 0, VolumeCapPercent = 100, Muted = true, TakeOver = takeOver,
                LiveVolumePercent = () => EffectiveVolume(slot), LiveVolumeCapPercent = Cap, LiveMuted = () => LiveMuted(session),
                BeforeReadAsync = (ticks, ct) => BeforeReadAsync(slot, session, ticks, ct),
                ObserveWrite = observation => Observe(session, observation),
                OnStreamStarted = _ => StreamStarted(session),
            };
            session.Client = await _connect(slot.Options, target, transport, _stop.Token).ConfigureAwait(false);
            lock (_commands)
            {
                if (_lost.Task.IsCompleted)
                    throw new GroupMemberException(LostOutputName ?? "DLNA", LostException ?? new IOException(LostReason));
                _stop.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (session.Client.Lost.IsCompleted) throw session.Client.LostException ?? new IOException(session.Client.Lost.Result);
                if (_timeline != null) BindReader(slot, session);
                slot.Current = session;
                slot.Error = null;
                slot.Failure = null;
                slot.Terminal = slot.Retrying = false;
            }
            if (_timeline != null) await PublishLatestAsync(slot, session).ConfigureAwait(false);
        }
        catch
        {
            lock (_commands) if (ReferenceEquals(slot.Current, session)) slot.Current = null;
            session.Cleanup();
            throw;
        }
    }

    public async Task StartAsync(TimelineBuffer timeline, int groupDelayMs, double volume, bool muted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        List<(Slot Slot, Session Session)> sessions;
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_commands)
            {
                if (_lost.Task.IsCompleted)
                    throw new GroupMemberException(LostOutputName ?? "DLNA", LostException ?? new IOException(LostReason));
                _stop.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (_timeline != null) throw new InvalidOperationException("DLNA outputs already started");
                if (timeline.SampleRate != DlnaReadClock.SampleRate) throw new ArgumentException("DLNA requires a 44100 Hz timeline", nameof(timeline));
                if (groupDelayMs < MinimumDelayMs) throw new ArgumentOutOfRangeException(nameof(groupDelayMs));
                _timeline = timeline;
                _delayMs = groupDelayMs;
                if (!_volumePublished) { _volume = Normalize(volume); _muted = muted; }
                _volumePublished = true;
                _version++;
                sessions = _slots.Where(s => s.Current != null).Select(s => (s, s.Current!)).ToList();
                foreach (var (slot, session) in sessions) BindReader(slot, session);
            }
            await Task.WhenAll(sessions.Select(async pair =>
            {
                try { await PublishLatestAsync(pair.Slot, pair.Session).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested || ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    pair.Session.Fail(error);
                    if (pair.Slot.Options.Required) throw new GroupMemberException(pair.Slot.Options.Name, error);
                }
            })).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
    }

    private void BindReader(Slot slot, Session session)
    {
        lock (session.Audio)
        {
            session.Reader = new DlnaTimelineReader(_timeline!, _delayMs, slot.Options.TrimMs, slot.Options.EstimatedLatencyMs, slot.Options.Channels);
            session.AnchorTicks = _epoch();
            session.Clock.Reset();
            session.Ready = false;
        }
    }

    private static double Normalize(double percent) => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
    private double Cap() { try { return Normalize(_cap()); } catch { return 0; } }
    private double EffectiveVolume(Slot slot)
    {
        lock (_commands)
        {
            double cap = Cap(), master = Math.Min(_volume, cap);
            return master <= 0 ? 0 : Math.Clamp(master + slot.Options.VolumeOffset, 0, cap);
        }
    }
    private bool LiveMuted(Session session) { lock (_commands) return _muted || _disposed != 0 || !Volatile.Read(ref session.Ready); }

    private async Task PublishLatestAsync(Slot slot, Session session)
    {
        await session.Commands.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                double volume;
                bool muted;
                long version;
                lock (_commands)
                {
                    if (_disposed != 0 || !ReferenceEquals(slot.Current, session)) return;
                    volume = EffectiveVolume(slot); muted = _muted || _timeline == null; version = _version;
                }
                await session.Client.SetVolumeAsync(volume, muted, _stop.Token).ConfigureAwait(false);
                lock (_commands)
                {
                    if (_disposed != 0 || !ReferenceEquals(slot.Current, session)) return;
                    if (version != _version) continue;
                    Volatile.Write(ref session.Ready, _timeline != null);
                    return;
                }
            }
        }
        finally { session.Commands.Release(); }
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
        foreach (var (slot, session) in sessions)
            _ = PublishChangedAsync(slot, session);
    }

    private async Task PublishChangedAsync(Slot slot, Session session)
    {
        try { await PublishLatestAsync(slot, session).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { session.Fail(error); }
    }

    private void Read(Slot slot, Session session, long ticks, Span<short> pcm)
    {
        lock (session.Audio)
        {
            if (!Volatile.Read(ref session.Ready) || session.Reader == null) { pcm.Clear(); return; }
            // Prefetch before Start has its own sender epoch. Start/rejoin anchors the first real sample afresh.
            long anchor = double.IsNaN(session.Reader.Position) ? session.AnchorTicks : ticks;
            double position = session.Reader.Position;
            if (double.IsNaN(position))
                position = _timeline!.FrameAt(checked(anchor + MediaClock.FromMs(slot.Options.EstimatedLatencyMs)))
                    - Math.Round(((long)_delayMs + slot.Options.TrimMs) * DlnaReadClock.SampleRate / 1000.0);
            // Start can publish between a preparation callback's gate and its Read. Keep that bounded silence
            // block from committing a cursor into audio the producer has not written yet.
            if (_timeline!.EndFrame < position + pcm.Length / 2 * session.Clock.SourceFramesPerOutputFrame + 64)
            { pcm.Clear(); return; }
            session.Reader.Read(anchor, session.Clock.SourceFramesPerOutputFrame, pcm);
        }
    }

    private async ValueTask BeforeReadAsync(Slot slot, Session session, long plannedTicks, CancellationToken ct)
    {
        while (true)
        {
            TimelineBuffer? timeline;
            double end;
            lock (session.Audio)
            {
                timeline = Volatile.Read(ref _timeline);
                if (session.Reader == null || !Volatile.Read(ref session.Ready))
                {
                    // Preparation may prefetch inside SetURI/Play. Keep serving silence without an unbounded
                    // receiver-paced prefill or a gate that deadlocks the SOAP response.
                    end = double.NaN;
                }
                else
                {
                    double position = session.Reader.Position;
                    if (double.IsNaN(position))
                        position = timeline!.FrameAt(checked(session.AnchorTicks + MediaClock.FromMs(slot.Options.EstimatedLatencyMs)))
                            - Math.Round(((long)_delayMs + slot.Options.TrimMs) * DlnaReadClock.SampleRate / 1000.0);
                    // Include the sinc's future taps when an estimated rate starts correcting the cursor.
                    end = position + DlnaTimelineReader.MaximumFrames * session.Clock.SourceFramesPerOutputFrame + 64;
                }
            }
            if (double.IsNaN(end))
            {
                long remaining = plannedTicks - MediaClock.Now;
                if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds((double)remaining / MediaClock.Frequency), ct).ConfigureAwait(false);
                return;
            }
            if (timeline!.EndFrame >= end) return;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private static void Observe(Session session, DlnaWriteObservation observation)
    {
        lock (session.Audio)
            session.Clock.Observe(observation.PcmFrames, observation.CompletedTicks,
                Volatile.Read(ref session.Ready) && observation.BackpressureEligible);
    }

    private void StreamStarted(Session session)
    {
        lock (session.Audio)
        {
            session.Reader?.Reset();
            session.Clock.Reset();
            session.AnchorTicks = _epoch();
        }
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
                    Task<string> loss = await Task.WhenAny(current.Lost.Task, current.Client.Lost).WaitAsync(_stop.Token).ConfigureAwait(false);
                    string reason = await loss.ConfigureAwait(false);
                    Exception error = current.Failure ?? current.Client.LostException ?? new IOException(reason);
                    lock (_commands) if (ReferenceEquals(slot.Current, current)) slot.Current = null;
                    current.Cleanup();
                    RecordFailure(slot, error);
                    if (slot.Options.Required)
                    {
                        lock (_commands)
                        {
                            LostReason = reason; LostOutputName = slot.Options.Name; LostRequiresUserAction = slot.Terminal;
                            LostException = error;
                            _lost.TrySetResult(L.F("{0}：{1}", slot.Options.Name, reason));
                        }
                        StopAllLocal();
                        _stop.Cancel();
                        return;
                    }
                }
                lock (_commands)
                {
                    if (slot.Terminal) { slot.Retrying = false; return; }
                    slot.Retrying = true;
                }
                await _retry(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                _stop.Token.ThrowIfCancellationRequested();
                try { await ConnectOne(slot).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception error) { RecordFailure(slot, error); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            Session? current;
            lock (_commands) { current = slot.Current; slot.Current = null; slot.Retrying = false; }
            current?.Cleanup();
        }
    }

    private void RecordFailure(Slot slot, Exception error)
    {
        Action<Exception>? notify = null;
        lock (_commands)
        {
            slot.Error = error.Message;
            slot.Failure = error;
            slot.Terminal = DlnaFailures.RequiresUserAction(error);
            slot.Retrying = !slot.Terminal && !slot.Options.Required;
            if (slot.Terminal && !slot.ActionNotified)
            {
                slot.ActionNotified = true;
                notify = slot.Options.ActionRequired;
            }
        }
        try { notify?.Invoke(error); }
        catch (Exception consumer) { Log.Warn($"DLNA output action {Log.Name(slot.Options.Name)}: {consumer.Message}"); }
    }

    private void StopAllLocal()
    {
        Session[] sessions;
        lock (_commands) sessions = _slots.Select(s => s.Current).OfType<Session>().ToArray();
        foreach (var session in sessions) session.Stop();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Session[] sessions;
        Task[] workers;
        lock (_commands)
        {
            sessions = _slots.Select(s => s.Current).OfType<Session>().ToArray();
            foreach (var slot in _slots) slot.Current = null;
            workers = _slots.Select(s => s.Worker ?? Task.CompletedTask).ToArray();
        }
        foreach (var session in sessions) session.Stop();
        _stop.Cancel();
        foreach (var session in sessions) session.Cleanup();
        _ = Task.WhenAll(workers.Append(_prepareDone.Task)).ContinueWith(t =>
        {
            _ = t.Exception;
            _cancel.Dispose();
            _stop.Dispose();
        }, TaskScheduler.Default);
    }
}
