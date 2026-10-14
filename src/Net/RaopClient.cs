using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

public delegate void RaopPcmReader(Span<short> stereoPcm);
public sealed record RaopOptions
{
    public int LatencyMs { get; init; } = 2000;
    public double? VolumePercent { get; init; }
    public double VolumeCapPercent { get; init; } = 100;
    public string? Password { get; init; }
    public IReadOnlyDictionary<string, string>? Txt { get; init; }
    public int? EncryptionOverride { get; init; }
    public RaopPcmReader? ReadAudio { get; init; }
    public Action<string>? Log { get; init; }
    internal RSA? RsaPublicKey { get; init; }

    /// <summary>
    /// How long Dispose waits for FLUSH and TEARDOWN before it closes the connection. Tests give more: 2026-10-14, on
    /// a busy CI runner the 500 ms ran out mid-exchange twice, and the fake receiver saw the connection aborted instead
    /// of TEARDOWN.
    /// </summary>
    internal TimeSpan EndTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed class RaopPasswordRequiredException() : AirPlayException(L.T("这台 AirPlay 设备需要密码。"));
public sealed class RaopBusyException() : AirPlayException(L.T("这台 AirPlay 设备正在被其他发送端使用。"));

/// <summary>Separate classic RAOP session; never uses HAP pairing or the HomePod realtime sender.</summary>
public sealed class RaopClient : IDisposable
{
    public const string ClosedByReceiver = "receiver ended RAOP session";
    private readonly RtspConnection _rtsp;
    private readonly TimingServer _timing;
    private readonly UdpClient _control, _audio;
    private readonly RaopOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _requests = new(), _resends = new();
    private readonly Dictionary<ushort, byte[]> _history = new();
    private readonly Queue<ushort> _historyOrder = new();
    private readonly byte[]? _key, _iv;
    private readonly bool _pcm;
    private readonly uint _ssrc = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly ushort _firstSeq = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
    private readonly uint _rtpBase = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private RaopDigest? _digest;
    private string? _session;
    private IPEndPoint? _dataPeer, _controlPeer;
    private Task[] _tasks = [];
    private int _disposed;
    private readonly object _events = new();
    private string? _failureReason;
    private Action<string>? _lostHandlers;
    private bool _recording;

    public double VolumeCapPercent { get; set; }
    public int LatencyFrames => _options.LatencyMs * RaopAudio.SampleRate / 1000;
    public long PacketsSent { get; private set; }
    public long StartedAt { get; private set; }
    public long RetransmitRequests { get; private set; }
    public long Retransmitted { get; private set; }
    public long RetransmitMisses { get; private set; }
    public int TimingRequests => _timing.RequestsAnswered;
    public event Action<string>? Lost
    {
        add
        {
            string? reason;
            lock (_events) { _lostHandlers += value; reason = _failureReason; }
            if (reason != null) value?.Invoke(reason);
        }
        remove { lock (_events) _lostHandlers -= value; }
    }

    private RaopClient(RtspConnection rtsp, RaopOptions options, int encryption)
    {
        _rtsp = rtsp; _options = options; VolumeCapPercent = options.VolumeCapPercent;
        var codecs = Tokens(options.Txt?.GetValueOrDefault("cn"));
        _pcm = codecs.Contains(0) && !codecs.Contains(1);
        if (codecs.Count > 0 && !codecs.Contains(0) && !codecs.Contains(1)) throw new AirPlayException("RAOP receiver has no ALAC or PCM codec");
        if (encryption == 1) { _key = RandomNumberGenerator.GetBytes(16); _iv = RandomNumberGenerator.GetBytes(16); }
        _timing = new TimingServer(rtsp.LocalIp);
        try
        {
            _control = new UdpClient(new IPEndPoint(rtsp.LocalIp, 0));
            try { _audio = new UdpClient(new IPEndPoint(rtsp.LocalIp, 0)); }
            catch { _control.Dispose(); throw; }
        }
        catch { _timing.Dispose(); throw; }
    }

