using HomePodCast.Audio;

namespace HomePodCast.Net;

internal sealed class ReceiverGroupStoppedException(string message) : AirPlayException(message);
internal sealed class ReceiverGroupUnsupportedException(string message) : AirPlayException(message);
internal sealed class ReceiverGroupDelayUnavailableException() : AirPlayException(L.T("这台输出需要更大的整组延迟，请重新连接分组。"));
internal sealed class ReceiverGroupDelayChangedException() : IOException("Receiver latency changed; the group delay must be renegotiated");

internal static class ReceiverGroupFailures
{
    public static bool RequiresUserAction(Exception? error) => error is ReceiverGroupStoppedException or ReceiverGroupUnsupportedException or ReceiverGroupDelayUnavailableException
        or PairingRequiredException or RaopBusyException or RaopPasswordRequiredException or RaopUnsupportedException or RaopRefusedException;
    public static Exception Normalize(Exception error) => error is AirPlayException { ResponseStatusCode: 453 } ? new RaopBusyException() : error;
}

internal interface IPreparedReceiverSession : IDisposable
{
    ReceiverProtocol Protocol { get; }
    bool UsesPtp { get; }
    int LatencyMs { get; }
    int? ArrivalToRenderMs { get; }
    IReadOnlyDictionary<string, object?> Info { get; }
    Task<string> Lost { get; }
    Exception? LostException { get; }
    long PacketsSent { get; }
    void Start(IReceiverAudioSource source, long startAt, double percent, double cap);
    void SetVolume(double percent, double cap);
    void StopSending();
}

/// <summary>Protocol trials prepare muted transports; no captured audio is consumed before the shared start.</summary>
internal static class ReceiverGroupConnections
{
    internal delegate Task<IPreparedReceiverSession> Connector(ReceiverProtocol protocol, AirPlayDevice device,
        ReceiverPreferences? settings, ReceiverConnectOptions options, PtpClockPool clocks, CancellationToken ct);

    public static async Task<IPreparedReceiverSession> PrepareAsync(AirPlayDevice device, ReceiverPreferences? settings,
        ReceiverConnectOptions options, PtpClockPool clocks, CancellationToken ct, Connector? connect = null,
        Func<IAacEncoder>? encoder = null)
    {
        if (device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv)
            throw new InvalidOperationException("Apple devices keep their original group output");
        var failures = new List<string>();
        PairingRequiredException? pairing = null;
        foreach (var protocol in ReceiverProtocols.Candidates(device, settings))
        {
            ct.ThrowIfCancellationRequested();
            options.Log?.Invoke($"group receiver protocol attempt: {protocol} {Log.Name(device.Name)}");
            try
            {
                var session = connect != null ? await connect(protocol, device, settings, options, clocks, ct).ConfigureAwait(false)
                    : await PrepareOne(protocol, device, settings, options, clocks, ct, encoder).ConfigureAwait(false);
                if (session.Lost.IsCompleted)
                {
                    Exception error = session.LostException ?? new IOException(await session.Lost.ConfigureAwait(false));
                    session.Dispose(); throw error;
                }
                return session;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AirPlayException error) when (error.ResponseStatusCode == 453) { throw new RaopBusyException(); }
            catch (RaopBusyException) { throw; }
            catch (RaopPasswordRequiredException) { throw; }
            catch (ReceiverGroupStoppedException) { throw; }
            catch (PairingRequiredException error)
            {
                pairing = error;
                failures.Add($"{protocol}: {error.Message}");
                options.Log?.Invoke($"group receiver refused: {protocol}: {AirPlayException.ForLog(error)}");
            }
            catch (Exception error)
            {
                failures.Add($"{protocol}: {error.Message}");
                options.Log?.Invoke($"group receiver refused: {protocol}: {error.Message}");
            }
        }
        if (pairing != null) throw pairing;
        throw new ReceiverGroupUnsupportedException(L.F("这台设备没有接受可用的连接方式：{0}", string.Join("; ", failures)));
    }

