using System.Net;
using System.Security.Cryptography;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// Which receivers need a one-time PIN pairing before they stream: their status flags (/info "statusFlags", the TXT
/// record's "flags") say so with bit 9 (OneTimePairingRequired) or bit 3 (PINRequired), as pyatv's
/// get_pairing_requirement and OwnTone's /info handler read them. The user's Apple TV advertises 0x644 (bit 9 set) and
/// answers a transient pair-setup with 470; HomePods advertise 0x80404 / 0x1A0404 (neither bit) and pair transiently.
/// </summary>
public static class PairingFlags
{
    public const long PinRequired = 1L << 3;
    public const long OneTimePairingRequired = 1L << 9;

    public static bool NeedsPinPairing(long flags) => (flags & (PinRequired | OneTimePairingRequired)) != 0;

    /// <summary>From GET /info (its statusFlags, else the TXT record it carries); false when neither says.</summary>
    public static bool NeedsPinPairing(IReadOnlyDictionary<string, object?> info) =>
        (StatusFlags.FromInfo(info) ?? StatusFlags.FromTxt(TxtRecord.FromInfo(info))) is { } flags && NeedsPinPairing(flags);
}

public enum PairingReason
{
    /// <summary>The receiver needs a one-time PIN pairing and this PC has none with it.</summary>
    NotPaired,

    /// <summary>The stored pairing was refused by pair-verify: the receiver was reset, or this PC was removed from it.</summary>
    PairingRejected,
}

/// <summary>
/// The receiver needs a PIN pairing first (<see cref="PairingReason"/>). Thrown before anything shows on the TV: no
/// /pair-pin-start is ever sent by a connect, only by <see cref="PinPairingSession"/> after the user asked for it.
/// </summary>
public sealed class PairingRequiredException : AirPlayException
{
    public PairingRequiredException(string speaker, string? deviceId, PairingReason reason) : base(Describe(speaker, reason))
    {
        Speaker = speaker;
        DeviceId = deviceId;
        Reason = reason;
        LogMessage = $"{Log.Name(speaker)}: " + (reason == PairingReason.NotPaired
            ? "needs a one-time PIN pairing"
            : "the stored pairing was refused (the receiver was reset or removed this PC)");
    }

    public string Speaker { get; }
    public string? DeviceId { get; }
    public PairingReason Reason { get; }

    /// <summary>The status line (首页, the tray) while the speaker waits for a pairing.</summary>
    internal static string Describe(string speaker, PairingReason reason) => reason == PairingReason.NotPaired
        ? L.F("{0}需要在电视上配对一次，点「连接」开始", speaker)
        : L.F("{0}上的配对已失效，点「连接」重新配对", speaker);
}

/// <summary>HTTP POSTs of the HAP pairing steps on an AirPlay control connection, with pyatv's headers for HAP.</summary>
internal static class HapHttp
{
    public const string UserAgent = "AirPlay/320.20";
    private const string ContentType = "application/octet-stream";

    /// <summary>"X-Apple-HKP: 3" is the normal (PIN) pairing and pair-verify; the transient pairing sends 4.</summary>
    private static readonly KeyValuePair<string, string>[] Headers =
    [
        new("Connection", "keep-alive"),
        new("X-Apple-HKP", "3"),
    ];

    /// <summary>
    /// The answer's body. 470 (Connection Authorization Required) counts as <see cref="HapError.Authentication"/>; any
    /// other failure without a TLV error is an <see cref="AirPlayException"/>. TLV errors are left for the caller's parser.
    /// </summary>
    public static byte[] Post(RtspConnection rtsp, string path, byte[]? body, string step)
    {
        var resp = rtsp.Http("POST", path, UserAgent, ContentType, body, Headers);
        if (resp.StatusCode == 470)
            throw new HapPairingException($"{step}: {resp.StartLine}", HapError.Authentication);
        if (!resp.IsSuccess && !HasTlvError(resp.Body))
            throw new AirPlayException($"{step} failed: {resp.StartLine}");
        return resp.Body;
    }

