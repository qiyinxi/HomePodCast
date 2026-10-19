using HomePodCast.Audio;

namespace HomePodCast.Net;

internal sealed record LocalOutputStatus(string Id, string Name, OutputState State, string? Error, OutputStats Stats);

/// <summary>Required outputs fail the group; optional endpoints recover on their own, without restarting AirPlay.</summary>
internal sealed class LocalOutputSet : IDisposable
{
    private sealed class Slot(LocalOutputOptions options)
    {
        public LocalOutputOptions Options { get; } = options;
        public LocalOutputSink? Current;
        public string? Error;
        public Task? Worker;
    }
    private readonly Slot[] _slots;
    private readonly CancellationTokenSource _stop;
    private readonly Func<LocalOutputOptions, CancellationToken, Task<LocalOutputSink>> _prepare;
    private readonly Func<TimeSpan, CancellationToken, Task>? _retry;
    private readonly SemaphoreSlim _changed = new(0);
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _watch;
    private int _disposed;
    private TimelineBuffer? _timeline;
    private int _delayMs;
    private double _volume;

    private LocalOutputSet(IReadOnlyList<LocalOutputOptions> options, Func<string?> captured, Func<double> cap,
        CancellationToken ct, Func<string, ILocalRenderDevice>? open, Func<TimeSpan, CancellationToken, Task>? retry)
    {
        _slots = options.Select(o => new Slot(o)).ToArray();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _prepare = (o, c) => LocalOutputSink.PrepareAsync(o, captured, cap, c, open);
        _retry = retry;
        _watch = open == null;
        if (_watch) AudioDeviceWatcher.Changed += DevicesChanged;
    }

    public int MinimumDelayMs => _slots.Max(s => (s.Current?.MinLatencyMs ?? s.Options.ExtraLatencyMs + 100) - s.Options.TrimMs);
    public int HistoryMs(int groupDelayMs) => checked(groupDelayMs + _slots.Max(s => Math.Max(0, s.Options.TrimMs)) + 2000);
    public Task<string> Lost => _lost.Task;
    public string? LostReason { get; private set; }
    public string? LostOutputName { get; private set; }
    public IReadOnlyList<LocalOutputStatus> Status => _slots.Select(s =>
    {
        var sink = Volatile.Read(ref s.Current);
        return new LocalOutputStatus(s.Options.DeviceId, s.Options.Name, sink?.State ?? OutputState.Lost, s.Error ?? sink?.LostReason, sink?.Stats ?? default);
    }).ToArray();

    public static async Task<LocalOutputSet> PrepareAsync(IReadOnlyList<LocalOutputOptions> options, Func<string?> captured,
        Func<double> cap, CancellationToken ct, Func<string, ILocalRenderDevice>? open = null,
        Func<TimeSpan, CancellationToken, Task>? retry = null)
    {
        if (options.Count == 0) throw new ArgumentException("No local outputs", nameof(options));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            option.Validate();
            if (!ids.Add(option.DeviceId)) throw new ArgumentException("Duplicate local endpoint", nameof(options));
            LocalOutputSink.CheckCapture(option.DeviceId, captured());
        }
        var set = new LocalOutputSet(options, captured, cap, ct, open, retry);
        try
        {
            await Task.WhenAll(set._slots.Select(set.PrepareOne));
            ct.ThrowIfCancellationRequested();
            return set;
        }
        catch { set.Dispose(); throw; }
    }

    private async Task PrepareOne(Slot slot)
    {
        try
        {
            var sink = await _prepare(slot.Options, _stop.Token);
            sink.BindVolumeSource(() => Volatile.Read(ref _volume));
            slot.Current = sink;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            slot.Error = ex.Message;
            if (slot.Options.Required)
            {
                _stop.Cancel();
                throw new GroupMemberException(slot.Options.Name, ex);
            }
        }
    }

    public async Task StartAsync(TimelineBuffer timeline, int delayMs, double volume, CancellationToken ct)
    {
        if (_timeline != null) throw new InvalidOperationException("Local outputs already started");
        _timeline = timeline; _delayMs = delayMs;
        SetVolume(volume);
        foreach (var slot in _slots)
        {
            if (slot.Current is { } sink)
            {
                try { await sink.StartAsync(timeline, delayMs, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    slot.Error = ex.Message;
                    Interlocked.Exchange(ref slot.Current, null)?.Dispose();
                    if (slot.Options.Required) throw new GroupMemberException(slot.Options.Name, ex);
                }
            }
            slot.Worker = Task.Run(() => RunSlot(slot));
        }
    }

    private async Task RunSlot(Slot slot)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (slot.Current is { } current)
                {
                    string reason = await current.Lost.WaitAsync(_stop.Token);
                    slot.Error = reason;
                    Interlocked.Exchange(ref slot.Current, null)?.Dispose();
                    if (slot.Options.Required)
                    {
                        LostReason = reason;
                        LostOutputName = slot.Options.Name;
                        _lost.TrySetResult(L.F("{0}：{1}", slot.Options.Name, reason));
                        return;
                    }
                }
                if (_retry != null) await _retry(TimeSpan.FromSeconds(2), _stop.Token);
                else await _changed.WaitAsync(TimeSpan.FromSeconds(2), _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                try
                {
                    var sink = await _prepare(slot.Options, _stop.Token);
                    sink.BindVolumeSource(() => Volatile.Read(ref _volume));
                    Interlocked.Exchange(ref slot.Current, sink);
                    await sink.StartAsync(_timeline!, _delayMs, _stop.Token);
                    slot.Error = null;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    slot.Error = ex.Message;
                    Interlocked.Exchange(ref slot.Current, null)?.Dispose();
                    Log.Warn($"local output {Log.Name(slot.Options.Name)}: {ex.Message}; retrying");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { Interlocked.Exchange(ref slot.Current, null)?.Dispose(); }
    }

    public void SetVolume(double volume)
    {
        Volatile.Write(ref _volume, volume);
    }

    private void DevicesChanged()
    {
        if (Volatile.Read(ref _disposed) == 0 && _changed.CurrentCount == 0) _changed.Release(_slots.Length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_watch) AudioDeviceWatcher.Changed -= DevicesChanged;
        _stop.Cancel();
        foreach (var slot in _slots) Interlocked.Exchange(ref slot.Current, null)?.Dispose();
        _ = Task.WhenAll(_slots.Select(s => s.Worker ?? Task.CompletedTask)).ContinueWith(t =>
        {
            _ = t.Exception;
            _changed.Dispose(); _stop.Dispose();
        }, TaskScheduler.Default);
    }
}
