using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

public sealed record StreamOptions(int LatencyMs, double? VolumePercent, bool LatencyInSync = false)
{
    /// <summary>No volume above this is ever sent, including the speaker's own initial volume (percent).</summary>
    public double VolumeCapPercent { get; init; } = 100;

    /// <summary>In-place processing of every packet on the sender thread (EQ, night mode).</summary>
    public IAudioEffect? Effects { get; init; }

    /// <summary>Experiments only (`stream --setup key=value`): keys added to or replaced in the stream SETUP.</summary>
    public IReadOnlyDictionary<string, object?>? StreamSetupOverrides { get; init; }

    /// <summary>
    /// Group members only (stereo pair / multi-room): keys added to or replaced in the session SETUP, such as
    /// the group's shared "groupUUID". Null for a single speaker, whose session SETUP stays as it always was.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? SessionSetupExtras { get; init; }
}

/// <summary>
/// One AirPlay 2 realtime audio session to one speaker:
/// transient pair-setup → encrypted RTSP → SETUP (session) → event channel → SETUP (stream)
/// → RECORD/FLUSH → RTP + sync, with /feedback keep-alives.
/// </summary>
public sealed class AirPlayClient : IDisposable, IGroupMember
{
    private const string PairUserAgent = "AirPlay/320.20";
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromSeconds(2);

    private readonly RtspConnection _rtsp;
    private readonly TimingServer _timing;
    private readonly UdpClient _control;
    private EventChannel? _events;
    private RtpSender? _sender;
    private System.Threading.Timer? _feedbackTimer;
    private int _feedbackFailures;
    private int _lostSignalled;

    public IReadOnlyDictionary<string, object?> Info { get; private set; } = new Dictionary<string, object?>();
    public RtpSender? Sender => _sender;
    public int TimingRequests => _timing.RequestsAnswered;
    public double? InitialVolumeDb => Info.TryGetValue("initialVolume", out var v) && v is double d ? d : null;

    /// <summary>Time the speaker says it needs from packet arrival to playout (from the stream SETUP reply).</summary>
    public int? ArrivalToRenderMs { get; private set; }

    /// <summary>Playout delay requested in the stream SETUP, in frames.</summary>
    public int LatencyFrames { get; private set; }

    public string Name => Info.GetValueOrDefault("name") as string ?? _rtsp.RemoteIp.ToString();

    /// <summary>Where this session's RTP goes (set by the stream SETUP).</summary>
    internal RtpTarget? StreamTarget { get; private set; }

    /// <summary>Ceiling applied by <see cref="SetVolumeDb"/> to everything sent (percent).</summary>
    public double VolumeCapPercent { get; set; } = 100;

    /// <summary>Raised once when the session dies (speaker closed it, network gone, taken over...).</summary>
    public event Action<string>? Lost;

    private AirPlayClient(RtspConnection rtsp)
    {
        _rtsp = rtsp;
        _timing = new TimingServer(rtsp.LocalIp);
        _control = new UdpClient(new IPEndPoint(rtsp.LocalIp, 0));
    }

