using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Protocol;

/// <summary>
/// A HAP pairing with one receiver (an Apple TV): who we are to it — our controller identifier and Ed25519 private key —
/// and who it is — its identifier and Ed25519 long-term public key, from pair-setup M6. Secret: kept only in the
/// DPAPI-encrypted store (Net.PairingStore), never logged or shown.
/// </summary>
public sealed record HapPairing(string ControllerId, byte[] ControllerSeed, byte[] AccessoryId, byte[] AccessoryPublicKey)
{
    /// <summary>Never the keys.</summary>
    public override string ToString() => $"HapPairing(controller {ControllerId})";
}

/// <summary>The HAP TLV error codes a receiver answers a pairing step with.</summary>
public enum HapError : byte
{
    Unknown = 1,

    /// <summary>Wrong PIN (pair-setup M4), or a controller the receiver does not know (pair-verify M4).</summary>
    Authentication = 2,

    /// <summary>Too many attempts for now: retry after the seconds in the BackOff TLV.</summary>
    BackOff = 3,

    /// <summary>No room for another pairing.</summary>
    MaxPeers = 4,

    /// <summary>Too many failed attempts: pairing is locked.</summary>
    MaxTries = 5,

    /// <summary>The receiver does not accept a pairing now.</summary>
    Unavailable = 6,

    /// <summary>Another pairing is in progress.</summary>
    Busy = 7,
}

/// <summary>
/// A pairing step that failed: the receiver answered with a TLV error (<see cref="Error"/>), or one of our checks failed
/// (a signature or identity that does not match counts as <see cref="HapError.Authentication"/>; a malformed answer has
/// no error code). The message is for the log (English, no secrets).
/// </summary>
public sealed class HapPairingException(string message, HapError? error = null, int? retryAfterSeconds = null) : Exception(message)
{
    public HapError? Error { get; } = error;

    /// <summary>With <see cref="HapError.BackOff"/>: how long the receiver wants us to wait, if it said.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// The TLV8 messages of HAP pairing as AirPlay 2 receivers use them (pyatv's auth/hap_srp.py and
/// protocols/airplay/auth/hap.py, OwnTone's pair_ap/pair_homekit.c, the HomeKit Accessory Protocol specification):
/// ChaCha20-Poly1305 with the 8-byte ASCII nonces "PS-Msg05", "PV-Msg02"... after 4 zero bytes and no associated data,
/// keys from HKDF-SHA512 (<see cref="HapKeys.Derive"/>).
/// </summary>
internal static class HapMessages
{
    public const int TagSize = 16;

    /// <summary>
    /// The TLVs of a receiver's answer, after checking it carries no error and is the expected state (step 2, 4 or 6).
    /// </summary>
    public static Dictionary<byte, byte[]> Read(byte[] body, int state, string step)
    {
        Dictionary<byte, byte[]> tlv;
        try
        {
            tlv = Tlv8.Read(body);
        }
        catch (InvalidDataException)
        {
            throw new HapPairingException($"{step}: malformed TLV8 answer ({body.Length} bytes)");
        }
        if (tlv.TryGetValue(Tlv8.Error, out var error))
        {
            var code = error.Length > 0 ? (HapError)error[0] : HapError.Unknown;
            int? retry = tlv.TryGetValue(Tlv8.BackOff, out var backOff) && backOff.Length is > 0 and <= 4 ? LittleEndian(backOff) : null;
            throw new HapPairingException($"{step}: the receiver answered TLV error {(int)code} ({code})" +
                                          (retry is { } s ? $", retry after {s} s" : ""), code, retry);
        }
        if (tlv.TryGetValue(Tlv8.State, out var s2) && (s2.Length != 1 || s2[0] != state))
            throw new HapPairingException($"{step}: unexpected state {(s2.Length > 0 ? s2[0] : -1)} (expected {state})");
        return tlv;
    }

    public static byte[] Require(Dictionary<byte, byte[]> tlv, byte type, string step, int? length = null)
    {
        if (!tlv.TryGetValue(type, out var value))
            throw new HapPairingException($"{step}: TLV 0x{type:X2} missing");
        if (length is { } n && value.Length != n)
            throw new HapPairingException($"{step}: TLV 0x{type:X2} is {value.Length} bytes, expected {n}");
        return value;
    }

    /// <summary>The 12-byte nonce of a pairing message: 4 zero bytes, then the 8-character label.</summary>
    public static byte[] Nonce(string label)
    {
        var nonce = new byte[12];
        Encoding.ASCII.GetBytes(label, 0, 8, nonce, 4);
        return nonce;
    }

