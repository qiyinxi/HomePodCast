using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// An AirPlay 2 session that carries no audio, only the remote-control channel (what an iPhone's Control Centre and
/// Remote app open to an Apple TV): pair-verify with the stored pairing, a session SETUP with isRemoteControlOnly, the
/// event channel, RECORD, and a type 130 data stream on its own encrypted TCP connection that carries MediaRemote
/// messages (<see cref="MediaRemote"/>) both ways. The sequence, the keys' names and the frame format are pyatv's
/// (pyatv/protocols/airplay/ap2_session.py and channels.py), which is known to work against tvOS.
/// </summary>
/// <remarks>
/// It takes nothing from the Apple TV: no audio stream is set up, so whatever plays there keeps playing. 2026-11-11: an
/// Apple TV 4K on tvOS 27 accepted the session SETUP with the pairing this app already stores for streaming.
/// </remarks>
internal sealed class RemoteControlSession : IDisposable
{
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromSeconds(2); // what iOS uses, per pyatv

    private const int HeaderSize = 32;

    private readonly RtspConnection _rtsp;
    private EventChannel? _events;
    private TcpClient? _data;
    private NetworkStream? _dataStream;
    private HapSession? _dataSession;
    private Thread? _reader;
    private System.Threading.Timer? _feedback;
    private readonly object _sendLock = new();
    // pyatv starts at a random value in this range and never changes it; the Apple TV's replies repeat it.
    private readonly ulong _sendSeq = 0x100000000UL + (ulong)RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MediaRemote.Received>> _waiting = new();
    private volatile bool _closed;
    private int _lostSignalled;

    /// <summary>GET /info of the receiver, read before pairing.</summary>
    public Dictionary<string, object?> Info { get; private set; } = new();

    /// <summary>Every MediaRemote message the Apple TV sends, replies to <see cref="RequestAsync"/> included. Raised on the reader thread.</summary>
    public event Action<MediaRemote.Received>? Received;

    /// <summary>The session ended without <see cref="Dispose"/>: the reason, for the log.</summary>
    public event Action<string>? Lost;

    private RemoteControlSession(RtspConnection rtsp) => _rtsp = rtsp;

    /// <param name="pairings">Where the receiver's PIN pairing is; without one this throws <see cref="PairingRequiredException"/>.</param>
    public static async Task<RemoteControlSession> ConnectAsync(IPAddress host, int port, PairingStore pairings, CancellationToken ct)
    {
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        var session = new RemoteControlSession(rtsp);
        try
        {
            await session.SetupAsync(pairings, ct);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task SetupAsync(PairingStore pairings, CancellationToken ct)
    {
        var info = _rtsp.Rtsp("GET", "/info");
        if (info.IsSuccess && info.Body.Length > 0) Info = BPlist.ReadDict(info.Body);
        string name = Info.GetValueOrDefault("name") as string ?? _rtsp.RemoteIp.ToString();
        string? deviceId = Info.GetValueOrDefault("deviceID") as string;
        var pairing = (deviceId != null ? pairings.Find(deviceId) : null)
                      ?? throw new PairingRequiredException(name, deviceId, PairingReason.NotPaired);
        byte[] shared;
        try
        {
            shared = HapHttp.Verify(_rtsp, pairing);
        }
        catch (HapPairingException ex) when (ex.Error == HapError.Authentication)
        {
            throw new PairingRequiredException(name, deviceId, PairingReason.PairingRejected);
        }
        _rtsp.EnableEncryption(
            HapKeys.Derive(shared, "Control-Salt", "Control-Write-Encryption-Key"),
            HapKeys.Derive(shared, "Control-Salt", "Control-Read-Encryption-Key"));

        // The identity is pyatv's default (an iPhone): this is the request tvOS is known to accept. Only the name is ours.
        var setup = Expect(_rtsp.RtspPlist("SETUP", new Dictionary<string, object?>
        {
            ["isRemoteControlOnly"] = true,
            ["osName"] = "iPhone OS",
            ["sourceVersion"] = "550.10",
            ["timingProtocol"] = "None",
            ["model"] = "iPhone10,6",
            ["deviceID"] = "02:48:50:43:41:53",
            ["osVersion"] = "14.7.1",
            ["osBuildVersion"] = "18G82",
            ["macAddress"] = "02:48:50:43:41:53",
            ["sessionUUID"] = MediaRemote.NewId(),
            ["name"] = Environment.MachineName,
        }), "SETUP remote control");
        var reply = BPlist.ReadDict(setup.Body);
        if (Log.Verbose) Log.Debug($"remote control: session reply {BPlist.Describe(reply)}");
        int eventPort = Convert.ToInt32(reply.GetValueOrDefault("eventPort") ?? 0L);
        _events = await EventChannel.ConnectAsync(_rtsp.RemoteIp, eventPort, shared, ct);
        _events.Closed += SignalLost;

        Expect(_rtsp.Rtsp("RECORD"), "RECORD");

        // The seed goes into the data channel's salt as decimal text, so it stays within what a signed plist integer holds.
        long seed = RandomNumberGenerator.GetInt32(int.MaxValue) * (long)int.MaxValue + RandomNumberGenerator.GetInt32(int.MaxValue);
        var stream = Expect(_rtsp.RtspPlist("SETUP", new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["controlType"] = 2,
                    ["channelID"] = MediaRemote.NewId(),
                    ["seed"] = seed,
                    ["clientUUID"] = MediaRemote.NewId(),
                    ["type"] = 130,
                    ["wantsDedicatedSocket"] = true,
                    ["clientTypeUUID"] = "1910A70F-DBC0-4242-AF95-115DB30604E1",
                },
            },
        }), "SETUP data stream");
        if (Log.Verbose) Log.Debug($"remote control: data stream reply {BPlist.Describe(stream.Body)}");
        var streamReply = (BPlist.ReadDict(stream.Body).GetValueOrDefault("streams") as List<object?>)?[0] as Dictionary<string, object?>
                          ?? throw new InvalidDataException("SETUP data stream: no streams in reply");
        int dataPort = Convert.ToInt32(streamReply["dataPort"]);

