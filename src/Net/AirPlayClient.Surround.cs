using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

// experimental since 1.4
/// <summary>What a 5.1 surround session reads from and reports to; the rest comes from <see cref="StreamOptions"/>.</summary>
/// <param name="Fifo">48 kHz, 6 channels (FL FR FC LFE Ls Rs).</param>
internal sealed record SurroundSetup(AudioFifo Fifo)
{
    /// <summary>Sees every 6-channel block, so 夜间自动断开 never cuts a film (the stereo effect chain is bypassed).</summary>
    public SoundActivity? Activity { get; init; }

    /// <summary>Non-null (translated): the capture can't give 5.1 any more, and the session ends.</summary>
    public Func<string?> SourceProblem { get; init; } = () => null;

    public PtpPorts Ports { get; init; } = PtpPorts.Standard;

    /// <summary>Tests: an encoder without Media Foundation. Null: Windows' AAC encoder, required to give ASC 11 B0.</summary>
    public Func<IAacEncoder>? Encoder { get; init; }

    /// <summary>The CLI: refuse (fail closed) when /info shows another sender's session or no statusFlags.</summary>
    public bool RequireIdle { get; init; }

    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>5.1 surround can't be used with this speaker or on this PC right now; the message (translated) says why.</summary>
public sealed class SurroundUnavailableException(string message) : AirPlayException(message);

/// <summary><see cref="SurroundSetup.RequireIdle"/>: another sender has a session, or the speaker doesn't say (CLI only).</summary>
internal sealed class SpeakerInUseException(string message) : AirPlayException(message);

// experimental since 1.4
// 5.1 surround to one HomePod: AAC-LC 48 kHz 5.1 over the buffered (type 103) path, anchored in the HomePod's own PTP
// timeline. The sequence is the one verified on a HomePod 2 (OS 27.0) on 2026-10-28, including the SETPEERS and
// FLUSHBUFFERED answers it tolerated. ConnectBufferedAsync keeps refusing Apple devices; this is their only buffered path.
public sealed partial class AirPlayClient
{
    private PtpFollower? _follower;
    private SurroundSender? _surround;

    internal SurroundSender? SurroundSender => _surround;
    internal PtpFollower? SurroundClock => _follower;

    partial void DisposeSurroundSender() => _surround?.Dispose();

    partial void DisposeSurroundClock() => _follower?.Dispose();

