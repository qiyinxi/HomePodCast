using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>
/// An AirPlay 2 receiver on loopback for the pairing tests, the accessory side of everything AirPlayClient and
/// PinPairingSession do: GET /info; HAP pair-setup, either transient with PIN 3939 (a HomePod) or with a PIN shown on the
/// TV (an Apple TV, which answers a transient M1 with 470); pair-verify against the controllers paired with it; and then
/// the encrypted RTSP session (SETUP session and stream, RECORD, FLUSH, SET_PARAMETER, /feedback, TEARDOWN) with an
/// event port and a <see cref="FakeReceiver"/> for the RTP. Every request is recorded in order (with " [encrypted]" when it
/// came over the HAP-encrypted channel); anything that goes wrong on this side is collected in <see cref="Errors"/>.
/// </summary>
internal sealed class FakeAirPlayReceiver : IDisposable
{
    /// <summary>The user's Apple TV (AppleTV14,1) as probed on 2026-10-12: bit 9, OneTimePairingRequired, is set.</summary>
    public const long AppleTvFlags = 0x644;

    /// <summary>An idle HomePod (2nd generation), read on 2026-10-11.</summary>
    public const long HomePodFlags = 0x80404;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _events = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, byte[]> _controllers = new();
    private readonly List<TcpClient> _connections = [];
    private volatile bool _closed;
    private int _verifications;

    public FakeAirPlayReceiver(bool appleTv)
    {
        AppleTv = appleTv;
        StatusFlags = appleTv ? AppleTvFlags : HomePodFlags;
        AccessoryPublicKey = Ed25519.PublicKey(AccessorySeed);
        _listener.Start();
        _events.Start();
        new Thread(() => AcceptLoop(_listener, Serve)) { IsBackground = true, Name = "fake AirPlay receiver" }.Start();
        new Thread(() => AcceptLoop(_events, Hold)) { IsBackground = true, Name = "fake AirPlay receiver events" }.Start();
    }

    /// <summary>An Apple TV: PIN pairing only (a transient M1 gets 470). Otherwise a HomePod: transient pairing only.</summary>
    public bool AppleTv { get; }

    /// <summary>What /info reports as "statusFlags".</summary>
    public long StatusFlags { get; set; }

    public string Pin { get; set; } = "0482";
    public string DeviceId { get; } = "3E:B7:8E:9F:64:72";
    public string Name { get; } = "客厅 Apple TV";
    public int ArrivalToRenderMs { get; set; } = 140;

