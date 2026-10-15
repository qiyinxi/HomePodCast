using System.Net;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast;

public sealed record RaopInteraction(AirPlayDevice Device, ConnectTrigger Trigger);

public sealed partial class StreamController
{
    private RaopClient? _raopClient;
    public RaopClient? RaopClient => _raopClient;
    public Func<string, AirPlayDevice?>? ReceiverLookup { get; set; }
    public Func<string, int>? CompatibilityLatency { get; set; }
    public Func<string, string?>? ReceiverPassword { get; set; }
    public event Action<RaopInteraction>? RaopPasswordRequired;
    public event Action<RaopInteraction>? RaopTakeoverRequired;

    internal static bool UsesRaop(AirPlayDevice? device) => device?.Kind is ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1;

    private async Task RunRaopAsync(AirPlayDevice device, double? volume, int? volumeAsOf, ConnectTrigger trigger, CancellationToken ct)
    {
        TakeStartVolume(volume, volumeAsOf);
        string key = OwnSession.KeyOf(device.DeviceId);
        bool streamed = false, rediscover = false;
        while (!ct.IsCancellationRequested)
        {
            RaopClient? mine = null;
            string? lostReason = null;
            DateTime began = UtcNow();
            try
            {
                Set(StreamState.Connecting, L.T("正在连接 AirPlay 1 兼容模式（实验性）…")); // experimental since 1.1
                // This path uses the advertised RAOP port; never probe the HomePod's fixed 7000 port here.
                if (device.Address.Equals(IPAddress.None) || rediscover)
                    device = (await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct)).FirstOrDefault(d => RaopPassword.Key(d.DeviceId) == RaopPassword.Key(device.DeviceId))
                        ?? throw new AirPlayException(L.T("找不到音箱：请确认它已通电、和电脑在同一网络"));
                rediscover = false;
                HostResolved?.Invoke(device.Address.ToString());
                await WaitUntilFreeAsync(TakeoverPolicy.RetryTrigger(trigger, streamed), key,
                    [new BusyTarget(device.DeviceId, device.Name, device.Address, device.RaopPort ?? device.Port)], ct, false, null);
                EnsureCapture(ct);
                int selectedLatency = CompatibilityLatency?.Invoke(device.DeviceId) ?? 2000;
                EffectiveLatencyMs = selectedLatency is 1000 or 1500 or 2000 ? selectedLatency : 2000;
                var audio = new float[RaopAudio.FramesPerPacket * 2];
                using var output = await RaopOutputSink.ConnectAsync(device.Address, device.RaopPort ?? device.Port, new RaopOptions
                {
                    LatencyMs = EffectiveLatencyMs, VolumePercent = Muted ? 0 : Volume, VolumeCapPercent = VolumeCapPercent,
                    Txt = device.RaopTxt ?? device.Txt, Password = ReceiverPassword?.Invoke(device.DeviceId), Log = Log.Info,
                    ReadAudio = pcm =>
                    {
                        _fifo.Read(audio);
                        Effects.Process(audio, RaopAudio.FramesPerPacket);
                        for (int i = 0; i < pcm.Length; i++) pcm[i] = float.IsFinite(audio[i])
                            ? (short)Math.Clamp((int)Math.Round(audio[i] * 32767), short.MinValue, short.MaxValue) : (short)0;
                    },
                }, ct, device.DeviceId, device.Name);
                var client = output.Client;
                lock (_lock) { if (!ct.IsCancellationRequested) _raopClient = mine = client; }
                if (mine == null) { client.Dispose(); break; }
                client.VolumeCapPercent = VolumeCapPercent;
                Volume ??= 0;
                PushVolume();
                OwnSession.Began(key, UtcNow()); streamed = true; began = UtcNow();
                Set(StreamState.Streaming, L.F("{0} · AirPlay 1 兼容模式（实验性）", device.Name)); // experimental since 1.1
                using var network = new NetworkWatch(device.Address);
                _net = network;
                lostReason = await output.Lost.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RaopPasswordRequiredException ex)
            {
                DisposeCapture(); Set(StreamState.Idle, ex.Message);
                RaopPasswordRequired?.Invoke(new(device, trigger)); return;
            }
            catch (RaopUnsupportedException ex)
            {
                Log.Warn($"RAOP: {ex.Message}");
                DisposeCapture(); Set(StreamState.Idle, ex.Message);
                return; // FairPlay only: every retry would end the same
            }
            catch (RaopBusyException ex)
            {
                if (trigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver)
                {
                    DisposeCapture(); Set(StreamState.Idle, ex.Message);
                    if (trigger == ConnectTrigger.Manual) RaopTakeoverRequired?.Invoke(new(device, trigger));
                    return; // a confirmed retry is still refused: the receiver decides, never force another sender off
                }
                lostReason = ex.Message;
            }
            catch (Exception ex)
            {
                lostReason = ex.Message;
                rediscover = ex is System.Net.Sockets.SocketException or IOException or TimeoutException;
                Log.Warn($"RAOP: {ex.Message}");
            }
            finally
            {
                if (mine != null)
                {
                    OwnSession.Ended(UtcNow(), clean: ct.IsCancellationRequested);
                    if (Interlocked.CompareExchange(ref _raopClient, null, mine) == mine) mine.Dispose();
                }
            }
            if (ct.IsCancellationRequested) break;
            if (streamed && UtcNow() - began > TakeoverGrace && lostReason == Net.RaopClient.ClosedByReceiver)
            {
                OwnSession.Forget(); DisposeCapture(); Set(StreamState.Idle, L.T(TakenOverText)); return;
            }
            Set(StreamState.Retrying, L.F("{0}，{1:F0} 秒后重试", lostReason, 30));
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