    /// <summary>ChaCha20-Poly1305 with a label nonce: ciphertext followed by the 16-byte tag.</summary>
    public static byte[] Seal(byte[] key, string nonceLabel, byte[] plain)
    {
        using var aead = new ChaCha20Poly1305(key);
        var output = new byte[plain.Length + TagSize];
        aead.Encrypt(Nonce(nonceLabel), plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length));
        return output;
    }

    public static byte[] Open(byte[] key, string nonceLabel, byte[] sealedData, string step)
    {
        if (sealedData.Length < TagSize) throw new HapPairingException($"{step}: encrypted data too short");
        using var aead = new ChaCha20Poly1305(key);
        int n = sealedData.Length - TagSize;
        var plain = new byte[n];
        try
        {
            aead.Decrypt(Nonce(nonceLabel), sealedData.AsSpan(0, n), sealedData.AsSpan(n), plain);
        }
        catch (CryptographicException)
        {
            throw new HapPairingException($"{step}: the encrypted data does not decrypt");
        }
        return plain;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }
        return result;
    }

    private static int LittleEndian(byte[] value)
    {
        int v = 0;
        for (int i = value.Length - 1; i >= 0; i--) v = v << 8 | value[i];
        return v;
    }
}

/// <summary>
/// Pair-setup with a PIN, the controller's side (M1, M3, M5 out; M2, M4, M6 in). The SRP part is <see cref="SrpClient"/>
/// with user "Pair-Setup" and the PIN as the password; the transport (POST /pair-setup) is Net.PinPairingSession's.
/// </summary>
public static class HapPairSetup
{
    /// <summary>M1: method 0 (pair setup), state 1, and no flags (a transient pairing sets 0x10).</summary>
    public static byte[] M1() => Tlv8.Write((Tlv8.Method, [0]), (Tlv8.State, [1]));

    /// <summary>M2: the receiver's SRP salt and public key B.</summary>
    public static (byte[] Salt, byte[] PublicKey) ReadM2(byte[] body)
    {
        var tlv = HapMessages.Read(body, 2, "pair-setup M2");
        return (HapMessages.Require(tlv, Tlv8.Salt, "pair-setup M2"), HapMessages.Require(tlv, Tlv8.PublicKey, "pair-setup M2"));
    }

    /// <summary>M3: our SRP public key A and proof M1.</summary>
    public static byte[] M3(byte[] clientPublic, byte[] proof) =>
        Tlv8.Write((Tlv8.State, [3]), (Tlv8.PublicKey, clientPublic), (Tlv8.Proof, proof));

    /// <summary>M4: the receiver's SRP proof. A wrong PIN is TLV error 2 (<see cref="HapError.Authentication"/>).</summary>
    public static byte[] ReadM4(byte[] body) =>
        HapMessages.Require(HapMessages.Read(body, 4, "pair-setup M4"), Tlv8.Proof, "pair-setup M4");

