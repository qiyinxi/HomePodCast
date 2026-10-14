using HomePodCast.Audio;
using HomePodCast.Protocol;
using System.Net;
using System.Security.Cryptography;

namespace HomePodCast.Net;

public sealed partial class AirPlayClient
{
    private PtpMaster? _ptp;
    private BufferedSender? _buffered;
    internal BufferedSender? Buffered => _buffered;
    internal PtpRealtimeSender? PtpRealtime { get; private set; }

    internal static async Task<AirPlayClient> ConnectBufferedAsync(AirPlayDevice device, StreamOptions options,
        AudioFifo fifo, CancellationToken ct, Func<IAacEncoder>? encoder = null,
        PtpPorts? timingPorts = null, bool realtime = false, bool prepareOnly = false)
    {
        if (device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv)
            throw new InvalidOperationException("Apple devices keep their original realtime NTP path");
        if ((ReceiverKinds.Features(device.Txt?.GetValueOrDefault("features") ?? device.Txt?.GetValueOrDefault("ft")) & ReceiverKinds.Ptp) == 0)
            throw new AirPlayException(L.T("缓冲连接需要设备支持 PTP 对时。"));
        var rtsp = await RtspConnection.ConnectAsync(device.Address, device.Port, ct);
        rtsp.PairingOverRtsp = true;
        var client = new AirPlayClient(rtsp);
        try
        {
            await client.SetupBufferedAsync(options, fifo, encoder, timingPorts ?? PtpPorts.Standard, realtime, prepareOnly, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private async Task SetupBufferedAsync(StreamOptions options, AudioFifo fifo, Func<IAacEncoder>? encoder,
        PtpPorts ports, bool realtime, bool prepareOnly, CancellationToken ct)
    {
        VolumeCapPercent = options.VolumeCapPercent;
        if (options.RequestLog != null)
            _rtsp.RequestTrace = (method, uri, status) => options.RequestLog($"AirPlay 2 {(realtime ? "realtime PTP" : "buffered")} {method} {uri}: {status}");
        var info = Expect(_rtsp.Rtsp("GET", "/info"), "GET /info");
        if (info.Body.Length > 0) Info = BPlist.ReadDict(info.Body);
        if (ReceiverKinds.Classify(Info.GetValueOrDefault("model") as string ?? "", null, info: Info)
            is ReceiverKind.HomePod or ReceiverKind.AppleTv)
            throw new InvalidOperationException("Receiver identified itself as Apple; buffered PTP is restricted to third parties");
        var shared = Pair(options);
        _rtsp.EnableEncryption(HapKeys.Derive(shared, "Control-Salt", "Control-Write-Encryption-Key"),
            HapKeys.Derive(shared, "Control-Salt", "Control-Read-Encryption-Key"));
        _ptp = new PtpMaster(_rtsp.LocalIp, new(_rtsp.RemoteIp, ports.PeerEvent), new(_rtsp.RemoteIp, ports.PeerGeneral), true, ports.LocalEvent, ports.LocalGeneral);
        _ptp.Failed += ex => SignalLost(ex.Message);
        var session = Expect(_rtsp.RtspPlist("SETUP", PtpSessionBody(Guid.NewGuid().ToString(), _rtsp.LocalIp, _ptp.ClockId)), "SETUP PTP session");
        int events = Convert.ToInt32(BPlist.ReadDict(session.Body).GetValueOrDefault("eventPort") ?? 0L);
        if (events > 0)
        {
            _events = await EventChannel.ConnectAsync(_rtsp.RemoteIp, events, shared, ct);
            _events.Closed += SignalLost;
        }
        Expect(_rtsp.Rtsp("SETPEERS", contentType: "application/x-apple-binary-plist",
            body: BPlist.Write(new object?[] { _rtsp.RemoteIp.ToString(), _rtsp.LocalIp.ToString() })), "SETPEERS");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        LatencyFrames = options.LatencyMs * BufferedAudio.SampleRate / 1000;
        bool alac = (Info.GetValueOrDefault("model") as string ?? "").StartsWith("Shairport", StringComparison.OrdinalIgnoreCase);
        var setup = Expect(_rtsp.RtspPlist("SETUP", new()
        {
            ["streams"] = new object?[] { new Dictionary<string, object?>
            {
                ["type"] = realtime ? 96 : 103, ["audioFormat"] = realtime ? (alac ? 0x40000 : 0x800) : 0x400000,
                ["ct"] = realtime ? (alac ? 2 : 1) : 3, ["sr"] = 44100,
                ["spf"] = realtime ? 352 : 1024, ["shk"] = key, ["isMedia"] = true, ["audioMode"] = "default",
                ["latencyMin"] = LatencyFrames, ["latencyMax"] = 88200,
                ["controlPort"] = ((IPEndPoint)_control.Client.LocalEndPoint!).Port,
                ["streamConnectionID"] = _rtsp.SessionId, ["supportsDynamicStreamID"] = false,
            } },
        }), "SETUP buffered stream");
        var stream = (BPlist.ReadDict(setup.Body)["streams"] as List<object?>)?[0] as Dictionary<string, object?>
            ?? throw new InvalidDataException("SETUP buffered stream: no streams");
        int port = Convert.ToInt32(stream["dataPort"]);
        if (realtime)
        {
            PtpRealtime = new(_rtsp.LocalIp, _rtsp.RemoteIp, port, Convert.ToInt32(stream["controlPort"]),
                _control, key, _rtsp.SessionId, LatencyFrames, alac, fifo, options.Effects, _ptp);
            PtpRealtime.Failed += ex => SignalLost(ex.Message);
            if (stream.GetValueOrDefault("arrivalToRenderLatencyMs") is long a2r) ArrivalToRenderMs = (int)a2r;
        }
        else
        {
        _buffered = await BufferedSender.ConnectAsync(_rtsp.RemoteIp, port, key, _rtsp.SessionId, fifo,
            options.Effects, _ptp, encoder, ct);
        _buffered.Failed += ex => SignalLost(ex.Message);
        }
        if (!realtime)
        {
            Record();
            SetVolumeDb(options.VolumePercent is { } pct ? PercentToDb(pct) : InitialVolumeDb ?? -20);
            // Initialise the receiver's decoded PCM cursor before its first AAC block. Type 103
            // receivers use FLUSHBUFFERED rather than the realtime RTP-Info FLUSH.
            Expect(_rtsp.RtspPlist("FLUSHBUFFERED", new()
            {
                ["flushUntilSeq"] = 0,
                ["flushUntilTS"] = _buffered!.RtpBase,
            }), "FLUSHBUFFERED");
        }
        // Give the receiver's PTP monitor time to establish the clock before its first anchor.
        // This delay is startup only; live audio thereafter uses the selected 1/1.5/2 second offset.
        await Task.Delay(1000, ct);
        await _ptp.WaitForStableMasterAsync(ct);
        if (realtime)
        {
            if (!prepareOnly) StartPreparedPtpRealtime(options);
            return;
        }
        ulong time = PtpMaster.NowNanoseconds;
        Expect(_rtsp.RtspPlist("SETRATEANCHORTIME", BufferedAudio.Anchor(_ptp.ClockId,
            time + (ulong)options.LatencyMs * 1_000_000, _buffered!.RtpBase)), "SETRATEANCHORTIME");
        _buffered!.Start();
        StartFeedback();
    }

    internal void StartPreparedPtpRealtime(StreamOptions options)
    {
        Record();
        SetVolumeDb(options.VolumePercent is { } pct ? PercentToDb(pct) : InitialVolumeDb ?? -20);
        Flush(PtpRealtime!.FirstSeq, PtpRealtime.RtpBase);
        PtpRealtime.Start(); StartFeedback();
    }

    internal static Dictionary<string, object?> PtpSessionBody(string uuid, IPAddress local, ulong clock)
    {
        var peer = new Dictionary<string, object?>
        {
            ["ID"] = uuid, ["DeviceType"] = 0, ["ClockID"] = clock,
            ["SupportsClockPortMatchingOverride"] = false, ["Addresses"] = new object?[] { local.ToString() },
        };
        return new()
        {
            ["deviceID"] = "02:48:50:43:41:53", ["macAddress"] = "02:48:50:43:41:53",
            ["name"] = "HomePodCast", ["sessionUUID"] = uuid, ["groupUUID"] = uuid,
            ["groupContainsGroupLeader"] = false, ["timingProtocol"] = "PTP",
            ["timingPeerInfo"] = peer, ["timingPeerList"] = new object?[] { peer },
        };
    }
}

internal sealed class BufferedReceiverSession(AirPlayClient client) : IReceiverSession
{
    public ReceiverProtocol Protocol => ReceiverProtocol.AirPlay2Buffered;
    public IReadOnlyDictionary<string, object?> Info => client.Info;
    public int LatencyMs => client.LatencyFrames * 1000 / BufferedAudio.SampleRate;
    public int? ArrivalToRenderMs => null;
    public double? InitialVolumeDb => client.InitialVolumeDb;
    public double VolumeCapPercent { get => client.VolumeCapPercent; set => client.VolumeCapPercent = value; }
    public long PacketsSent => client.Buffered?.PacketsSent ?? 0;
    public string? LostReason => client.LostReason;
    public Action<long>? OnsetSent { get => client.Buffered?.OnsetSent; set { if (client.Buffered is { } sender) sender.OnsetSent = value; } }
    public event Action<string>? Lost { add => client.Lost += value; remove => client.Lost -= value; }
    public void SetVolume(double percent) => client.SetVolumePercent(percent);
    public void Dispose() => client.Dispose();
}