    /// <summary>The identifier in M6 and pair-verify M2.</summary>
    public byte[] AccessoryId => Encoding.UTF8.GetBytes(DeviceId);
    public byte[] AccessorySeed { get; private set; } = RandomNumberGenerator.GetBytes(32);
    public byte[] AccessoryPublicKey { get; private set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    private int EventPort => ((IPEndPoint)_events.LocalEndpoint).Port;

    /// <summary>Where the RTP goes (the stream SETUP answers with its ports).</summary>
    public FakeReceiver Rtp { get; } = new();

    /// <summary>The stream key ("shk") the sender put in the stream SETUP.</summary>
    public byte[]? StreamKey { get; private set; }

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The headers of the pairing requests: (request, X-Apple-HKP, User-Agent, Content-Type).</summary>
    public ConcurrentQueue<(string What, string Hkp, string UserAgent, string ContentType)> PairingHeaders { get; } = new();

    public ConcurrentQueue<string> Errors { get; } = new();

    public int PinStarts => Requests.Count(r => r.StartsWith("POST /pair-pin-start", StringComparison.Ordinal));
    public int Verifications => Volatile.Read(ref _verifications);

    public bool IsPaired(string controllerId) => _controllers.ContainsKey(controllerId);

    /// <summary>The TV drops every pairing (this PC removed in its settings) but keeps its identity.</summary>
    public void ForgetControllers() => _controllers.Clear();

    /// <summary>A factory reset: a new identity and no pairings.</summary>
    public void Reset()
    {
        _controllers.Clear();
        AccessorySeed = RandomNumberGenerator.GetBytes(32);
        AccessoryPublicKey = Ed25519.PublicKey(AccessorySeed);
    }

    // ---------------------------------------------------------------- connections

    private void AcceptLoop(TcpListener listener, Action<TcpClient> serve)
    {
        while (!_closed)
        {
            TcpClient tcp;
            try
            {
                tcp = listener.AcceptTcpClient();
            }
            catch (Exception) // SocketException or ObjectDisposedException once disposed
            {
                return;
            }
            lock (_connections) _connections.Add(tcp);
            new Thread(() => serve(tcp)) { IsBackground = true, Name = "fake AirPlay receiver connection" }.Start();
        }
    }

    /// <summary>The event channel: accepted and kept open until the sender closes it.</summary>
    private void Hold(TcpClient tcp)
    {
        try
        {
            var buf = new byte[4096];
            using var stream = tcp.GetStream();
            while (stream.Read(buf, 0, buf.Length) > 0) { }
        }
        catch (Exception) { }
        finally
        {
            tcp.Dispose();
        }
    }

    private sealed class Connection
    {
        public SrpServer? Srp;
        public bool Transient;
        public byte[]? SetupKey;
        public byte[]? VerifyPublic;
        public byte[]? ClientVerifyPublic;
        public byte[]? VerifyShared;
        public HapSession? Session;
    }

    private sealed record Reply(string Status, byte[] Body, string ContentType = "application/octet-stream", byte[]? EnableKeys = null);

    private static readonly Reply Ok = new("200 OK", []);

    private void Serve(TcpClient tcp)
    {
        var c = new Connection();
        try
        {
            using var stream = tcp.GetStream();
            var rx = new List<byte>();
            var buf = new byte[16384];
            while (true)
            {
                HttpMessage? req;
                while ((req = HttpMessage.TryParse(rx)) == null)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n == 0) return;
                    rx.AddRange(c.Session != null ? c.Session.Decrypt(buf.AsSpan(0, n)) : buf.AsSpan(0, n).ToArray());
                }
                Reply reply;
                try
                {
                    reply = Handle(req, c);
                }
                catch (Exception ex)
                {
                    Errors.Enqueue($"{req.StartLine}: {ex}");
                    reply = new Reply("500 Internal Server Error", []);
                }
                var headers = new List<KeyValuePair<string, string>> { new("Content-Length", reply.Body.Length.ToString()) };
                if (req.Headers.TryGetValue("CSeq", out var cseq)) headers.Add(new("CSeq", cseq));
                if (reply.Body.Length > 0) headers.Add(new("Content-Type", reply.ContentType));
                var raw = HttpMessage.Format($"{req.StartLine.Split(' ')[^1]} {reply.Status}", headers, reply.Body);
                stream.Write(c.Session?.Encrypt(raw) ?? raw);
                if (reply.EnableKeys is { } shared)
                {
                    // The sender writes with the Write key and reads with the Read key; the receiver the other way round.
                    c.Session = new HapSession(HapKeys.Derive(shared, "Control-Salt", "Control-Read-Encryption-Key"),
                        HapKeys.Derive(shared, "Control-Salt", "Control-Write-Encryption-Key"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // the sender closed the connection
        }
        catch (Exception ex)
        {
            Errors.Enqueue(ex.ToString());
        }
        finally
        {
            c.Session?.Dispose();
            tcp.Dispose();
        }
    }

    private void Record(string what, bool encrypted) => Requests.Enqueue(encrypted ? what + " [encrypted]" : what);

    private void RecordPairing(string what, HttpMessage req, bool encrypted)
    {
        Record(what, encrypted);
        PairingHeaders.Enqueue((what, req.Header("X-Apple-HKP"), req.Header("User-Agent"), req.Header("Content-Type")));
    }

    private Reply Handle(HttpMessage req, Connection c)
    {
        bool encrypted = c.Session != null;
        var parts = req.StartLine.Split(' ');
        string method = parts[0], path = parts.Length > 1 ? parts[1] : "";
        switch (method, path)
        {
            case ("GET", "/info"):
                Record("GET /info", encrypted);
                return new Reply("200 OK", BPlist.Write(new Dictionary<string, object?>
                {
                    ["name"] = Name,
                    ["model"] = AppleTv ? "AppleTV14,1" : "AudioAccessory6,1",
                    ["deviceID"] = DeviceId,
                    ["statusFlags"] = StatusFlags,
                    ["sourceVersion"] = AppleTv ? "980.77.2" : "900.7.1",
                }), "application/x-apple-binary-plist");
            case ("POST", "/pair-pin-start"):
                RecordPairing("POST /pair-pin-start", req, encrypted);
                return Ok;
            case ("POST", "/pair-setup"):
                return PairSetup(req, c, encrypted);
            case ("POST", "/pair-verify"):
                return PairVerify(req, c, encrypted);
        }
        if (!encrypted)
        {
            Record($"{method} {path} (plaintext)", false);
            return new Reply("403 Forbidden", []);
        }
        switch (method)
        {
            case "SETUP":
                return Setup(BPlist.ReadDict(req.Body));
            case "RECORD" or "FLUSH" or "SET_PARAMETER" or "TEARDOWN":
                Record(method, true);
                return Ok;
            case "POST" when path == "/feedback":
                Record("POST /feedback", true);
                return Ok;
        }
        Record($"{method} {path} (unexpected)", true);
        return new Reply("501 Not Implemented", []);
    }

    private static Reply Tlv(params (byte Type, byte[] Value)[] items) => new("200 OK", Tlv8.Write(items));

    private static Reply TlvError(int state, HapError error) => Tlv((Tlv8.State, [(byte)state]), (Tlv8.Error, [(byte)error]));

    private Reply PairSetup(HttpMessage req, Connection c, bool encrypted)
    {
        var tlv = Tlv8.Read(req.Body);
        int state = tlv.TryGetValue(Tlv8.State, out var s) ? s[0] : 0;
        switch (state)
        {
            case 1:
            {
                bool transient = tlv.TryGetValue(Tlv8.Flags, out var flags) && (flags[0] & Tlv8.FlagTransient) != 0;
                RecordPairing(transient ? "POST /pair-setup M1 transient" : "POST /pair-setup M1", req, encrypted);
                if (transient && AppleTv) return new Reply("470 Connection Authorization Required", []);
                if (!transient && !AppleTv) return TlvError(2, HapError.Unavailable);
                c.Transient = transient;
                c.Srp = new SrpServer("Pair-Setup", transient ? "3939" : Pin);
                return Tlv((Tlv8.State, [2]), (Tlv8.Salt, c.Srp.Salt), (Tlv8.PublicKey, c.Srp.PublicKey));
            }
            case 3:
            {
                RecordPairing("POST /pair-setup M3", req, encrypted);
                var srp = c.Srp;
                c.Srp = null; // one PIN per M1: a wrong one ends this pairing attempt
                if (srp == null) return TlvError(4, HapError.Unknown);
                if (srp.Verify(tlv[Tlv8.PublicKey], tlv[Tlv8.Proof]) is not { } ok) return TlvError(4, HapError.Authentication);
                c.SetupKey = ok.Key;
                var m4 = Tlv((Tlv8.State, [4]), (Tlv8.Proof, ok.Proof));
                return c.Transient ? m4 with { EnableKeys = ok.Key } : m4;
            }
            case 5:
            {
                RecordPairing("POST /pair-setup M5", req, encrypted);
                if (c.SetupKey is not { } key || c.Transient) return TlvError(6, HapError.Unknown);
                var encryptKey = HapKeys.Derive(key, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
                var inner = Tlv8.Read(HapMessages.Open(encryptKey, "PS-Msg05", tlv[Tlv8.EncryptedData], "fake M5"));
                byte[] id = inner[Tlv8.Identifier], ltpk = inner[Tlv8.PublicKey], signature = inner[Tlv8.Signature];
                var x = HapKeys.Derive(key, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
                if (!Ed25519.Verify(ltpk, HapMessages.Concat(x, id, ltpk), signature)) return TlvError(6, HapError.Authentication);
                _controllers[Encoding.UTF8.GetString(id)] = ltpk;

                var ax = HapKeys.Derive(key, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
                var ours = Ed25519.Sign(AccessorySeed, HapMessages.Concat(ax, AccessoryId, AccessoryPublicKey));
                var m6 = Tlv8.Write((Tlv8.Identifier, AccessoryId), (Tlv8.PublicKey, AccessoryPublicKey), (Tlv8.Signature, ours));
                return Tlv((Tlv8.State, [6]), (Tlv8.EncryptedData, HapMessages.Seal(encryptKey, "PS-Msg06", m6)));
            }
        }
        Errors.Enqueue($"pair-setup state {state}");
        return TlvError(state + 1, HapError.Unknown);
    }

    private Reply PairVerify(HttpMessage req, Connection c, bool encrypted)
    {
        var tlv = Tlv8.Read(req.Body);
        int state = tlv.TryGetValue(Tlv8.State, out var s) ? s[0] : 0;
        if (state == 1)
        {
            RecordPairing("POST /pair-verify M1", req, encrypted);
            var ephemeral = RandomNumberGenerator.GetBytes(32);
            c.ClientVerifyPublic = tlv[Tlv8.PublicKey];
            c.VerifyPublic = X25519.PublicKey(ephemeral);
            c.VerifyShared = X25519.SharedSecret(ephemeral, c.ClientVerifyPublic);
            var key = HapKeys.Derive(c.VerifyShared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
            var signature = Ed25519.Sign(AccessorySeed, HapMessages.Concat(c.VerifyPublic, AccessoryId, c.ClientVerifyPublic));
            var inner = Tlv8.Write((Tlv8.Identifier, AccessoryId), (Tlv8.Signature, signature));
            return Tlv((Tlv8.State, [2]), (Tlv8.PublicKey, c.VerifyPublic), (Tlv8.EncryptedData, HapMessages.Seal(key, "PV-Msg02", inner)));
        }
        if (state == 3)
        {
            RecordPairing("POST /pair-verify M3", req, encrypted);
            if (c.VerifyShared is not { } shared) return TlvError(4, HapError.Unknown);
            var key = HapKeys.Derive(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
            var inner = Tlv8.Read(HapMessages.Open(key, "PV-Msg03", tlv[Tlv8.EncryptedData], "fake verify M3"));
            byte[] id = inner[Tlv8.Identifier], signature = inner[Tlv8.Signature];
            if (!_controllers.TryGetValue(Encoding.UTF8.GetString(id), out var ltpk) ||
                !Ed25519.Verify(ltpk, HapMessages.Concat(c.ClientVerifyPublic!, id, c.VerifyPublic!), signature))
                return TlvError(4, HapError.Authentication);
            Interlocked.Increment(ref _verifications);
            return new Reply("200 OK", Tlv8.Write((Tlv8.State, [4])), EnableKeys: shared);
        }
        Errors.Enqueue($"pair-verify state {state}");
        return TlvError(state + 1, HapError.Unknown);
    }

    private Reply Setup(Dictionary<string, object?> body)
    {
        const string plist = "application/x-apple-binary-plist";
        if (body.GetValueOrDefault("streams") is List<object?> { Count: > 0 } streams && streams[0] is Dictionary<string, object?> stream)
        {
            Record("SETUP stream", true);
            StreamKey = stream.GetValueOrDefault("shk") as byte[];
            return new Reply("200 OK", BPlist.Write(new Dictionary<string, object?>
            {
                ["streams"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = 96L,
                        ["dataPort"] = (long)Rtp.DataPort,
                        ["controlPort"] = (long)Rtp.ControlPort,
                        ["arrivalToRenderLatencyMs"] = (long)ArrivalToRenderMs,
                    },
                },
            }), plist);
        }
        Record("SETUP session", true);
        return new Reply("200 OK", BPlist.Write(new Dictionary<string, object?>
        {
            ["eventPort"] = (long)EventPort,
            ["timingPort"] = 0L,
        }), plist);
    }

    public void Dispose()
    {
        _closed = true;
        _listener.Stop();
        _events.Stop();
        lock (_connections)
            foreach (var tcp in _connections) tcp.Dispose();
        Rtp.Dispose();
    }
}

/// <summary>
/// The accessory side of SRP-6a as HAP uses it (3072-bit group, SHA-512, the encodings of <see cref="SrpClient"/>):
/// v = g^x, B = k·v + g^b; S = (A·v^u)^b, K = H(S); the client's proof M1 checked, the server's H(A | M1 | K) returned.
/// </summary>
internal sealed class SrpServer
{
    private readonly string _user;
    private readonly BigInteger _v;
    private readonly BigInteger _b;
    private readonly BigInteger _publicB;

    public byte[] Salt { get; } = RandomNumberGenerator.GetBytes(16);
    public byte[] PublicKey { get; }

    public SrpServer(string user, string password)
    {
        var n = SrpClient.N;
        var g = SrpClient.G;
        _user = user;
        var x = SrpClient.HashInt(Salt, SHA512.HashData(Encoding.UTF8.GetBytes($"{user}:{password}")));
        _v = BigInteger.ModPow(g, x, n);
        _b = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true);
        var k = SrpClient.HashInt(SrpClient.Bytes(n), SrpClient.Pad(g));
        _publicB = (k * _v + BigInteger.ModPow(g, _b, n)) % n;
        PublicKey = SrpClient.Bytes(_publicB);
    }

    /// <summary>The session key K and the server's proof; null when the client's proof is wrong (another password).</summary>
    public (byte[] Key, byte[] Proof)? Verify(byte[] clientPublic, byte[] clientProof)
    {
        var n = SrpClient.N;
        var g = SrpClient.G;
        var a = new BigInteger(clientPublic, isUnsigned: true, isBigEndian: true);
        if (a % n == 0) return null;
        var u = SrpClient.HashInt(SrpClient.Pad(a), SrpClient.Pad(_publicB));
        var s = BigInteger.ModPow(a * BigInteger.ModPow(_v, u, n) % n, _b, n);
        var key = SHA512.HashData(SrpClient.Bytes(s));
        var hNxorG = SrpClient.HashInt(SrpClient.Bytes(n)) ^ SrpClient.HashInt(SrpClient.Bytes(g));
        var hUser = SrpClient.HashInt(Encoding.UTF8.GetBytes(_user));
        var expected = SHA512.HashData(SrpClient.Concat(SrpClient.Bytes(hNxorG), SrpClient.Bytes(hUser), Salt,
            SrpClient.Bytes(a), SrpClient.Bytes(_publicB), key));
        if (!expected.AsSpan().SequenceEqual(clientProof)) return null;
        return (key, SHA512.HashData(SrpClient.Concat(SrpClient.Bytes(a), clientProof, key)));
    }
}