    private static async Task<IPreparedReceiverSession> PrepareOne(ReceiverProtocol protocol, AirPlayDevice device,
        ReceiverPreferences? settings, ReceiverConnectOptions options, PtpClockPool clocks, CancellationToken ct,
        Func<IAacEncoder>? encoder)
    {
        if (protocol == ReceiverProtocol.Raop)
            return await Legacy.Prepare(device, settings, options, ct).ConfigureAwait(false);
        bool ptp = protocol == ReceiverProtocol.AirPlay2Buffered
            || (ReceiverKinds.Features(device.Txt?.GetValueOrDefault("features") ?? device.Txt?.GetValueOrDefault("ft")) & ReceiverKinds.Ptp) != 0;
        int latency;
        if (protocol == ReceiverProtocol.AirPlay2Buffered)
            latency = ReceiverLatencies.CompatibilityChoices.Contains(settings?.CompatibilityLatencyMs ?? 2000) ? settings?.CompatibilityLatencyMs ?? 2000 : 2000;
        else
        {
            var policy = ReceiverLatencies.For(device.Kind, protocol, settings?.ArrivalToRenderMs, device.Model);
            if (policy.MinimumMs > policy.MaximumMs) throw new AirPlayException(L.T("这台设备的处理时间超过低延迟范围，请使用兼容或缓冲连接。"));
            latency = policy.Clamp(settings?.RealtimeLatencyMs ?? Math.Max(policy.DefaultMs, policy.RecommendedMinimumMs));
        }
        AirPlayClient? prepared = null;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var stream = new StreamOptions(latency, 0)
                {
                    VolumeCapPercent = options.VolumeCapPercent, Pairings = options.Pairings, DeviceId = device.DeviceId,
                    RequestLog = options.Log, AllowMissingEventChannel = true, PairingOverRtsp = true,
                };
                prepared = ptp
                    ? await AirPlayClient.ConnectBufferedAsync(device, stream, new AudioFifo(44100, 0, 0), ct,
                        encoder, options.TimingPorts, realtime: protocol == ReceiverProtocol.AirPlay2Realtime, prepareOnly: true, clocks: clocks).ConfigureAwait(false)
                    : await AirPlayClient.PrepareAsync(device.Address, device.Port, stream, ChannelMode.Stereo, ct).ConfigureAwait(false);
                if (ReceiverKinds.Classify(prepared.Info.GetValueOrDefault("model") as string ?? "", null, info: prepared.Info)
                    is ReceiverKind.HomePod or ReceiverKind.AppleTv)
                    throw new InvalidOperationException("Receiver identified itself as Apple; it keeps the original group output");
                if (protocol == ReceiverProtocol.AirPlay2Realtime)
                {
                    var actual = ReceiverLatencies.For(device.Kind, protocol, prepared.ArrivalToRenderMs, device.Model);
                    if (actual.MinimumMs > actual.MaximumMs) throw new AirPlayException(L.T("这台设备的处理时间超过低延迟范围，请使用兼容或缓冲连接。"));
                    int next = actual.Clamp(settings?.RealtimeLatencyMs ?? Math.Max(actual.DefaultMs, actual.RecommendedMinimumMs));
                    if (next > latency)
                    {
                        if (pass == 1) throw new AirPlayException("Receiver processing latency changed during group preparation");
                        prepared.Dispose(); prepared = null; latency = next; continue;
                    }
                }
                return new Modern(prepared, protocol, ptp, stream);
            }
            throw new AirPlayException("Receiver group preparation did not converge");
        }
        catch { prepared?.Dispose(); throw; }
    }

    private sealed class Modern : IPreparedReceiverSession
    {
        private readonly AirPlayClient _client;
        private readonly StreamOptions _options;
        private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        public ReceiverProtocol Protocol { get; }
        public bool UsesPtp { get; }
        public int LatencyMs => (int)Math.Round(_client.LatencyFrames * 1000.0 / 44100);
        public int? ArrivalToRenderMs => _client.ArrivalToRenderMs;
        public IReadOnlyDictionary<string, object?> Info => _client.Info;
        public Task<string> Lost => _lost.Task;
        public Exception? LostException => Volatile.Read(ref _failure);
        public long PacketsSent => _client.Sender?.PacketsSent ?? _client.PtpRealtime?.PacketsSent ?? _client.Buffered?.PacketsSent ?? 0;
        public Modern(AirPlayClient client, ReceiverProtocol protocol, bool ptp, StreamOptions options)
        {
            _client = client; Protocol = protocol; UsesPtp = ptp; _options = options;
            client.Lost += OnLost;
            if (client.LostReason is { } reason) OnLost(reason);
        }
        private void OnLost(string reason)
        {
            Exception failure = reason == EventChannel.ClosedBySpeaker ? new ReceiverGroupStoppedException(reason) : new IOException(reason);
            Interlocked.CompareExchange(ref _failure, failure, null);
            _lost.TrySetResult(reason);
        }
        public void Start(IReceiverAudioSource source, long startAt, double percent, double cap)
        {
            _client.VolumeCapPercent = cap;
            var options = _options with { VolumePercent = percent, VolumeCapPercent = cap };
            if (UsesPtp) _client.StartCoordinatedPtp(options, source.Read, startAt, Protocol == ReceiverProtocol.AirPlay2Realtime);
            else _client.StartCoordinatedNtp(options, source, startAt);
        }
        public void SetVolume(double percent, double cap) { _client.VolumeCapPercent = cap; _client.SetGroupVolumePercentConfirmed(percent); }
        public void StopSending() => _client.StopCoordinatedSending();
        public void Dispose() { _client.Lost -= OnLost; _client.Dispose(); }
    }

    private sealed class Legacy : IPreparedReceiverSession
    {
        private RaopClient _client = null!;
        private readonly TaskCompletionSource<long> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly float[] _scratch = new float[704];
        private IReceiverAudioSource? _source;
        public ReceiverProtocol Protocol => ReceiverProtocol.Raop;
        public bool UsesPtp => false;
        public int LatencyMs => (int)Math.Round(_client.LatencyFrames * 1000.0 / 44100);
        public int? ArrivalToRenderMs => null;
        public IReadOnlyDictionary<string, object?> Info { get; }
        public Task<string> Lost => _lost.Task;
        public Exception? LostException => _client.LostException;
        public long PacketsSent => _client.PacketsSent;
        private Legacy(AirPlayDevice device) => Info = new Dictionary<string, object?> { ["name"] = device.Name, ["model"] = device.Model };
        public static async Task<Legacy> Prepare(AirPlayDevice device, ReceiverPreferences? settings, ReceiverConnectOptions options, CancellationToken ct)
        {
            var result = new Legacy(device);
            int latency = settings?.CompatibilityLatencyMs ?? 2000;
            if (!ReceiverLatencies.CompatibilityChoices.Contains(latency)) latency = 2000;
            try
            {
                result._client = await RaopClient.ConnectAsync(device.Address, device.RaopPort ?? device.Port, new RaopOptions
                {
                    LatencyMs = latency, VolumePercent = 0, VolumeCapPercent = options.VolumeCapPercent, Password = options.Password,
                    Txt = device.RaopTxt ?? device.Txt, Log = options.Log, GroupStart = result._gate.Task, ReadTimedAudio = result.Read,
                }, ct).ConfigureAwait(false);
                result._client.Lost += result.OnLost;
                if (result._client.LostReason is { } reason) result.OnLost(reason);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        private void OnLost(string reason) => _lost.TrySetResult(reason);
        private void Read(long ticks, Span<short> stereo)
        {
            if (_source == null) { stereo.Clear(); return; }
            _source.Read(ticks, _scratch);
            for (int i = 0; i < stereo.Length; i++) stereo[i] = (short)Math.Clamp(Math.Round(_scratch[i] * 32767), short.MinValue, short.MaxValue);
        }
        public void Start(IReceiverAudioSource source, long startAt, double percent, double cap)
        { _source = source; SetVolume(percent, cap); if (!_gate.TrySetResult(startAt)) throw new InvalidOperationException("Receiver already started"); }
        public void SetVolume(double percent, double cap) { _client.VolumeCapPercent = cap; _client.SetVolume(percent); }
        public void StopSending() { _gate.TrySetCanceled(); _client?.StopSending(); }
        public void Dispose() { StopSending(); if (_client != null) { _client.Lost -= OnLost; _client.Dispose(); } }
    }
}
