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
    /// Also experiments (`stream --session key=value`).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? SessionSetupExtras { get; init; }

    /// <summary>
    /// Stored HAP pairings (receivers that need a PIN pairing, such as an Apple TV), looked up by <see cref="DeviceId"/>.
    /// Null: no stored pairing is used (a receiver that needs one then fails with <see cref="PairingRequiredException"/>).
    /// </summary>
    public PairingStore? Pairings { get; init; }

    /// <summary>The receiver's device id (TXT "deviceid"); null: the one its /info reports ("deviceID").</summary>
    public string? DeviceId { get; init; }

    /// <summary>Third-party diagnostics only: request path and status, never body or credentials.</summary>
    public Action<string>? RequestLog { get; init; }

    /// <summary>Some third-party receivers omit the optional event channel. Default preserves the HomePod path.</summary>
    public bool AllowMissingEventChannel { get; init; }

    /// <summary>Third-party pairing over RTSP. HomePods and Apple TVs retain HTTP by default.</summary>
    internal bool PairingOverRtsp { get; init; }
}

/// <summary>
/// One AirPlay 2 realtime audio session to one speaker:
/// transient pair-setup (HomePods) or pair-verify with a stored PIN pairing (Apple TV) → encrypted RTSP → SETUP (session)
/// → event channel → SETUP (stream) → RECORD/FLUSH → RTP + sync, with /feedback keep-alives.
/// </summary>
public sealed partial class AirPlayClient : IDisposable, IGroupMember
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
    private int _feedbackBusy;
    private volatile bool _lostByFeedback;
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
        // PtpRealtimeSender reads resend requests from this socket and ends the session on any error.
        _control = new UdpClient(new IPEndPoint(rtsp.LocalIp, 0)).IgnorePortUnreachable();
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
            Log.Info($"group member {Log.Name(client.Name)} ({host}): {channels}, /info keys: {string.Join(',', client.Info.Keys)}");
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

        StartRealtimeSender(target, options, fifo);
    }

    internal void StartPreparedStream(StreamOptions options, AudioFifo fifo)
    {
        if (StreamTarget == null || _sender != null) throw new InvalidOperationException("No prepared stream");
        StartRealtimeSender(StreamTarget, options, fifo);
    }

    private void StartRealtimeSender(RtpTarget target, StreamOptions options, AudioFifo fifo)
    {
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

    /// <summary>/info, pairing (<see cref="Pair"/>), encrypted session SETUP, event channel and stream SETUP.</summary>
    private async Task<RtpTarget> NegotiateAsync(StreamOptions options, CancellationToken ct)
    {
        VolumeCapPercent = options.VolumeCapPercent;
        if (options.PairingOverRtsp) _rtsp.PairingOverRtsp = true;
        if (options.RequestLog != null)
            _rtsp.RequestTrace = (method, uri, status) => options.RequestLog($"AirPlay 2 {method} {uri}: {status}");
        var info = _rtsp.Rtsp("GET", "/info");
        if (info.IsSuccess && info.Body.Length > 0)
            Info = BPlist.ReadDict(info.Body);
        Log.Info($"connected to {Log.Name(Info.GetValueOrDefault("name"))} ({Info.GetValueOrDefault("model")}, " +
                 $"AirTunes/{Info.GetValueOrDefault("sourceVersion")}, OS {Info.GetValueOrDefault("osBuildVersion")})");

        var shared = Pair(options);
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

        if (eventPort > 0 || !options.AllowMissingEventChannel)
        {
            _events = await EventChannel.ConnectAsync(_rtsp.RemoteIp, eventPort, shared, ct);
            _events.Closed += SignalLost;
        }

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

    /// <summary>
    /// Starts the /feedback heartbeat once; later calls do nothing. PrepareAsync starts it, and the third-party paths that
    /// start a prepared stream (StartPreparedStream, StartCoordinatedNtp) call it again: a second timer would double the
    /// requests and the failure count, and outlive Dispose, which only disposes the timer in the field.
    /// </summary>
    private void StartFeedback() =>
        _feedbackTimer ??= new System.Threading.Timer(_ => Feedback(), null, TimeSpan.Zero, FeedbackInterval);

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
    /// with zero timing requests means its packets never reached us (almost always the firewall; on a Public
    /// network, always: our rule only applies on Private ones).
    /// </summary>
    private HttpMessage SetupStream(Dictionary<string, object?> body)
    {
        try
        {
            return _rtsp.RtspPlist("SETUP", body);
        }
        catch (IOException) when (_timing.RequestsAnswered == 0)
        {
            throw new FirewallBlockedException(NetworkProfile.ForLocalAddress(_rtsp.LocalIp));
        }
    }

    /// <summary>
    /// The secret the session keys come from. A receiver with a stored pairing: pair-verify. One that needs a PIN pairing
    /// (<see cref="PairingFlags"/>) and has none: <see cref="PairingRequiredException"/> before anything is sent to it, so an
    /// automatic connect never shows a PIN on the TV. Everything else (HomePods): the transient pairing, exactly as before.
    /// </summary>
    private byte[] Pair(StreamOptions options)
    {
        string? deviceId = options.DeviceId ?? Info.GetValueOrDefault("deviceID") as string;
        var stored = deviceId != null ? options.Pairings?.Find(deviceId) : null;
        if (stored != null)
        {
            try
            {
                var secret = HapHttp.Verify(_rtsp, stored);
                Log.Info("pairing: pair-verify with the stored pairing ok");
                return secret;
            }
            catch (HapPairingException ex) when (ex.Error == HapError.Authentication)
            {
                Log.Warn($"pairing: pair-verify refused: {ex.Message}");
                throw new PairingRequiredException(Name, deviceId, PairingReason.PairingRejected);
            }
            catch (HapPairingException ex)
            {
                throw new AirPlayException($"pair-verify failed: {ex.Message}");
            }
        }
        if (PairingFlags.NeedsPinPairing(Info))
        {
            string flags = StatusFlags.FromInfo(Info) is { } f ? StatusFlags.Hex(f) : "(from TXT)";
            Log.Info($"pairing: statusFlags {flags} ask for a one-time PIN pairing, and none is stored");
            throw new PairingRequiredException(Name, deviceId, PairingReason.NotPaired);
        }
        return PairTransient(deviceId);
    }

    /// <summary>HAP transient pairing (pair-setup M1..M4 with PIN 3939); returns the SRP session key.</summary>
    private byte[] PairTransient(string? deviceId)
    {
        var headers = new KeyValuePair<string, string>[]
        {
            new("Connection", "keep-alive"),
            new("X-Apple-HKP", "4"),
        };
        const string octet = "application/octet-stream";

        _rtsp.Http("POST", "/pair-pin-start", PairUserAgent, octet, null, headers);

        var m2 = _rtsp.Http("POST", "/pair-setup", PairUserAgent, octet,
            Tlv8.Write((Tlv8.Method, [0]), (Tlv8.State, [1]), (Tlv8.Flags, [Tlv8.FlagTransient])), headers);
        if (m2.StatusCode == 470) // Connection Authorization Required: a receiver whose flags didn't say it needs a pairing
        {
            Log.Info("pairing: transient pair-setup refused with 470");
            throw new PairingRequiredException(Name, deviceId, PairingReason.NotPaired);
        }
        Expect(m2, "pair-setup M1");
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
        resp.IsSuccess ? resp : throw new AirPlayException($"{what} failed: {resp.StartLine}") { ResponseStatusCode = resp.StatusCode, RequestName = what };

    /// <summary>
    /// One /feedback heartbeat (the timer's tick). One at a time: a periodic timer runs its callbacks side by side, and a
    /// request can wait the whole 5 s receive timeout, so a speaker that stopped answering would queue a new request on
    /// the RTSP lock every 2 s, with Dispose's TEARDOWN behind all of them. A tick while one is under way is skipped and
    /// counts as no failure: the one under way decides.
    /// </summary>
    internal void Feedback()
    {
        if (Interlocked.Exchange(ref _feedbackBusy, 1) != 0) return;
        try
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
            if (_feedbackFailures >= 3)
            {
                _lostByFeedback = true;
                SignalLost(L.T("音箱没有响应（网络中断？）"));
            }
        }
        finally
        {
            Volatile.Write(ref _feedbackBusy, 0);
        }
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
        _buffered?.Dispose();
        PtpRealtime?.Dispose();
        _surround?.Dispose();
        // A session lost to three failed heartbeats in a row (as a rule, no answer within the receive timeout) would not
        // get a TEARDOWN answered either: it would only hold up the reconnect for another receive timeout. One failed
        // heartbeat and then 断开 still ends the session cleanly.
        if (!_lostByFeedback)
        {
            try
            {
                if (_rtsp.PairingOverRtsp) _rtsp.RtspPlist("TEARDOWN", new());
                else _rtsp.Rtsp("TEARDOWN", extra: [new("Session", "0")]);
            }
            catch { }
        }
        _events?.Dispose();
        _control.Dispose();
        _timing.Dispose();
        _ptp?.Dispose();
        _follower?.Dispose();
        _rtsp.Dispose();
    }
}

/// <param name="network">
/// The network the speaker is reached through: when it is Public and the firewall doesn't let us in there (it reads
/// the rules), the message says so, which is what the main window and the tray show.
/// </param>
public sealed class FirewallBlockedException(NetworkProfile? network = null) : AirPlayException(Describe(network, AppEnvironment.IsPackaged))
{
    /// <param name="packaged">The Store version: its package brings the Windows Firewall rules, so on a Private network the
    /// usual cause is another firewall or security program, and it has no exe path worth naming.</param>
    internal static string Describe(NetworkProfile? network, bool packaged) =>
        network is { Category: NetworkCategory.Public } && !Firewall.Inspect().PublicAllowed
            ? L.F("网络「{0}」是公用网络，Windows 防火墙拦截了音箱连回本程序的连接", network.DisplayName)
            : packaged
                ? L.T("音箱发来的对时请求没有到达本程序，可能是防火墙或安全软件拦截了 HomePodCast 的入站连接")
                : L.T("音箱发来的对时请求没有到达本程序，通常是 Windows 防火墙拦截了 HomePodCast.exe 的入站连接");
}