    private static bool HasTlvError(byte[] body)
    {
        if (body.Length == 0) return false;
        try
        {
            return Tlv8.Read(body).ContainsKey(Tlv8.Error);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Pair-verify with a stored pairing; returns the shared secret the session keys come from.</summary>
    public static byte[] Verify(RtspConnection rtsp, HapPairing pairing)
    {
        var verify = new HapPairVerify(pairing);
        var m2 = Post(rtsp, "/pair-verify", verify.M1(), "pair-verify M1");
        var m4 = Post(rtsp, "/pair-verify", verify.ReadM2(m2), "pair-verify M3");
        HapPairVerify.ReadM4(m4);
        return verify.SharedSecret!;
    }
}

/// <summary>
/// The one-time PIN pairing, started only by the user (the pairing dialog): <see cref="StartAsync"/> asks the receiver to
/// show a PIN (POST /pair-pin-start, which lights up the TV) and sends pair-setup M1; <see cref="Finish"/> runs M3..M6
/// with the PIN the user read off the TV and returns the pairing to store. One PIN per session: after a wrong one, start
/// a new session (the TV shows a new PIN).
/// </summary>
public sealed class PinPairingSession : IDisposable
{
    private readonly RtspConnection _rtsp;
    private readonly byte[] _salt;
    private readonly byte[] _serverPublic;
    private bool _used;

    private PinPairingSession(RtspConnection rtsp, byte[] salt, byte[] serverPublic)
    {
        _rtsp = rtsp;
        _salt = salt;
        _serverPublic = serverPublic;
    }

    public static async Task<PinPairingSession> StartAsync(IPAddress host, int port, CancellationToken ct)
    {
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        try
        {
            HapHttp.Post(rtsp, "/pair-pin-start", null, "pair-pin-start");
            ct.ThrowIfCancellationRequested();
            var (salt, serverPublic) = HapPairSetup.ReadM2(HapHttp.Post(rtsp, "/pair-setup", HapPairSetup.M1(), "pair-setup M1"));
            Log.Info("pairing: the receiver shows a PIN (pair-setup M2 received)");
            return new PinPairingSession(rtsp, salt, serverPublic);
        }
        catch
        {
            rtsp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// M3..M6 with <paramref name="pin"/> (the digits as shown, leading zeros included). A wrong PIN throws
    /// <see cref="HapPairingException"/> with <see cref="HapError.Authentication"/>.
    /// </summary>
    public HapPairing Finish(string pin)
    {
        if (_used) throw new InvalidOperationException("one PIN per pairing session");
        _used = true;
        var srp = new SrpClient("Pair-Setup", pin);
        var proof = srp.ComputeProof(_salt, _serverPublic);
        var serverProof = HapPairSetup.ReadM4(HapHttp.Post(_rtsp, "/pair-setup", HapPairSetup.M3(srp.PublicKey, proof), "pair-setup M3"));
        // The receiver accepted our proof, so the PIN was right; a proof of its own that doesn't match is not a wrong PIN.
        if (!srp.VerifyServerProof(serverProof))
            throw new HapPairingException("pair-setup M4: the receiver's proof does not match");

        // Like pyatv: a random UUID (lower case) as our identifier for this receiver, and a new Ed25519 key.
        var controllerId = Guid.NewGuid().ToString();
        var seed = RandomNumberGenerator.GetBytes(Ed25519.SeedSize);
        var m6 = HapHttp.Post(_rtsp, "/pair-setup", HapPairSetup.M5(srp.SessionKey!, controllerId, seed), "pair-setup M5");
        var (accessoryId, accessoryKey) = HapPairSetup.ReadM6(m6, srp.SessionKey!);
        Log.Info("pairing: done (pair-setup M6 verified)");
        return new HapPairing(controllerId, seed, accessoryId, accessoryKey);
    }

    public void Dispose() => _rtsp.Dispose();
}