    /// <summary>
    /// One 5.1 session. <see cref="SurroundUnavailableException"/> (translated) when 5.1 can't be used here: the caller then
    /// streams stereo. A broken RTSP connection, a cancellation or a pairing that is needed propagate as they are, so the
    /// usual retry tries 5.1 again.
    /// </summary>
    internal static async Task<AirPlayClient> ConnectSurroundAsync(IPAddress host, int port, StreamOptions options,
        SurroundSetup setup, CancellationToken ct)
    {
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        var client = new AirPlayClient(rtsp);
        try
        {
            await client.SetupSurroundAsync(options, setup, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task SetupSurroundAsync(StreamOptions options, SurroundSetup setup, CancellationToken ct)
    {
        VolumeCapPercent = options.VolumeCapPercent;
        if (options.RequestLog != null)
            _rtsp.RequestTrace = (method, uri, status) => options.RequestLog($"AirPlay 2 5.1 {method} {uri}: {status}");

        var info = Expect(_rtsp.Rtsp("GET", "/info"), "GET /info");
        if (info.Body.Length > 0) Info = BPlist.ReadDict(info.Body);
        string model = Info.GetValueOrDefault("model") as string ?? "";
        long? flags = StatusFlags.FromInfo(Info);
        Log.Info($"5.1: {Log.Name(Info.GetValueOrDefault("name"))} ({model}, OS {Info.GetValueOrDefault("osBuildVersion")}), " +
                 $"{SupportedFormats.Describe(Info)}, statusFlags {(flags is { } f ? StatusFlags.Hex(f) : "none")}");

        // Before pairing: an Apple TV, a HomePod mini or any speaker without bit 39 never gets a pairing attempt (no PIN).
        if (ReceiverKinds.Classify(model, null, info: Info) != ReceiverKind.HomePod || !SupportedFormats.Has(Info, BufferedAudio.Surround51Bit))
            throw new SurroundUnavailableException(L.T("这台音箱不支持 5.1"))
                { LogMessage = $"no 5.1 here: {model}, {SupportedFormats.Describe(Info)}" };
        if (setup.RequireIdle && (flags is not { } idle || StatusFlags.SessionActive(idle)))
            throw new SpeakerInUseException($"speaker in use (statusFlags {(flags is { } busy ? StatusFlags.Describe(busy) : "none")}): not taking it over");

        var shared = Pair(options);
        _rtsp.EnableEncryption(
            HapKeys.Derive(shared, "Control-Salt", "Control-Write-Encryption-Key"),
            HapKeys.Derive(shared, "Control-Salt", "Control-Read-Encryption-Key"));

        try
        {
            await NegotiateSurroundAsync(options, setup, shared, ct);
        }
        catch (AirPlayException ex) when (ex is not SurroundUnavailableException && ex.ResponseStatusCode != null)
        {
            // The speaker said no to a step of the 5.1 sequence: stereo instead, with the step in the status.
            string detail = AirPlayException.ForLog(ex);
            throw new SurroundUnavailableException(L.F("5.1 连接失败：{0}", detail)) { LogMessage = $"5.1 connection failed: {detail}" };
        }
    }

    private async Task NegotiateSurroundAsync(StreamOptions options, SurroundSetup setup, byte[] shared, CancellationToken ct)
    {
        // Bound before the session SETUP, so that ports in use never take the speaker. Top bit clear: BPlist writes a
        // ulong as int64 bits, and a negative ClockID in timingPeerInfo is one doubt less.
        ulong clockId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fff_ffff_ffff_ffff;
        _follower = PtpFollower.Bind(_rtsp.LocalIp, _rtsp.RemoteIp, clockId, setup.Ports, setup.StaleAfter);
        _follower.Failed += reason => SignalLost(L.F("5.1 环绕声中断：{0}", reason));

        string uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var body = SessionSetupBody(uuid, 0, null);
        body.Remove("timingPort");
        foreach (var (key, value) in PtpSessionBody(uuid, _rtsp.LocalIp, clockId)) body[key] = value;
        body["name"] = "HomePodCast";
        var session = BPlist.ReadDict(Expect(_rtsp.RtspPlist("SETUP", body), "SETUP session").Body);
        int eventPort = Convert.ToInt32(session.GetValueOrDefault("eventPort") ?? 0L);
        _events = await EventChannel.ConnectAsync(_rtsp.RemoteIp, eventPort, shared, ct);
        _events.Closed += SignalLost;

        // 2026-10-28: tolerated as the verified run did; the HomePod's answer to it was not what decided the session.
        var peers = _rtsp.Rtsp("SETPEERS", contentType: "application/x-apple-binary-plist",
            body: BPlist.Write(new object?[] { _rtsp.RemoteIp.ToString(), _rtsp.LocalIp.ToString() }));
        if (!peers.IsSuccess) Log.Warn($"5.1: SETPEERS answered {peers.StartLine}; continuing");

        byte[] streamKey = RandomNumberGenerator.GetBytes(32);
        int latencyFrames = options.LatencyMs * SurroundSender.Rate / 1000;
        var stream = BPlist.ReadDict(Expect(_rtsp.RtspPlist("SETUP", new()
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 103, ["audioFormat"] = 1L << BufferedAudio.Surround51Bit, ["ct"] = 4,
                    ["sr"] = SurroundSender.Rate, ["spf"] = SurroundSender.Frames, ["shk"] = streamKey, ["isMedia"] = true,
                    ["audioMode"] = "default", ["latencyMin"] = latencyFrames,
                    ["latencyMax"] = Math.Max(latencyFrames, SurroundSender.Rate * 4), ["controlPort"] = 0,
                    ["streamConnectionID"] = (long)_rtsp.SessionId, ["supportsDynamicStreamID"] = false,
                },
            },
        }), "SETUP stream").Body);
        var reply = (stream.GetValueOrDefault("streams") as List<object?>)?[0] as Dictionary<string, object?>
            ?? throw new InvalidDataException("SETUP stream: no streams in reply");
        int dataPort = Convert.ToInt32(reply["dataPort"]);
        // Not arrivalToRenderLatencyMs: it belongs to the realtime path and would overwrite its saved floor.
        LatencyFrames = latencyFrames;

        _surround = await SurroundSender.ConnectAsync(_rtsp.RemoteIp, dataPort, streamKey,
            BufferedAudio.FormatSsrc(BufferedAudio.Surround51Bit), setup, _follower, ct);
        _surround.Failed += OnSurroundFailed;

        Record();

        // Checked, unlike SetVolumeDb: a speaker that refused it would play at whatever volume it was left at.
        double db = VolumeLimit.ClampDb(options.VolumePercent is { } pct ? PercentToDb(pct) : InitialVolumeDb ?? -20, VolumeCapPercent);
        Expect(_rtsp.Rtsp("SET_PARAMETER", contentType: "text/parameters",
            body: System.Text.Encoding.UTF8.GetBytes($"volume: {db.ToString("F6", CultureInfo.InvariantCulture)}")), "SET_PARAMETER volume");

        var flush = _rtsp.RtspPlist("FLUSHBUFFERED", new() { ["flushUntilSeq"] = 0, ["flushUntilTS"] = _surround.RtpBase });
        if (!flush.IsSuccess) Log.Warn($"5.1: FLUSHBUFFERED answered {flush.StartLine}; continuing");

        await Task.Delay(1000, ct);
        if (!await _follower.SettleAsync(setup.SettleTimeout, ct))
            throw new SurroundUnavailableException(L.T("没有收到 HomePod 的 PTP 对时，可能被防火墙拦截"))
                { LogMessage = $"no PTP Sync from the HomePod within {setup.SettleTimeout.TotalSeconds:F1} s" };

        // 2026-10-28: anchors without networkTimeFlags got 400; with it, 200. Up to three tries 500 ms apart, as a JBL
        // needed in the research.
        long startAt = 0;
        ulong startHp = 0;
        for (int attempt = 1; ; attempt++)
        {
            startAt = MediaClock.Now;
            startHp = _follower.TimeAt(startAt) ?? throw new SurroundUnavailableException(L.T("没有收到 HomePod 的 PTP 对时，可能被防火墙拦截"));
            ulong timeline = _follower.TimelineId!.Value;
            var anchor = _rtsp.RtspPlist("SETRATEANCHORTIME", BufferedAudio.Anchor(timeline,
                startHp + (ulong)options.LatencyMs * 1_000_000, _surround.RtpBase, networkTimeFlags: 0));
            if (anchor.IsSuccess) break;
            Log.Warn($"5.1: SETRATEANCHORTIME try {attempt} answered {anchor.StartLine}");
            if (attempt == 3)
            {
                string detail = $"SETRATEANCHORTIME: {anchor.StartLine}";
                throw new SurroundUnavailableException(L.F("5.1 连接失败：{0}", detail)) { LogMessage = $"5.1 connection failed: {detail}" };
            }
            await Task.Delay(500, ct);
        }

        _surround.Start(startAt, startHp);
        StartFeedback();
        Log.Info($"5.1 streaming: encoder {(_surround.EncoderSettings is { Length: > 0 } s ? s : "(test)")}, " +
                 $"AudioSpecificConfig {Convert.ToHexString(_surround.AudioSpecificConfig)}, GM 0x{_follower.TimelineId:X16}, " +
                 $"offset {_follower.OffsetNs / 1e9:F6} s, ssrc 0x{BufferedAudio.FormatSsrc(BufferedAudio.Surround51Bit):X8}, " +
                 $"latency {options.LatencyMs} ms, RTP base {_surround.RtpBase}");
    }

    /// <summary>
    /// The HomePod closing the data connection is reported a second later: when another sender took over, its event
    /// channel closes too, and <see cref="EventChannel.ClosedBySpeaker"/> must win (the loss fires once), so that the
    /// takeover is handled as one.
    /// </summary>
    private void OnSurroundFailed(string reason, bool receiverClosed)
    {
        if (receiverClosed)
            _ = Task.Delay(1000).ContinueWith(_ => SignalLost(L.F("5.1 环绕声中断：{0}", L.T("HomePod 关闭了音频连接"))), TaskScheduler.Default);
        else SignalLost(L.F("5.1 环绕声中断：{0}", reason));
    }
}
