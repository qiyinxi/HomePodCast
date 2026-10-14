using HomePodCast.Net;

namespace HomePodCast;

public sealed partial class StreamController
{
    private IReceiverSession? _receiverSession;
    internal PtpPorts? ReceiverTimingPorts { get; set; }
    public IReceiverSession? ReceiverSession => _receiverSession;
    public Func<string, ReceiverPreferences?>? ReceiverSettings { get; set; }
    internal ThirdPartyConnections.Connector? BufferedReceiverConnector { get; set; }
    internal Func<ReceiverProtocol, ThirdPartyConnections.Connector?>? ReceiverConnectorOverride { get; set; }
    public event Action<string, ReceiverProtocol, int?>? ReceiverProtocolConnected;
    public ReceiverProtocol? ActiveProtocol => _receiverSession?.Protocol
        ?? (_raopClient != null ? ReceiverProtocol.Raop : ActiveSender != null ? ReceiverProtocol.AirPlay2Realtime : null);

    private bool UsesThirdPartyPath(AirPlayDevice? device)
    {
        if (device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv) return false;
        var forced = ReceiverSettings?.Invoke(device.DeviceId)?.ProtocolOverride ?? ReceiverProtocol.Auto;
        return device.Kind is ReceiverKind.ThirdPartyAirPlay2 or ReceiverKind.Unknown
            || forced is ReceiverProtocol.AirPlay2Realtime or ReceiverProtocol.AirPlay2Buffered;
    }

    private async Task RunThirdPartyAsync(AirPlayDevice device, double? volume, int? volumeAsOf, ConnectTrigger trigger, CancellationToken ct)
    {
        TakeStartVolume(volume, volumeAsOf);
        string key = OwnSession.KeyOf(device.DeviceId);
        bool streamed = false;
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            IReceiverSession? mine = null;
            string? reason = null;
            DateTime began = UtcNow();
            try
            {
                Set(StreamState.Connecting, L.T("正在连接第三方 AirPlay 设备（实验性，未在实机上测试）…")); // experimental since 1.1
                HostResolved?.Invoke(device.Address.ToString());
                await WaitUntilFreeAsync(TakeoverPolicy.RetryTrigger(trigger, streamed), key,
                    [new BusyTarget(device.DeviceId, device.Name, device.Address, device.Port)], ct);
                EnsureCapture(ct);
                var options = new ReceiverConnectOptions(250, Muted ? 0 : Volume, VolumeCapPercent, Pairings,
                    ReceiverPassword?.Invoke(device.DeviceId), Effects, Log.Info) { TimingPorts = ReceiverTimingPorts };
                var session = await ThirdPartyConnections.ConnectAsync(device, ReceiverSettings?.Invoke(device.DeviceId), options, _fifo, ct,
                    BufferedReceiverConnector, ReceiverConnectorOverride);
                var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Lost += value => lost.TrySetResult(value);
                if (session.LostReason is { } early) lost.TrySetResult(early);
                lock (_lock) { if (!ct.IsCancellationRequested) _receiverSession = mine = session; }
                if (mine == null) { session.Dispose(); break; }
                EffectiveLatencyMs = session.LatencyMs;
                session.VolumeCapPercent = VolumeCapPercent;
                Volume ??= session.InitialVolumeDb is { } db ? VolumeLimit.Clamp(AirPlayClient.DbToPercent(db), VolumeCapPercent) : 0;
                PushVolume();
                ReceiverProtocolConnected?.Invoke(device.DeviceId, session.Protocol, session.ArrivalToRenderMs);
                OwnSession.Began(key, UtcNow()); streamed = true; began = UtcNow(); attempt = 0;
                Set(StreamState.Streaming, L.F("{0} · {1}", device.Name, ReceiverProtocols.Label(session.Protocol)));
                using var network = new NetworkWatch(device.Address);
                _net = network;
                reason = await lost.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (PairingRequiredException ex)
            {
                StopForPairing(device.DeviceId, device.Address, ex, trigger, device.Port, thirdParty: true); return;
            }
            catch (RaopPasswordRequiredException ex)
            {
                DisposeCapture(); Set(StreamState.Idle, ex.Message); RaopPasswordRequired?.Invoke(new(device, trigger)); return;
            }
            catch (RaopBusyException ex)
            {
                if (trigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver)
                {
                    DisposeCapture(); Set(StreamState.Idle, ex.Message);
                    if (trigger == ConnectTrigger.Manual) RaopTakeoverRequired?.Invoke(new(device, trigger));
                    return;
                }
                reason = ex.Message;
            }
            catch (Exception ex) { reason = ex.Message; Log.Warn($"receiver connect: {ex.Message}"); }
            finally
            {
                if (mine != null)
                {
                    OwnSession.Ended(UtcNow(), clean: ct.IsCancellationRequested);
                    if (Interlocked.CompareExchange(ref _receiverSession, null, mine) == mine) mine.Dispose();
                }
            }
            if (ct.IsCancellationRequested) break;
            if (streamed && UtcNow() - began > TakeoverGrace && reason is EventChannel.ClosedBySpeaker or Net.RaopClient.ClosedByReceiver)
            {
                OwnSession.Forget(); DisposeCapture(); Set(StreamState.Idle, L.T(TakenOverText)); return;
            }
            int seconds = Math.Min(30, 1 << Math.Min(++attempt, 5));
            Set(StreamState.Retrying, L.F("{0}，{1:F0} 秒后重试", reason, seconds));
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
