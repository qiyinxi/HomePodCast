using System.Net;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

// experimental since 1.4
// An experiment for one question (`stream --ptp-realtime --apple-tv`): does an Apple TV whose output is its home-theater
// HomePods pass on a realtime (type 96) stream when the session is timed with PTP, as an Apple sender's is? 2026-11-12:
// with NTP timing the realtime stream, and with PTP timing the buffered one (AirPlayClient.Surround.cs), it accepts
// everything and plays nothing; this is the combination that was left. The session follows the Apple TV's own PTP clock,
// as the 5.1 path does, and writes its sync packets in that timeline. Nothing in the app uses it.
public sealed partial class AirPlayClient
{
    internal static async Task<AirPlayClient> ConnectAppleTvPtpRealtimeAsync(IPAddress host, int port, StreamOptions options,
        AudioFifo fifo, CancellationToken ct)
    {
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        var client = new AirPlayClient(rtsp);
        try
        {
            await client.SetupAppleTvPtpRealtimeAsync(options, fifo, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task SetupAppleTvPtpRealtimeAsync(StreamOptions options, AudioFifo fifo, CancellationToken ct)
    {
        VolumeCapPercent = options.VolumeCapPercent;
        bool modernRequested = options.StreamSetupOverrides?.ContainsKey("streamConnections") == true;
        if (options.RequestLog != null)
            _rtsp.RequestTrace = (method, uri, status) => options.RequestLog($"AirPlay 2 realtime PTP {method} {uri}: {status}");
        var info = Expect(_rtsp.Rtsp("GET", "/info"), "GET /info");
        if (info.Body.Length > 0) Info = BPlist.ReadDict(info.Body);
        string model = Info.GetValueOrDefault("model") as string ?? "";
        // Before pairing: only an Apple TV, and Pair never starts a new pairing by itself (no PIN on the TV).
        if (ReceiverKinds.Classify(model, null, info: Info) != ReceiverKind.AppleTv)
            throw new AirPlayException($"the realtime PTP experiment is for an Apple TV, not {model}");

        var shared = Pair(options);
        _rtsp.EnableEncryption(
            HapKeys.Derive(shared, "Control-Salt", "Control-Write-Encryption-Key"),
            HapKeys.Derive(shared, "Control-Salt", "Control-Read-Encryption-Key"));

        // Bound before the session SETUP, as in the 5.1 path: ports in use never take the Apple TV.
        ulong clockId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fff_ffff_ffff_ffff;
        _follower = PtpFollower.Bind(_rtsp.LocalIp, _rtsp.RemoteIp, clockId, PtpPorts.Standard, TimeSpan.FromSeconds(30));
        _follower.Failed += reason => SignalLost(reason);

        string uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var body = SessionSetupBody(uuid, 0, null);
        body.Remove("timingPort");
        foreach (var (key, value) in PtpSessionBody(uuid, _rtsp.LocalIp, clockId)) body[key] = value;
        if (modernRequested)
        {
            body["isScreenMirroringSession"] = true;
            body["combinedGetInfoWithControlSetup"] = true;
            body["updateSessionRequest"] = false;
            body["sourceVersion"] = "980.71.1";
            body["osBuildVersion"] = "13F69";
            if (body.GetValueOrDefault("timingPeerInfo") is Dictionary<string, object?> peer)
                peer["SupportsClockPortMatchingOverride"] = true;
        }
        if (options.SessionSetupExtras != null) foreach (var (key, value) in options.SessionSetupExtras) body[key] = value;
        if (Log.Verbose) Log.Debug($"realtime PTP SETUP session request: {BPlist.Describe(body)}");
        var session = BPlist.ReadDict(Expect(_rtsp.RtspPlist("SETUP", body), "SETUP session").Body);
        if (Log.Verbose) Log.Debug($"realtime PTP SETUP session reply: {BPlist.Describe(session)}");
        _events = await EventChannel.ConnectAsync(_rtsp.RemoteIp, Convert.ToInt32(session.GetValueOrDefault("eventPort") ?? 0L), shared, ct);
        _events.Closed += SignalLost;

        var peers = _rtsp.Rtsp("SETPEERS", contentType: "application/x-apple-binary-plist",
            body: BPlist.Write(new object?[] { _rtsp.RemoteIp.ToString(), _rtsp.LocalIp.ToString() }));
        if (!peers.IsSuccess) Log.Warn($"realtime PTP: SETPEERS answered {peers.StartLine}; continuing");
        if (modernRequested) Record();

        byte[] streamKey = RandomNumberGenerator.GetBytes(32);
        int latencyFrames = Math.Max(RtpSender.FramesPerPacket * 4, options.LatencyMs * RtpSender.SampleRate / 1000);
        var streamDesc = new Dictionary<string, object?>
        {
            ["audioFormat"] = 0x800, ["audioMode"] = "default",
            ["controlPort"] = ((IPEndPoint)_control.Client.LocalEndPoint!).Port,
            ["ct"] = 1, ["isMedia"] = true,
            ["latencyMax"] = modernRequested ? latencyFrames : Math.Max(latencyFrames, 88200),
            ["latencyMin"] = modernRequested ? 0 : latencyFrames,
            ["shk"] = streamKey, ["spf"] = RtpSender.FramesPerPacket, ["sr"] = RtpSender.SampleRate, ["type"] = 96,
            ["supportsDynamicStreamID"] = false, ["streamConnectionID"] = (long)_rtsp.SessionId,
        };
        if (modernRequested)
        {
            streamDesc["usingScreen"] = true;
            streamDesc["streamConnectionID"] = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
        }
        if (options.StreamSetupOverrides is { } overrides)
            foreach (var (key, value) in overrides) streamDesc[key] = value;
        if (streamDesc.GetValueOrDefault("streamConnections") is Dictionary<string, object?> requestedConnections &&
            requestedConnections.ContainsKey("streamConnectionTypeRTP"))
        {
            requestedConnections["streamConnectionTypeRTCP"] = new Dictionary<string, object?>
            {
                ["streamConnectionKeyPort"] = ((IPEndPoint)_control.Client.LocalEndPoint!).Port,
            };
        }
        var setup = BPlist.ReadDict(Expect(_rtsp.RtspPlist("SETUP", new() { ["streams"] = new List<object?> { streamDesc } }), "SETUP stream").Body);
        if (Log.Verbose) Log.Debug($"realtime PTP SETUP stream reply: {BPlist.Describe(setup)}");
        var stream = (setup.GetValueOrDefault("streams") as List<object?>)?[0] as Dictionary<string, object?>
            ?? throw new InvalidDataException("SETUP stream: no streams in reply");
        if (stream.GetValueOrDefault("arrivalToRenderLatencyMs") is long a2r) ArrivalToRenderMs = (int)a2r;
        LatencyFrames = latencyFrames;

        await Task.Delay(1000, ct);
        if (!await _follower.SettleAsync(TimeSpan.FromSeconds(5), ct))
            throw new AirPlayException("no PTP Sync from the Apple TV within 5 s");
        var follower = _follower;
        var timeline = new PtpTimeline(
            ticks => follower.TimeAt(ticks) ?? throw new IOException("the Apple TV's PTP clock is gone"),
            () => follower.TimelineId ?? throw new IOException("the Apple TV's PTP clock is gone"),
            () => follower.Problem == null);
        int dataPort, controlPort;
        bool modernStream = false;
        uint streamSsrc = unchecked((uint)Random.Shared.NextInt64(1, uint.MaxValue));
        if (stream.ContainsKey("dataPort"))
        {
            dataPort = Convert.ToInt32(stream["dataPort"]);
            controlPort = Convert.ToInt32(stream["controlPort"]);
        }
        else if (stream.GetValueOrDefault("streamConnections") is Dictionary<string, object?> connections &&
                 connections.GetValueOrDefault("streamConnectionTypeRTP") is Dictionary<string, object?> rtp &&
                 rtp.GetValueOrDefault("streamConnectionKeyPort") is { } rtpPort)
        {
            dataPort = Convert.ToInt32(rtpPort); controlPort = dataPort; modernStream = true;
            if (connections.GetValueOrDefault("streamConnectionTypeRTCP") is Dictionary<string, object?> rtcp &&
                rtcp.GetValueOrDefault("streamConnectionKeyPort") is { } rtcpPort)
                controlPort = Convert.ToInt32(rtcpPort);
        }
        else throw new InvalidDataException("SETUP stream: no RTP port");
        PtpRealtime = new PtpRealtimeSender(_rtsp.LocalIp, _rtsp.RemoteIp, dataPort,
            controlPort, _control, streamKey, streamSsrc, latencyFrames, alac: false, fifo,
            options.Effects, timeline, modernStream: modernStream);
        PtpRealtime.Failed += ex => SignalLost(ex.Message);
        Log.Info($"realtime PTP stream ready: arrivalToRenderLatency={stream.GetValueOrDefault("arrivalToRenderLatencyMs")}ms " +
                 $"latency={options.LatencyMs}ms, timeline 0x{follower.TimelineId:X16}, {follower.Describe}");
        if (modernStream)
        {
            SetVolumeDb(options.VolumePercent is { } pct ? PercentToDb(pct) : InitialVolumeDb ?? -20);
            PtpRealtime.Start();
            StartFeedback();
        }
        else StartPreparedPtpRealtime(options);
    }

    /// <summary>
    /// POST /command on the session's own connection. 2026-11-12, an iPhone's requests read in plain text through a proxy:
    /// it tells the Apple TV its playback state this way ("updateMRPlaybackState", mrPlaybackState 1 = playing, 2 =
    /// paused), and status-flags bit 20 of the Apple TV and of its home-theater HomePods follows that state.
    /// </summary>
    internal bool SendCommand(string type, Dictionary<string, object?> parameters)
    {
        var reply = _rtsp.RtspPlist("POST", new() { ["type"] = type, ["params"] = parameters }, "/command");
        Log.Info($"POST /command {type} {BPlist.Describe(parameters)}: {reply.StartLine}");
        return reply.IsSuccess;
    }
}