        string salt = "DataStream-Salt" + seed;
        _dataSession = new HapSession(
            HapKeys.Derive(shared, salt, "DataStream-Output-Encryption-Key"),
            HapKeys.Derive(shared, salt, "DataStream-Input-Encryption-Key"));
        _data = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _data.ConnectAsync(_rtsp.RemoteIp, dataPort, timeout.Token);
        }
        _dataStream = _data.GetStream();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "AirPlay remote control" };
        _reader.Start();
        _feedback = new System.Threading.Timer(_ => Feedback(), null, FeedbackInterval, FeedbackInterval);
        Log.Info($"remote control: connected to {Log.Name(name)} (events {eventPort}, data {dataPort})");
    }

    private static HttpMessage Expect(HttpMessage response, string step) =>
        response.IsSuccess ? response : throw new AirPlayException($"{step} failed: {response.StartLine}");

    private int _feedbackFailures;

    private void Feedback()
    {
        if (_closed) return;
        try
        {
            _feedbackFailures = _rtsp.Rtsp("POST", "/feedback").IsSuccess ? 0 : _feedbackFailures + 1;
        }
        catch (Exception ex)
        {
            _feedbackFailures++;
            Log.Debug($"remote control: feedback failed: {ex.Message}");
        }
        if (_feedbackFailures >= 3) SignalLost("no answer to /feedback");
    }

    /// <summary>Sends one MediaRemote message (a serialized ProtocolMessage).</summary>
    public void Send(byte[] message)
    {
        using var body = new MemoryStream();
        ProtoWriter.WriteVarint(body, (ulong)message.Length);
        body.Write(message);
        var payload = BPlist.Write(new Dictionary<string, object?>
        {
            ["params"] = new Dictionary<string, object?> { ["data"] = body.ToArray() },
        });
        SendFrame("sync", "comm", _sendSeq, payload);
    }

    /// <summary>
    /// Sends the message <paramref name="build"/> makes for a new identifier and waits for the message that repeats it.
    /// </summary>
    public async Task<MediaRemote.Received> RequestAsync(Func<string, byte[]> build, TimeSpan timeout, CancellationToken ct = default)
    {
        string identifier = MediaRemote.NewId();
        var answer = new TaskCompletionSource<MediaRemote.Received>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[identifier] = answer;
        try
        {
            Send(build(identifier));
            return await answer.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _waiting.TryRemove(identifier, out _);
        }
    }

    /// <summary>The frame of the data channel: size, a 12-byte type, a 4-byte command, a sequence number, padding; big-endian.</summary>
    internal static byte[] Frame(string type, string command, ulong seq, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)frame.Length);
        System.Text.Encoding.ASCII.GetBytes(type, frame.AsSpan(4, 12));
        System.Text.Encoding.ASCII.GetBytes(command, frame.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(20), seq);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    private void SendFrame(string type, string command, ulong seq, ReadOnlySpan<byte> payload)
    {
        var frame = Frame(type, command, seq, payload);
        lock (_sendLock)
        {
            if (_dataStream == null || _dataSession == null) throw new InvalidOperationException("remote control: not connected");
            _dataStream.Write(_dataSession.Encrypt(frame));
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[16384];
        var plain = new List<byte>();
        string reason = "the receiver closed the remote-control channel";
        try
        {
            while (!_closed)
            {
                int n = _dataStream!.Read(buf, 0, buf.Length);
                if (n == 0) break;
                plain.AddRange(_dataSession!.Decrypt(buf.AsSpan(0, n)));
                while (plain.Count >= HeaderSize)
                {
                    var header = plain.GetRange(0, HeaderSize).ToArray();
                    int size = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (size < HeaderSize) throw new InvalidDataException($"data channel: frame size {size}");
                    if (plain.Count < size) break;
                    var payload = plain.GetRange(HeaderSize, size - HeaderSize).ToArray();
                    plain.RemoveRange(0, size);
                    // A request ("sync…") wants a reply with its sequence number, whatever it carried.
                    if (header.AsSpan(4, 4).SequenceEqual("sync"u8))
                        SendFrame("rply", "\0\0\0\0", BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(20)), []);
                    if (payload.Length > 0) HandlePayload(payload);
                }
            }
        }
        catch (Exception ex) when (!_closed)
        {
            reason = $"remote-control channel: {ex.Message}";
        }
        catch
        {
            return;
        }
        if (!_closed) SignalLost(reason);
    }

    private void HandlePayload(byte[] payload)
    {
        byte[]? data;
        try
        {
            data = (BPlist.ReadDict(payload).GetValueOrDefault("params") as Dictionary<string, object?>)?.GetValueOrDefault("data") as byte[];
        }
        catch (Exception ex)
        {
            Log.Debug($"remote control: a frame that is no plist ({payload.Length} bytes): {ex.Message}");
            return;
        }
        if (data == null)
        {
            if (Log.Verbose) Log.Debug($"remote control: a frame without data: {BPlist.Describe(payload)}");
            return;
        }
        foreach (var raw in SplitMessages(data))
        {
            if (!ProtoMessage.TryParse(raw, out var message))
            {
                Log.Debug($"remote control: a message that does not parse ({raw.Length} bytes)");
                continue;
            }
            var received = MediaRemote.Received.From(message);
            if (received.Identifier != null && _waiting.TryRemove(received.Identifier, out var waiting)) waiting.TrySetResult(received);
            try
            {
                Received?.Invoke(received);
            }
            catch (Exception ex)
            {
                Log.Debug($"remote control: handler failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The messages in one frame's data: each with its length in front, except one that starts with 0x08 (a message's
    /// own first byte, its type field), which is a single message without a length (pyatv: ConfigureConnection).
    /// </summary>
    internal static List<byte[]> SplitMessages(byte[] data)
    {
        var messages = new List<byte[]>();
        int at = 0;
        while (at < data.Length)
        {
            if (data[at] == 0x08)
            {
                messages.Add(data[at..]);
                break;
            }
            ulong length;
            int used;
            try
            {
                (length, used) = ProtoWriter.ReadVarint(data.AsSpan(at));
            }
            catch (InvalidDataException)
            {
                break;
            }
            at += used;
            if (length > (ulong)(data.Length - at)) break;
            messages.Add(data.AsSpan(at, (int)length).ToArray());
            at += (int)length;
        }
        return messages;
    }

    private void SignalLost(string reason)
    {
        if (_closed || Interlocked.Exchange(ref _lostSignalled, 1) != 0) return;
        Log.Warn($"remote control: lost: {reason}");
        foreach (var waiting in _waiting.Values) waiting.TrySetException(new IOException(reason));
        Lost?.Invoke(reason);
    }

    public void Dispose()
    {
        _closed = true;
        _feedback?.Dispose();
        try { _data?.Close(); } catch { }
        _events?.Dispose();
        _rtsp.Dispose();
        _dataSession?.Dispose();
    }
}