    public static async Task<AirPlayClient> ConnectAsync(IPAddress host, int port, StreamOptions options,
        AudioFifo fifo, CancellationToken ct)
    {
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        var client = new AirPlayClient(rtsp);
        try
        {
            await client.SetupAsync(options, fifo, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Experimental group member (stereo pair / multi-room): the complete session up to and including the
    /// stream SETUP, with the /feedback heartbeat running but no sender. SpeakerGroup then drives one
    /// shared RtpSender for all members and calls Record / Flush on each.
    /// </summary>
    internal static async Task<AirPlayClient> PrepareAsync(IPAddress host, int port, StreamOptions options,
        ChannelMode channels, CancellationToken ct)
    {
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        var client = new AirPlayClient(rtsp);
        try
        {
            client.StreamTarget = await client.NegotiateAsync(options, ct) with { Channels = channels };
            Log.Info($"group member {client.Name} ({host}): {channels}, /info keys: {string.Join(',', client.Info.Keys)}");
            client.StartFeedback();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task SetupAsync(StreamOptions options, AudioFifo fifo, CancellationToken ct)
    {
        var target = await NegotiateAsync(options, ct);

        // --- start: anchor the timeline a little in the future so RECORD/FLUSH fit before packet 0
        _sender = new RtpSender(target.Control, target.Remote, target.DataPort, target.ControlPort, target.StreamKey,
            target.Ssrc, LatencyFrames, fifo) { LatencyInSync = options.LatencyInSync, Effects = options.Effects };
        _sender.Start(MediaClock.Now + MediaClock.FromMs(250));

        StartFeedback();
        Record();
        Flush(_sender.FirstSeq, _sender.RtpBase);

        double volumeDb = options.VolumePercent is { } pct ? PercentToDb(pct) : InitialVolumeDb ?? -20.0;
        SetVolumeDb(volumeDb);
    }

    /// <summary>/info, transient pairing, encrypted session SETUP, event channel and stream SETUP.</summary>
    private async Task<RtpTarget> NegotiateAsync(StreamOptions options, CancellationToken ct)
    {
        VolumeCapPercent = options.VolumeCapPercent;
        var info = _rtsp.Rtsp("GET", "/info");
        if (info.IsSuccess && info.Body.Length > 0)
            Info = BPlist.ReadDict(info.Body);
        Log.Info($"connected to {Info.GetValueOrDefault("name")} ({Info.GetValueOrDefault("model")}, " +
                 $"AirTunes/{Info.GetValueOrDefault("sourceVersion")}, OS {Info.GetValueOrDefault("osBuildVersion")})");

        var shared = PairTransient();
        _rtsp.EnableEncryption(
            HapKeys.Derive(shared, "Control-Salt", "Control-Write-Encryption-Key"),
            HapKeys.Derive(shared, "Control-Salt", "Control-Read-Encryption-Key"));

        // --- session SETUP
        if (options.SessionSetupExtras is { } extras)
            Log.Info($"session SETUP extras: {string.Join(", ", extras.Select(o => $"{o.Key}={o.Value}"))}");
        var setup1 = Expect(_rtsp.RtspPlist("SETUP",
            SessionSetupBody(Guid.NewGuid().ToString().ToUpperInvariant(), _timing.Port, options.SessionSetupExtras)), "SETUP session");
        var s1 = BPlist.ReadDict(setup1.Body);
        int eventPort = Convert.ToInt32(s1.GetValueOrDefault("eventPort") ?? 0L);
        Log.Debug($"SETUP session -> eventPort={eventPort} timingPort={s1.GetValueOrDefault("timingPort")}");

        _events = await EventChannel.ConnectAsync(_rtsp.RemoteIp, eventPort, shared, ct);
        _events.Closed += SignalLost;

        // --- stream SETUP
        int latencyFrames = Math.Max(RtpSender.FramesPerPacket * 4, options.LatencyMs * RtpSender.SampleRate / 1000);
        var streamKey = RandomNumberGenerator.GetBytes(32);
        var streamDesc = new Dictionary<string, object?>
        {
            ["audioFormat"] = 0x800,          // PCM 44100/16/2
            ["audioMode"] = "default",
            ["controlPort"] = ((IPEndPoint)_control.Client.LocalEndPoint!).Port,
            ["ct"] = 1,                        // raw PCM, as pyatv sends (see TODO(ALAC) in RtpSender.EncodePayloads)
            ["isMedia"] = true,
            ["latencyMax"] = Math.Max(latencyFrames, 88200),
            ["latencyMin"] = latencyFrames,
            ["shk"] = streamKey,
            ["spf"] = RtpSender.FramesPerPacket,
            ["sr"] = RtpSender.SampleRate,
            ["type"] = 96,                     // realtime audio
            ["supportsDynamicStreamID"] = false,
            ["streamConnectionID"] = (long)_rtsp.SessionId,
        };
        if (options.StreamSetupOverrides is { } overrides)
        {
            foreach (var (key, value) in overrides) streamDesc[key] = value;
            Log.Info($"stream SETUP overrides: {string.Join(", ", overrides.Select(o => $"{o.Key}={o.Value}"))}");
        }
        var setup2 = Expect(SetupStream(new Dictionary<string, object?>
        {
            ["streams"] = new List<object?> { streamDesc },
        }), "SETUP stream");
        var stream = (BPlist.ReadDict(setup2.Body)["streams"] as List<object?>)?[0] as Dictionary<string, object?>
                     ?? throw new InvalidDataException("SETUP stream: no streams in reply");
        int dataPort = Convert.ToInt32(stream["dataPort"]);
        int controlPort = Convert.ToInt32(stream["controlPort"]);
        if (stream.GetValueOrDefault("arrivalToRenderLatencyMs") is long a2r) ArrivalToRenderMs = (int)a2r;
        Log.Info($"stream ready: data={dataPort} control={controlPort} " +
                 $"arrivalToRenderLatency={stream.GetValueOrDefault("arrivalToRenderLatencyMs")}ms latency={options.LatencyMs}ms " +
                 $"sync={(options.LatencyInSync ? "legacy" : "plain")}");

        LatencyFrames = latencyFrames;
        return new RtpTarget(_control, _rtsp.RemoteIp, dataPort, controlPort, streamKey, _rtsp.SessionId);
    }

    /// <summary>
    /// The session SETUP plist. Without extras (a single speaker) these are exactly the keys, values and order
    /// sent before groups existed; a group member gets its extras added or replaced after them.
    /// Like pyatv's realtime sender: NTP timing, isMultiSelectAirPlay, and groupContainsGroupLeader false (what
    /// the iOS Music app sends, per OwnTone; members of an iOS group advertise gcgl=0 while playing).
    /// </summary>
    /// <remarks>
    /// TODO(PTP): Apple senders time HomePods (features bit 41) with PTP, and a stereo pair's own tight sync
    /// runs on it. Public evidence (OwnTone 28.x, NTP-only until 29.1) says two NTP sessions to the members of a
    /// pair play correctly split stereo, so PTP is not required; it may still hold the two sides closer together.
    /// What a PTP session would need, from OwnTone's PTP path and shairport-sync/nqptp:
    ///  1. a PTP clock on this PC (IEEE 1588 Sync/Follow_Up on UDP 319/320; announce as grandmaster, or follow
    ///     the HomePods' clock) with a 64-bit ClockID;
    ///  2. here: timingProtocol "PTP" instead of timingPort/"NTP", plus groupUUID, timingPeerInfo
    ///     {Addresses, ID, ClockID, DeviceType, SupportsClockPortMatchingOverride} and timingPeerList
    ///     (this PC and every member);
    ///  3. SETPEERS (or SETPEERSX, features bit 52) with all member addresses on every session after SETUP;
    ///  4. in RtpSender: PTP-style sync packets carrying the PTP time and ClockID instead of 0xD4 NTP ones.
    /// Hook: TimingServer would become one of two timing back-ends chosen per group; the sync-packet writer is
    /// RtpSender.WriteSyncPacket. Not implemented: a PTP master is a project of its own, and nothing shows a
    /// pair needs it.
    /// </remarks>
    internal static Dictionary<string, object?> SessionSetupBody(string sessionUuid, int timingPort,
        IReadOnlyDictionary<string, object?>? extras)
    {
        var body = new Dictionary<string, object?>
        {
            ["deviceID"] = "02:48:50:43:41:53",
            ["macAddress"] = "02:48:50:43:41:53",
            ["sessionUUID"] = sessionUuid,
            ["timingPort"] = timingPort,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["senderSupportsRelay"] = false,
            ["statsCollectionEnabled"] = false,
            ["name"] = Environment.MachineName,
            ["model"] = "HomePodCast",
            ["osName"] = "Windows",
            ["osVersion"] = Environment.OSVersion.Version.ToString(),
            ["sourceVersion"] = "690.7.1",
        };
        if (extras != null)
            foreach (var (key, value) in extras) body[key] = value;
        return body;
    }

    private void StartFeedback() =>
        _feedbackTimer = new System.Threading.Timer(_ => Feedback(), null, TimeSpan.Zero, FeedbackInterval);

    public void Record() => Expect(_rtsp.Rtsp("RECORD"), "RECORD");

    /// <summary>Tell the speaker where the stream starts: the first packet's sequence number and RTP time.</summary>
    public void Flush(ushort firstSeq, uint rtpBase) => Expect(_rtsp.Rtsp("FLUSH", extra:
    [
        new("Range", "npt=0-"),
        new("Session", "0"),
        new("RTP-Info", $"seq={firstSeq};rtptime={rtpBase}"),
    ]), "FLUSH");

    RtpTarget IGroupMember.Target => StreamTarget ?? throw new InvalidOperationException("stream not set up");

    /// <summary>
    /// The speaker only answers the stream SETUP once it has synced to our NTP clock, so a timeout here
    /// with zero timing requests means its packets never reached us (almost always the firewall).
    /// </summary>
    private HttpMessage SetupStream(Dictionary<string, object?> body)
    {
        try
        {
            return _rtsp.RtspPlist("SETUP", body);
        }
        catch (IOException) when (_timing.RequestsAnswered == 0)
        {
            throw new FirewallBlockedException();
        }
    }

    /// <summary>HAP transient pairing (pair-setup M1..M4 with PIN 3939); returns the SRP session key.</summary>
    private byte[] PairTransient()
    {
        var headers = new KeyValuePair<string, string>[]
        {
            new("Connection", "keep-alive"),
            new("X-Apple-HKP", "4"),
        };
        const string octet = "application/octet-stream";

        _rtsp.Http("POST", "/pair-pin-start", PairUserAgent, octet, null, headers);

        var m2 = Expect(_rtsp.Http("POST", "/pair-setup", PairUserAgent, octet,
            Tlv8.Write((Tlv8.Method, [0]), (Tlv8.State, [1]), (Tlv8.Flags, [Tlv8.FlagTransient])), headers), "pair-setup M1");
        var t2 = Tlv8.Read(m2.Body);
        ThrowIfTlvError(t2, "M2");

        var srp = new SrpClient("Pair-Setup", "3939");
        var proof = srp.ComputeProof(t2[Tlv8.Salt], t2[Tlv8.PublicKey]);

        var m4 = Expect(_rtsp.Http("POST", "/pair-setup", PairUserAgent, octet,
            Tlv8.Write((Tlv8.State, [3]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, proof)), headers), "pair-setup M3");
        ThrowIfTlvError(Tlv8.Read(m4.Body), "M4");
        Log.Debug("transient pairing ok");
        return srp.SessionKey!;
    }

    private static void ThrowIfTlvError(Dictionary<byte, byte[]> tlv, string step)
    {
        if (tlv.TryGetValue(Tlv8.Error, out var err))
            throw new AirPlayException($"pairing rejected at {step} (TLV error {err[0]})");
    }

    private static HttpMessage Expect(HttpMessage resp, string what) =>
        resp.IsSuccess ? resp : throw new AirPlayException($"{what} failed: {resp.StartLine}");

    private void Feedback()
    {
        try
        {
            var r = _rtsp.Rtsp("POST", "/feedback");
            _feedbackFailures = r.IsSuccess ? 0 : _feedbackFailures + 1;
        }
        catch (Exception ex)
        {
            _feedbackFailures++;
            Log.Debug($"feedback failed: {ex.Message}");
        }
        if (_feedbackFailures >= 3) SignalLost(L.T("音箱没有响应（网络中断？）"));
    }

    private void SignalLost(string reason)
    {
        if (Interlocked.Exchange(ref _lostSignalled, 1) == 0)
        {
            Log.Warn($"session lost: {reason}");
            LostReason = reason;
            Lost?.Invoke(reason);
        }
    }

    /// <summary>Why the session ended, once it has (null while alive, and after a deliberate Dispose).</summary>
    public string? LostReason { get; private set; }

    public static double PercentToDb(double pct) => pct <= 0 ? -144.0 : -30.0 + 30.0 * Math.Clamp(pct, 0, 100) / 100.0;

    public static double DbToPercent(double db) => db <= -30 ? 0 : Math.Clamp((db + 30.0) / 30.0 * 100.0, 0, 100);

    public void SetVolumeDb(double db)
    {
        db = VolumeLimit.ClampDb(db, VolumeCapPercent);
        var body = System.Text.Encoding.UTF8.GetBytes($"volume: {db.ToString("F6", CultureInfo.InvariantCulture)}");
        _rtsp.Rtsp("SET_PARAMETER", contentType: "text/parameters", body: body);
    }

    public void SetVolumePercent(double pct) => SetVolumeDb(PercentToDb(pct));

    public void Dispose()
    {
        _lostSignalled = 1; // no Lost events during deliberate teardown
        _feedbackTimer?.Dispose();
        _sender?.Dispose();
        try { _rtsp.Rtsp("TEARDOWN", extra: [new("Session", "0")]); } catch { }
        _events?.Dispose();
        _control.Dispose();
        _timing.Dispose();
        _rtsp.Dispose();
    }
}

public class AirPlayException(string message) : Exception(message);

public sealed class FirewallBlockedException() : AirPlayException(
    L.T("音箱发来的对时请求没有到达本程序，通常是 Windows 防火墙拦截了 HomePodCast.exe 的入站连接"));
