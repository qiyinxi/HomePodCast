using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>Protocol trials for third-party receivers. Apple sessions keep their original entry points.</summary>
internal static class ThirdPartyConnections
{
    internal delegate Task<IReceiverSession> Connector(AirPlayDevice device, ReceiverPreferences? settings,
        ReceiverConnectOptions options, AudioFifo fifo, CancellationToken ct);

    public static async Task<IReceiverSession> ConnectAsync(AirPlayDevice device, ReceiverPreferences? settings,
        ReceiverConnectOptions options, AudioFifo fifo, CancellationToken ct, Connector? buffered = null,
        Func<ReceiverProtocol, Connector?>? overrides = null)
    {
        if (device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv)
            throw new InvalidOperationException("Apple sessions do not use third-party protocol trials");
        var failures = new List<string>();
        PairingRequiredException? pairing = null;
        foreach (var protocol in ReceiverProtocols.Candidates(device, settings))
        {
            ct.ThrowIfCancellationRequested();
            options.Log?.Invoke($"receiver protocol attempt: {protocol} {device.Address}:{(protocol == ReceiverProtocol.Raop ? device.RaopPort ?? device.Port : device.Port)}");
            try
            {
                Connector connect = overrides?.Invoke(protocol) ?? protocol switch
                {
                    ReceiverProtocol.AirPlay2Realtime => RealtimeAsync,
                    ReceiverProtocol.Raop => LegacyAsync,
                    ReceiverProtocol.AirPlay2Buffered => buffered ?? BufferedAsync,
                    _ => throw new InvalidOperationException("No automatic protocol inside a trial"),
                };
                var session = await connect(device, settings, options, fifo, ct);
                if (session.LostReason != null)
                {
                    string reason = session.LostReason;
                    session.Dispose();
                    throw new AirPlayException(reason);
                }
                return session;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AirPlayException ex) when (ex.ResponseStatusCode == 453) { throw new RaopBusyException(); }
            catch (RaopBusyException) { throw; } // a refusal is never bypassed by trying another mode
            catch (RaopPasswordRequiredException) { throw; }
            catch (PairingRequiredException ex)
            {
                pairing = ex;
                failures.Add($"{protocol}: {ex.Message}");
                options.Log?.Invoke($"receiver protocol refused: {protocol}: {AirPlayException.ForLog(ex)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{protocol}: {ex.Message}");
                options.Log?.Invoke($"receiver protocol refused: {protocol}: {ex.Message}");
            }
        }
        if (pairing != null) throw pairing;
        throw new AirPlayException(L.F("这台设备没有接受可用的连接方式：{0}", string.Join("; ", failures)));
    }

    private static async Task<IReceiverSession> RealtimeAsync(AirPlayDevice device, ReceiverPreferences? settings,
        ReceiverConnectOptions options, AudioFifo fifo, CancellationToken ct)
    {
        var policy = ReceiverLatencies.For(device.Kind, ReceiverProtocol.AirPlay2Realtime, settings?.ArrivalToRenderMs, device.Model);
        int wanted = settings?.RealtimeLatencyMs ?? Math.Max(policy.DefaultMs, policy.RecommendedMinimumMs);
        if (policy.MinimumMs > policy.MaximumMs) throw new AirPlayException(L.T("这台设备的处理时间超过低延迟范围，请使用兼容或缓冲连接。"));
        int latency = policy.Clamp(wanted);
        AirPlayClient? prepared = null;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var stream = new StreamOptions(latency, options.VolumePercent)
                {
                    VolumeCapPercent = options.VolumeCapPercent, Effects = options.Effects,
                    Pairings = options.Pairings, DeviceId = device.DeviceId, RequestLog = options.Log, AllowMissingEventChannel = true, PairingOverRtsp = true,
                };
                bool ptp = (ReceiverKinds.Features(device.Txt?.GetValueOrDefault("features") ?? device.Txt?.GetValueOrDefault("ft")) & ReceiverKinds.Ptp) != 0;
                prepared = ptp
                    ? await AirPlayClient.ConnectBufferedAsync(device, stream, fifo, ct, timingPorts: options.TimingPorts, realtime: true, prepareOnly: true)
                    : await AirPlayClient.PrepareAsync(device.Address, device.Port, stream, ChannelMode.Stereo, ct);
                var actual = ReceiverLatencies.For(device.Kind, ReceiverProtocol.AirPlay2Realtime, prepared.ArrivalToRenderMs, device.Model);
                if (actual.MinimumMs > actual.MaximumMs) throw new AirPlayException(L.T("这台设备的处理时间超过低延迟范围，请使用兼容或缓冲连接。"));
                int next = actual.Clamp(settings?.RealtimeLatencyMs ?? Math.Max(actual.DefaultMs, actual.RecommendedMinimumMs));
                if (next > latency)
                {
                    if (pass == 1) throw new AirPlayException("receiver processing latency changed during negotiation");
                    options.Log?.Invoke($"receiver realtime floor: {latency} -> {next} ms, arrivalToRender={prepared.ArrivalToRenderMs}");
                    prepared.Dispose(); prepared = null; latency = next; continue;
                }
                if (ptp) prepared.StartPreparedPtpRealtime(stream);
                else prepared.StartPreparedStream(stream, fifo);
                return new RealtimeReceiverSession(prepared);
            }
            throw new AirPlayException("receiver realtime negotiation did not converge");
        }
        catch { prepared?.Dispose(); throw; }
    }

    private static async Task<IReceiverSession> BufferedAsync(AirPlayDevice device, ReceiverPreferences? settings,
        ReceiverConnectOptions options, AudioFifo fifo, CancellationToken ct)
    {
        int latency = settings?.CompatibilityLatencyMs ?? 2000;
        if (!ReceiverLatencies.CompatibilityChoices.Contains(latency)) latency = 2000;
        var stream = new StreamOptions(latency, options.VolumePercent)
        {
            VolumeCapPercent = options.VolumeCapPercent, Effects = options.Effects,
            Pairings = options.Pairings, DeviceId = device.DeviceId, RequestLog = options.Log,
        };
        return new BufferedReceiverSession(await AirPlayClient.ConnectBufferedAsync(device, stream, fifo, ct, timingPorts: options.TimingPorts));
    }

    private static async Task<IReceiverSession> LegacyAsync(AirPlayDevice device, ReceiverPreferences? settings,
        ReceiverConnectOptions options, AudioFifo fifo, CancellationToken ct)
    {
        var audio = new float[RaopAudio.FramesPerPacket * 2];
        int latency = settings?.CompatibilityLatencyMs ?? 2000;
        if (!ReceiverLatencies.CompatibilityChoices.Contains(latency)) latency = 2000;
        var client = await RaopClient.ConnectAsync(device.Address, device.RaopPort ?? device.Port, new RaopOptions
        {
            LatencyMs = latency, VolumePercent = options.VolumePercent, VolumeCapPercent = options.VolumeCapPercent,
            Password = options.Password, Txt = device.RaopTxt ?? device.Txt, Log = options.Log,
            ReadAudio = pcm =>
            {
                fifo.Read(audio); options.Effects.Process(audio, RaopAudio.FramesPerPacket);
                for (int i = 0; i < pcm.Length; i++) pcm[i] = float.IsFinite(audio[i])
                    ? (short)Math.Clamp((int)Math.Round(audio[i] * 32767), short.MinValue, short.MaxValue) : (short)0;
            },
        }, ct);
        return new LegacyReceiverSession(client, device);
    }
}