    public static int Encryption(IReadOnlyDictionary<string, string>? txt, int? forced = null)
    {
        var types = Tokens(txt?.GetValueOrDefault("et"));
        if (types.Count == 0) types.Add(0);
        if (forced is { } selected && types.Contains(selected) && selected is 0 or 1 or 4) return selected;
        if (forced != null) throw new ArgumentException("Requested RAOP encryption is not advertised");
        if (types.Contains(4)) return 4;
        if (types.Contains(0)) return 0;
        if (types.Contains(1)) return 1;
        throw new AirPlayException(L.T("这台设备要求 Apple 的加密，HomePodCast 不支持。"));
    }

    private static HashSet<int> Tokens(string? text) => (text ?? "").Split(',')
        .Select(v => int.TryParse(v.Trim(), out int n) ? n : -1).Where(n => n >= 0).ToHashSet();

    public static async Task<RaopClient> ConnectAsync(IPAddress host, int port, RaopOptions options, CancellationToken ct)
    {
        if (options.LatencyMs is not (1000 or 1500 or 2000)) throw new ArgumentOutOfRangeException(nameof(options), "RAOP latency must be 1000, 1500 or 2000 ms");
        int encryption = Encryption(options.Txt, options.EncryptionOverride);
        if (options.Password == null && bool.TryParse(options.Txt?.GetValueOrDefault("pw"), out var passwordRequired) && passwordRequired)
            throw new RaopPasswordRequiredException();
        var rtsp = await RtspConnection.ConnectAsync(host, port, ct);
        RaopClient? client = null;
        try
        {
            client = new RaopClient(rtsp, options, encryption);
            using var close = ct.Register(rtsp.Dispose);
            await Task.Run(() => client.Setup(encryption), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            client._tasks = [Task.Run(client.SendLoop), Task.Run(client.ControlLoop), Task.Run(client.KeepAliveLoop)];
            return client;
        }
        catch
        {
            client?.Dispose(); rtsp.Dispose();
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    internal string Sdp(int encryption)
    {
        var text = new StringBuilder()
            .Append($"v=0\r\no=iTunes {_rtsp.SessionId} 0 IN IP4 {_rtsp.LocalIp}\r\ns=iTunes\r\nc=IN IP4 {_rtsp.RemoteIp}\r\nt=0 0\r\nm=audio 0 RTP/AVP 96\r\n")
            .Append(_pcm ? "a=rtpmap:96 L16/44100/2\r\n" : "a=rtpmap:96 AppleLossless\r\na=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100\r\n");
        if (encryption == 1)
        {
            using var airport = _options.RsaPublicKey == null ? RaopAudio.AirportPublicKey() : null;
            var key = (_options.RsaPublicKey ?? airport!).Encrypt(_key!, RSAEncryptionPadding.OaepSHA1);
            text.Append("a=rsaaeskey:").Append(Convert.ToBase64String(key).TrimEnd('=')).Append("\r\n")
                .Append("a=aesiv:").Append(Convert.ToBase64String(_iv!).TrimEnd('=')).Append("\r\n");
        }
        text.Append($"a=min-latency:{LatencyFrames}\r\na=max-latency:{LatencyFrames}\r\n");
        return text.ToString();
    }

    private void Setup(int encryption)
    {
        Request("OPTIONS", "*");
        if (encryption == 4)
        {
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                var response = _rtsp.Http("POST", "/auth-setup", RtspConnection.UserAgent, "application/octet-stream",
                    [1, .. X25519.PublicKey(secret)],
                    [new("Client-Instance", _rtsp.DacpId), new("DACP-ID", _rtsp.DacpId), new("Active-Remote", _rtsp.ActiveRemote.ToString())]);
                _options.Log?.Invoke($"RAOP POST /auth-setup: {response.StartLine}");
                Check("POST /auth-setup", response);
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        Request("ANNOUNCE", body: Encoding.UTF8.GetBytes(Sdp(encryption)), contentType: "application/sdp");
        var setup = Request("SETUP", extra: [new("Transport", $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={((IPEndPoint)_control.Client.LocalEndPoint!).Port};timing_port={_timing.Port}")]);
        _session = setup.Header("Session").Split(';')[0];
        if (string.IsNullOrWhiteSpace(_session)) throw new AirPlayException("RAOP SETUP: missing Session");
        var transport = setup.Header("Transport").Split(';').Select(p => p.Trim().Split('=', 2))
            .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        int Port(string key) => transport.TryGetValue(key, out var text) && int.TryParse(text, out int n) && n is > 0 and <= 65535
            ? n : throw new AirPlayException("RAOP SETUP: invalid " + key);
        _dataPeer = new IPEndPoint(_rtsp.RemoteIp, Port("server_port"));
        _controlPeer = new IPEndPoint(_rtsp.RemoteIp, Port("control_port"));
        Request("RECORD", extra: [new("RTP-Info", $"seq={_firstSeq};rtptime={_rtpBase}"), new("Range", "npt=0-")]);
        _recording = true;
        SetVolume(_options.VolumePercent ?? 0); // an unknown initial volume never bypasses the cap
    }

    private static void Check(string method, HttpMessage response)
    {
        if (response.StatusCode == 453) throw new RaopBusyException();
        if (!response.IsSuccess) throw new AirPlayException($"RAOP {method}: {response.StartLine}");
    }

    private HttpMessage Request(string method, string? uri = null, string? contentType = null, byte[]? body = null,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        lock (_requests)
        {
            uri ??= _rtsp.Uri;
            List<KeyValuePair<string, string>> Headers()
            {
                var headers = extra?.ToList() ?? [];
                if (_session is { Length: > 0 }) headers.Add(new("Session", _session));
                if (_digest != null && _options.Password != null) headers.Add(new("Authorization", _digest.Authorization(method, uri, _options.Password)));
                return headers;
            }
            var response = _rtsp.Rtsp(method, uri, contentType, body, Headers());
            _options.Log?.Invoke($"RAOP {method}: {response.StartLine}");
            if (response.StatusCode == 401)
            {
                if (_options.Password == null) throw new RaopPasswordRequiredException();
                _digest = new RaopDigest(response.Header("WWW-Authenticate"));
                response = _rtsp.Rtsp(method, uri, contentType, body, Headers());
                _options.Log?.Invoke($"RAOP {method} authenticated: {response.StartLine}");
                if (response.StatusCode == 401) throw new RaopPasswordRequiredException();
            }
            Check(method, response);
            return response;
        }
    }

    public void SetVolume(double percent, bool mute = false)
    {
        percent = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, Math.Clamp(VolumeCapPercent, 0, 100));
        double db = mute || percent <= 0 ? -144 : -30 + percent * 0.3;
        Request("SET_PARAMETER", contentType: "text/parameters", body: Encoding.ASCII.GetBytes("volume: " + db.ToString("0.000000", CultureInfo.InvariantCulture) + "\r\n"));
    }

    private async Task SendLoop()
    {
        // 2026-10-14: shairport-sync 4.3.7 discards sync before the first NTP exchange. Starting audio
        // before that loses the beginning of a 1 s stream. Some emulators omit timing, so bound this wait.
        long timingDeadline = MediaClock.Now + MediaClock.FromMs(2000);
        try
        {
            while (_timing.RequestsAnswered == 0 && MediaClock.Now < timingDeadline)
                await Task.Delay(10, _stop.Token);
        }
        catch (OperationCanceledException) { return; }
        var pcm = new short[RaopAudio.FramesPerPacket * 2];
        long start = MediaClock.Now, n = 0, nextSync = start;
        StartedAt = start;
        bool firstSync = true;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                long deadline = start + (long)(n * (double)RaopAudio.FramesPerPacket * MediaClock.Frequency / RaopAudio.SampleRate);
                double wait = MediaClock.ToMs(deadline - MediaClock.Now);
                if (wait >= 1) { await Task.Delay(TimeSpan.FromMilliseconds(wait), _stop.Token); continue; }
                uint timestamp = unchecked(_rtpBase + (uint)(n * RaopAudio.FramesPerPacket));
                if (deadline >= nextSync)
                {
                    await _control.SendAsync(RaopPackets.Sync(timestamp, (uint)LatencyFrames, MediaClock.NtpAt(deadline), firstSync), _controlPeer!, _stop.Token);
                    firstSync = false; nextSync += MediaClock.FromMs(1000);
                }
                pcm.AsSpan().Clear();
                _options.ReadAudio?.Invoke(pcm);
                var payload = _pcm ? RaopAudio.L16(pcm) : RaopAudio.Alac(pcm);
                if (_key != null) payload = RaopAudio.Encrypt(payload, _key, _iv!);
                ushort sequence = unchecked((ushort)(_firstSeq + n));
                var packet = RaopPackets.Audio(sequence, timestamp, _ssrc, payload, n == 0);
                lock (_resends)
                {
                    _history[sequence] = packet; _historyOrder.Enqueue(sequence);
                    if (_historyOrder.Count > 512) _history.Remove(_historyOrder.Dequeue()); // >4 s at 352 frames/packet
                }
                await _audio.SendAsync(packet, _dataPeer!, _stop.Token);
                n++; PacketsSent++;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { SignalLost(ex); }
    }

    private async Task ControlLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await _control.ReceiveAsync(_stop.Token);
                var request = message.Buffer;
                if (!message.RemoteEndPoint.Equals(_controlPeer) || request.Length < 8 || request[0] != 0x80 || (request[1] & 0x7f) != 0x55) continue;
                int count = Math.Min(512, (int)BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6)));
                ushort first = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4));
                RetransmitRequests++;
                for (int i = 0; i < count; i++)
                {
                    byte[]? packet;
                    lock (_resends) packet = _history.GetValueOrDefault(unchecked((ushort)(first + i)));
                    if (packet == null) { RetransmitMisses++; continue; }
                    await _control.SendAsync(RaopPackets.Resend(packet), _controlPeer!, _stop.Token);
                    Retransmitted++;
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { SignalLost(ex); }
    }

    private async Task KeepAliveLoop()
    {
        try
        {
            while (true) { await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token); Request("OPTIONS", "*"); }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { SignalLost(ex); }
    }

    private void SignalLost(Exception ex)
    {
        string reason = ex is IOException && ex.Message == "connection closed by speaker" ? ClosedByReceiver : ex.Message;
        Action<string>? handlers;
        lock (_events)
        {
            if (_failureReason != null) return;
            _failureReason = reason; handlers = _lostHandlers;
        }
        _stop.Cancel();
        handlers?.Invoke(reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _audio.Dispose(); _control.Dispose(); _timing.Dispose();
        if (_recording)
        {
            var end = Task.Run(() =>
            {
                try { Request("FLUSH", extra: [new("RTP-Info", $"seq={unchecked((ushort)(_firstSeq + PacketsSent))};rtptime={unchecked(_rtpBase + (uint)(PacketsSent * RaopAudio.FramesPerPacket))}")]); } catch { }
                try { Request("TEARDOWN"); } catch { }
            });
            try { end.Wait(_options.EndTimeout); } catch (AggregateException) { }
        }
        _rtsp.Dispose();
        bool ended = false;
        try { ended = Task.WaitAll(_tasks, TimeSpan.FromSeconds(1)); } catch (AggregateException) { ended = _tasks.All(t => t.IsCompleted); }
        if (ended) _stop.Dispose();
        else _ = Task.WhenAll(_tasks).ContinueWith(t => { _ = t.Exception; _stop.Dispose(); }, TaskScheduler.Default);
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
    }
}