    /// <summary>
    /// M5: our identity, encrypted with HKDF(K, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info") and nonce "PS-Msg05":
    /// TLV identifier, Ed25519 public key (LTPK), and the signature over
    /// HKDF(K, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info") ‖ identifier ‖ LTPK.
    /// </summary>
    /// <param name="sessionKey">The SRP session key K.</param>
    public static byte[] M5(byte[] sessionKey, string controllerId, byte[] controllerSeed)
    {
        var id = Encoding.UTF8.GetBytes(controllerId);
        var ltpk = Ed25519.PublicKey(controllerSeed);
        var x = HapKeys.Derive(sessionKey, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
        var signature = Ed25519.Sign(controllerSeed, HapMessages.Concat(x, id, ltpk));
        var inner = Tlv8.Write((Tlv8.Identifier, id), (Tlv8.PublicKey, ltpk), (Tlv8.Signature, signature));
        return Tlv8.Write((Tlv8.State, [5]), (Tlv8.EncryptedData, HapMessages.Seal(EncryptKey(sessionKey), "PS-Msg05", inner)));
    }

    /// <summary>
    /// M6: the receiver's identifier and Ed25519 public key, decrypted (nonce "PS-Msg06") and checked: its signature over
    /// HKDF(K, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info") ‖ identifier ‖ LTPK must verify.
    /// </summary>
    public static (byte[] AccessoryId, byte[] AccessoryPublicKey) ReadM6(byte[] body, byte[] sessionKey)
    {
        const string step = "pair-setup M6";
        var tlv = HapMessages.Read(body, 6, step);
        var inner = Tlv8.Read(HapMessages.Open(EncryptKey(sessionKey), "PS-Msg06", HapMessages.Require(tlv, Tlv8.EncryptedData, step), step));
        var id = HapMessages.Require(inner, Tlv8.Identifier, step);
        var ltpk = HapMessages.Require(inner, Tlv8.PublicKey, step, Ed25519.PublicKeySize);
        var signature = HapMessages.Require(inner, Tlv8.Signature, step, Ed25519.SignatureSize);
        var x = HapKeys.Derive(sessionKey, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
        // Not HapError.Authentication: that is a wrong PIN, which this is not (M4 already proved the PIN).
        if (id.Length == 0 || !Ed25519.Verify(ltpk, HapMessages.Concat(x, id, ltpk), signature))
            throw new HapPairingException($"{step}: the receiver's signature does not verify");
        return (id, ltpk);
    }

    internal static byte[] EncryptKey(byte[] sessionKey) =>
        HapKeys.Derive(sessionKey, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
}

/// <summary>
/// Pair-verify, the controller's side, on every connection to a paired receiver: an X25519 exchange signed by both
/// long-term Ed25519 keys (M1, M3 out; M2, M4 in). Its shared secret then takes the place of the transient pairing's SRP
/// key: the RTSP keys are HKDF(secret, "Control-Salt", "Control-Write/Read-Encryption-Key") and the event channel's
/// "Events-Salt" ones, exactly as before.
/// </summary>
public sealed class HapPairVerify
{
    private readonly HapPairing _pairing;
    private readonly byte[] _private;

    /// <summary>Our ephemeral X25519 public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The X25519 shared secret, once <see cref="ReadM2"/> has accepted the receiver.</summary>
    public byte[]? SharedSecret { get; private set; }

    public HapPairVerify(HapPairing pairing) : this(pairing, RandomNumberGenerator.GetBytes(X25519.KeySize))
    {
    }

    /// <summary>A fixed ephemeral key: tests only.</summary>
    internal HapPairVerify(HapPairing pairing, byte[] ephemeralPrivate)
    {
        _pairing = pairing;
        _private = ephemeralPrivate;
        PublicKey = X25519.PublicKey(ephemeralPrivate);
    }

    /// <summary>M1: state 1 and our ephemeral public key.</summary>
    public byte[] M1() => Tlv8.Write((Tlv8.State, [1]), (Tlv8.PublicKey, PublicKey));

    /// <summary>
    /// M2: the receiver's ephemeral key and, encrypted with HKDF(secret, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info")
    /// and nonce "PV-Msg02", its identifier and signature over its key ‖ identifier ‖ our key. Both must be the paired
    /// receiver's (else <see cref="HapError.Authentication"/>: reset, or another device at that address). Returns M3: our
    /// identifier and signature over our key ‖ identifier ‖ its key, encrypted with nonce "PV-Msg03".
    /// </summary>
    public byte[] ReadM2(byte[] body)
    {
        const string step = "pair-verify M2";
        var tlv = HapMessages.Read(body, 2, step);
        var accessoryPublic = HapMessages.Require(tlv, Tlv8.PublicKey, step, X25519.KeySize);
        byte[] shared;
        try
        {
            shared = X25519.SharedSecret(_private, accessoryPublic);
        }
        catch (CryptographicException ex)
        {
            throw new HapPairingException($"{step}: {ex.Message}");
        }
        var key = EncryptKey(shared);
        var inner = Tlv8.Read(HapMessages.Open(key, "PV-Msg02", HapMessages.Require(tlv, Tlv8.EncryptedData, step), step));
        var id = HapMessages.Require(inner, Tlv8.Identifier, step);
        var signature = HapMessages.Require(inner, Tlv8.Signature, step, Ed25519.SignatureSize);
        if (!CryptographicOperations.FixedTimeEquals(id, _pairing.AccessoryId))
            throw new HapPairingException($"{step}: another receiver's identity", HapError.Authentication);
        if (!Ed25519.Verify(_pairing.AccessoryPublicKey, HapMessages.Concat(accessoryPublic, id, PublicKey), signature))
            throw new HapPairingException($"{step}: the receiver's signature does not verify", HapError.Authentication);

        var controllerId = Encoding.UTF8.GetBytes(_pairing.ControllerId);
        var ours = Ed25519.Sign(_pairing.ControllerSeed, HapMessages.Concat(PublicKey, controllerId, accessoryPublic));
        var m3 = Tlv8.Write((Tlv8.Identifier, controllerId), (Tlv8.Signature, ours));
        SharedSecret = shared;
        return Tlv8.Write((Tlv8.State, [3]), (Tlv8.EncryptedData, HapMessages.Seal(key, "PV-Msg03", m3)));
    }

    /// <summary>M4: state 4, or TLV error 2 when the receiver no longer knows us (it was reset, or removed this PC).</summary>
    public static void ReadM4(byte[] body) => HapMessages.Read(body, 4, "pair-verify M4");

    internal static byte[] EncryptKey(byte[] sharedSecret) =>
        HapKeys.Derive(sharedSecret, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
}
