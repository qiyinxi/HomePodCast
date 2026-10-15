using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public class RaopTests
{
    [Fact]
    public void Output_history_keeps_non_integral_QPC_packets_contiguous()
    {
        var timeline = new HomePodCast.Audio.TimelineBuffer(44100, 44100, 0);
        var history = new RaopOutputSink.PacketHistory(timeline);
        // This is the production RAOP deadline formula, at the phase that used to overlap packet four.
        long start = (long)Math.Round(2_500_114 * (double)MediaClock.Frequency / 10_000_000);
        long first = timeline.FrameAt(start);
        var pcm = new short[704];
        var actual = new float[704];
        for (int packet = 0; packet < 1000; packet++)
        {
            Array.Fill(pcm, (short)(packet - 500));
            history.Write(start + (long)(packet * 352.0 * MediaClock.Frequency / 44100), pcm);
            Assert.True(timeline.ReadFrames(first + packet * 352L, actual));
            Assert.All(actual, sample => Assert.Equal((packet - 500) / 32768f, sample));
        }
    }

    [Fact]
    public async Task Output_sink_preserves_RAOP_audio_timeline_cap_mute_and_teardown()
    {
        using var fake = new FakeRaop(false, false);
        using var output = await RaopOutputSink.ConnectAsync(IPAddress.Loopback, fake.Port, new RaopOptions
        {
            Txt = new Dictionary<string, string> { ["et"] = "0", ["cn"] = "1" },
            VolumePercent = 80, VolumeCapPercent = 10, EndTimeout = TimeSpan.FromSeconds(10),
            ReadAudio = pcm => { for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(i - 352); },
        }, CancellationToken.None);
        IOutputSink sink = output;
        Assert.Equal(OutputKind.Raop, sink.Kind);
        Assert.Equal(OutputState.Streaming, sink.State);
        Assert.Equal(2000, sink.MinLatencyMs);
        var first = await fake.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var decoded = DecodeEscape(fake.Decrypt(first.Buffer[12..]));
        Assert.Equal(Enumerable.Range(0, 704).Select(i => (short)(i - 352)), decoded);
        var history = new float[704];
        Assert.True(sink.Timeline!.ReadAt(output.Client.StartedAt, 0, 0, history));
        Assert.Equal(decoded.Select(v => v / 32768f), history);
        Assert.Contains("volume: -27.000000", fake.LastVolume);
        sink.SetVolume(80, mute: true);
        Assert.Contains("volume: -144.000000", fake.LastVolume);
        sink.SetVolume(80);
        Assert.Contains("volume: -27.000000", fake.LastVolume);
        sink.Dispose();
        await fake.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OutputState.Stopped, sink.State);
        Assert.True(sink.Stats.PacketsSent >= 1);
        Assert.Contains("FLUSH", fake.Methods);
        Assert.Equal("TEARDOWN", fake.Methods[^1]);
    }

    [Fact]
    public void ALAC_escape_frame_decodes_signed_stereo_samples()
    {
        var pcm = Enumerable.Range(0, 704).Select(i => unchecked((short)(i * 101 - 32768))).ToArray();
        Assert.Equal(pcm, DecodeEscape(RaopAudio.Alac(pcm)));
        Assert.Equal(1412, RaopAudio.Alac(pcm).Length);
    }

    // Independent bit reader for the uncompressed subset of Apple's Apache-2.0 ALAC decoder format.
    // Full independent decoder coverage is the shairport-sync CI job, which consumes the production frames.
    internal static short[] DecodeEscape(byte[] packet)
    {
        int bit = 0;
        uint Read(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++, bit++) value = value << 1 | (uint)((packet[bit / 8] >> (7 - bit % 8)) & 1);
            return value;
        }
        Assert.Equal(1U, Read(3)); Assert.Equal(0U, Read(4)); Assert.Equal(0U, Read(12)); Assert.Equal(1U, Read(4));
        var samples = new short[704];
        for (int i = 0; i < samples.Length; i++) samples[i] = unchecked((short)Read(16));
        Assert.Equal(7U, Read(3));
        return samples;
    }

    [Fact]
    public void CBC_resets_IV_each_packet_and_keeps_partial_tail()
    {
        byte[] audio = RandomNumberGenerator.GetBytes(1412), key = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        var encrypted = RaopAudio.Encrypt(audio, key, iv);
        Assert.Equal(encrypted, RaopAudio.Encrypt(audio, key, iv));
        Assert.Equal(audio[^4..], encrypted[^4..]);
        using var aes = Aes.Create(); aes.Key = key;
        Assert.Equal(audio[..1408], aes.DecryptCbc(encrypted.AsSpan(0, 1408), iv, PaddingMode.None));
        using var rsa = RSA.Create(2048);
        Assert.Equal(key, rsa.Decrypt(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA1), RSAEncryptionPadding.OaepSHA1));
        using var airport = RaopAudio.AirportPublicKey();
        Assert.Equal(2048, airport.KeySize);
    }

    [Fact]
    public void Digest_matches_RFC_2617_known_answer_and_increments_nonce_count()
    {
        var digest = new RaopDigest("Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");
        string header = digest.Authorization("GET", "/dir/index.html", "Circle Of Life", "Mufasa", "0a4f113b");
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header);
        Assert.Contains("nc=00000001", header);
        Assert.Contains("nc=00000002", digest.Authorization("GET", "/dir/index.html", "Circle Of Life", "Mufasa", "0a4f113b"));
    }

    [Fact]
    public void Legacy_digest_matches_independent_MD5_calculation()
    {
        var digest = new RaopDigest("Digest realm=\"AirPlay\", nonce=\"12345\"");
        string Hash(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        string expected = Hash($"{Hash("HomePodCast:AirPlay:secret")}:12345:{Hash("ANNOUNCE:rtsp://127.0.0.1/1")}");
        Assert.Contains($"response=\"{expected}\"", digest.Authorization("ANNOUNCE", "rtsp://127.0.0.1/1", "secret"));
    }

    [Fact]
    public void Sync_and_resend_preserve_wraparound_RTP_mapping()
    {
        var sync = RaopPackets.Sync(352, 88200, 0x123456789abcdef0, true);
        Assert.Equal(new byte[] { 0x90, 0xd4, 0, 7 }, sync[..4]);
        Assert.Equal(unchecked(352U - 88200), BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(4)));
        Assert.Equal(0x123456789abcdef0UL, BinaryPrimitives.ReadUInt64BigEndian(sync.AsSpan(8)));
        Assert.Equal(352U, BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(16)));
        var audio = RaopPackets.Audio(ushort.MaxValue, uint.MaxValue, 1, new byte[] { 1, 2, 3 });
        Assert.Equal(audio, RaopPackets.Resend(audio)[4..]);
    }

    [Theory]
    [InlineData("0,1", 0)]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("0,4", 4)]
    public void Advertised_encryption_selects_supported_path(string types, int expected) =>
        Assert.Equal(expected, RaopClient.Encryption(new Dictionary<string, string> { ["et"] = types }));

    [Fact]
    public void FairPlay_only_receiver_is_rejected_before_any_socket_is_opened() =>
        Assert.Throws<RaopUnsupportedException>(() => RaopClient.Encryption(new Dictionary<string, string> { ["et"] = "3,5" }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Fake_receiver_validates_session_Digest_audio_and_retransmission(bool encrypted, bool password)
    {
        using var fake = new FakeRaop(encrypted, password);
        int sample = 0;
        using var client = await RaopClient.ConnectAsync(IPAddress.Loopback, fake.Port, new RaopOptions
        {
            Txt = new Dictionary<string, string> { ["et"] = encrypted ? "1" : "0", ["cn"] = "1" },
            RsaPublicKey = fake.Key, Password = password ? "secret" : null, VolumePercent = 80, VolumeCapPercent = 10,
            EndTimeout = TimeSpan.FromSeconds(10),
            ReadAudio = pcm => { for (int i = 0; i < pcm.Length; i++) pcm[i] = unchecked((short)sample++); },
        }, CancellationToken.None);
        var first = await fake.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var pcm = DecodeEscape(fake.Decrypt(first.Buffer[12..]));
        Assert.Equal(Enumerable.Range(0, 704).Select(i => (short)i), pcm);
        Assert.Contains("volume: -27.000000", fake.LastVolume);
        var sync = await fake.Control.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0xd4, sync.Buffer[1]);
        var resendRequest = new byte[] { 0x80, 0xd5, 0, 1, first.Buffer[2], first.Buffer[3], 0, 1 };
        await fake.Control.SendAsync(resendRequest, sync.RemoteEndPoint);
        UdpReceiveResult resent;
        do { resent = await fake.Control.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)); } while (resent.Buffer[1] != 0xd6);
        Assert.Equal(first.Buffer, resent.Buffer[4..]);
        client.Dispose();
        await fake.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("FLUSH", fake.Methods);
        Assert.Equal("TEARDOWN", fake.Methods[^1]);
        Assert.Equal(new[] { "OPTIONS", "ANNOUNCE", "SETUP", "RECORD" }, fake.Methods.Distinct().Take(4));
    }

    [Fact]
    public async Task A_SETUP_reply_without_Session_still_records_and_tears_down()
    {
        using var fake = new FakeRaop(false, false) { NoSession = true };
        var lines = new List<string>();
        using var client = await RaopClient.ConnectAsync(IPAddress.Loopback, fake.Port, new RaopOptions
        {
            Txt = new Dictionary<string, string> { ["et"] = "0", ["cn"] = "1" }, VolumePercent = 10, VolumeCapPercent = 10,
            EndTimeout = TimeSpan.FromSeconds(10), Log = line => { lock (lines) lines.Add(line); },
        }, CancellationToken.None);
        await fake.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose();
        await fake.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "OPTIONS", "ANNOUNCE", "SETUP", "RECORD" }, fake.Methods.Distinct().Take(4));
        lock (lines)
        {
            Assert.Contains(lines, l => l.StartsWith("RAOP SETUP reply: ", StringComparison.Ordinal) && l.Contains("Transport: "));
            Assert.Contains("RAOP SETUP: no Session header, going on without one", lines);
        }
    }

    [Fact]
    public async Task A_SETUP_reply_without_Transport_is_FairPlay_only()
    {
        using var fake = new FakeRaop(false, false) { NoTransport = true, NoSession = true };
        await Assert.ThrowsAsync<RaopUnsupportedException>(() => RaopClient.ConnectAsync(IPAddress.Loopback, fake.Port,
            new RaopOptions { Txt = new Dictionary<string, string> { ["et"] = "0,3,5" } }, CancellationToken.None));
        Assert.Contains("SETUP", fake.Methods);
        Assert.DoesNotContain("RECORD", fake.Methods);
    }

    [Fact]
    public async Task Controller_stops_without_retrying_when_the_receiver_needs_FairPlay()
    {
        using var fake = new FakeRaop(false, false) { NoTransport = true, NoSession = true };
        var device = new AirPlayDevice("Projector", "02:11", IPAddress.Loopback, fake.Port, "AppleTV3,2",
            new Dictionary<string, string> { ["et"] = "0,3,5", ["cn"] = "0,1,2,3" }) { HasAirPlay = false, RaopPort = fake.Port };
        using var controller = new StreamController { ReceiverLookup = _ => device, CaptureFactory = _ => new SilentCapture() };
        controller.Start(device.DeviceId, "127.0.0.1", 120, 10);
        await Until(() => controller.StatusText == L.T("这台设备要求 Apple 的加密，HomePodCast 不支持。"));
        Assert.Equal(StreamState.Idle, controller.State);
        Assert.Null(controller.Capture);
        Assert.DoesNotContain("RECORD", fake.Methods);
    }

    [Fact]
    public async Task Busy_receiver_453_stops_before_RECORD()
    {
        using var fake = new FakeRaop(false, false) { Busy = true };
        await Assert.ThrowsAsync<RaopBusyException>(() => RaopClient.ConnectAsync(IPAddress.Loopback, fake.Port, new RaopOptions(), CancellationToken.None));
        Assert.DoesNotContain("RECORD", fake.Methods);
    }

    [Fact]
    public void Passwords_are_encrypted_and_bound_to_the_receiver()
    {
        string password = "password-" + Guid.NewGuid().ToString("N");
        string encrypted = RaopPassword.Protect("02:11:22:33:44:55", password);
        Assert.DoesNotContain(password, encrypted);
        Assert.Equal(password, RaopPassword.Unprotect("021122334455", encrypted));
        Assert.Null(RaopPassword.Unprotect("another-device", encrypted));
        Assert.Null(RaopPassword.Unprotect("021122334455", "edited"));
    }

    [Fact]
    public async Task Auth_setup_sends_an_unencrypted_Curve25519_offer()
    {
        using var fake = new FakeRaop(false, false);
        using var client = await RaopClient.ConnectAsync(IPAddress.Loopback, fake.Port,
            new RaopOptions { Txt = new Dictionary<string, string> { ["et"] = "4" } }, CancellationToken.None);
        Assert.Equal(new[] { "OPTIONS", "POST", "ANNOUNCE", "SETUP", "RECORD" }, fake.Methods.Take(5));
    }

    [Fact]
    public async Task TXT_password_requirement_is_reported_before_contacting_receiver() =>
        await Assert.ThrowsAsync<RaopPasswordRequiredException>(() => RaopClient.ConnectAsync(IPAddress.Loopback, 1,
            new RaopOptions { Txt = new Dictionary<string, string> { ["pw"] = "true" } }, CancellationToken.None));

    [Fact]
    public async Task Controller_selects_RAOP_and_keeps_capture_effects_volume_and_mute_upstream()
    {
        using var fake = new FakeRaop(false, false);
        var device = new AirPlayDevice("RAOP", "02:11", IPAddress.Loopback, fake.Port, "ShairportSync",
            new Dictionary<string, string> { ["et"] = "0", ["cn"] = "1" }) { HasAirPlay = false, RaopPort = fake.Port };
        using var controller = new StreamController { ReceiverLookup = _ => device, CaptureFactory = _ => new SilentCapture() };
        controller.SetVolumeCap(10);
        controller.Start(device.DeviceId, "127.0.0.1", 120, 80);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Null(controller.Client);
        Assert.NotNull(controller.RaopClient);
        Assert.NotNull(controller.Capture);
        Assert.Equal(2000, controller.EffectiveLatencyMs);
        Assert.Contains("volume: -27.000000", fake.LastVolume);
        controller.SetMuted(true);
        await Until(() => fake.LastVolume.Contains("-144.000000"));
        controller.Stop();
        Assert.Null(controller.Capture);
        Assert.Null(controller.RaopClient);
    }

    [Theory]
    [InlineData(ConnectTrigger.Manual, true)]
    [InlineData(ConnectTrigger.TakeOver, false)]
    [InlineData(ConnectTrigger.Automatic, false)]
    public async Task Controller_busy_reply_only_asks_after_an_unconfirmed_click(ConnectTrigger trigger, bool asks)
    {
        using var fake = new FakeRaop(false, false) { Busy = true };
        var device = new AirPlayDevice("RAOP", "02:11", IPAddress.Loopback, fake.Port, "ShairportSync",
            new Dictionary<string, string>()) { HasAirPlay = false, RaopPort = fake.Port };
        using var controller = new StreamController { ReceiverLookup = _ => device, CaptureFactory = _ => new SilentCapture() };
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RaopTakeoverRequired += _ => prompted.TrySetResult();
        controller.Start(device.DeviceId, "127.0.0.1", 120, 10, trigger: trigger);
        await Until(() => controller.StatusText.Contains(L.T("这台 AirPlay 设备正在被其他发送端使用。")));
        if (asks) await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(asks, prompted.Task.IsCompleted);
        Assert.Equal(trigger == ConnectTrigger.Automatic ? StreamState.Retrying : StreamState.Idle, controller.State);
        Assert.DoesNotContain("RECORD", fake.Methods);
    }

    [Fact]
    public void Mixed_protocol_group_is_rejected_before_opening_any_session()
    {
        var legacy = new AirPlayDevice("RAOP", "legacy", IPAddress.Loopback, 1, "",
            new Dictionary<string, string>()) { HasAirPlay = false, RaopPort = 1 };
        using var controller = new StreamController { ReceiverLookup = id => id == "legacy" ? legacy : null };
        var plan = new GroupPlan(GroupKind.MultiRoom, "group", null,
            [new("legacy", "RAOP", "127.0.0.1"), new("homepod", "HomePod", "127.0.0.1")], false, false, new Dictionary<string, int>());
        controller.StartGroup(plan, 120, 10);
        Assert.Equal(StreamState.Idle, controller.State);
        Assert.Null(controller.Capture);
        Assert.Equal(L.T("AirPlay 1 兼容模式设备不能加入多音箱分组。"), controller.StatusText);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, limit.Token);
    }
    private sealed class SilentCapture : HomePodCast.Audio.ICaptureSource
    {
        public string? DeviceName => "fake"; public double DriftPpm => 0; public float Peak => 0; public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    private sealed class FakeRaop : IDisposable
    {
        private readonly TcpListener _listen = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _password, _encrypted;
        private byte[]? _aesKey, _iv;
        public readonly RSA Key = RSA.Create(2048);
        public readonly UdpClient Audio = new(new IPEndPoint(IPAddress.Loopback, 0)), Control = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _timing = new(new IPEndPoint(IPAddress.Loopback, 0));
        private int _senderTimingPort;
        public List<string> Methods { get; } = [];
        public string LastVolume { get; private set; } = "";
        public bool Busy { get; init; }
        /// <summary>SETUP's reply has no Session header (the XGIMI Z6X projector, 2026-10-14).</summary>
        public bool NoSession { get; init; }
        /// <summary>SETUP's reply has neither Transport nor Session: the projector without FairPlay (2026-10-14).</summary>
        public bool NoTransport { get; init; }
        public TaskCompletionSource TornDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => ((IPEndPoint)_listen.LocalEndpoint).Port;
        public FakeRaop(bool encrypted, bool password)
        {
            _encrypted = encrypted; _password = password;
            _listen.Start();
            _ = Run();
        }
        public byte[] Decrypt(byte[] payload)
        {
            if (!_encrypted) return payload;
            using var aes = Aes.Create(); aes.Key = _aesKey!;
            int blocks = payload.Length / 16 * 16;
            return [.. aes.DecryptCbc(payload.AsSpan(0, blocks), _iv!, PaddingMode.None), .. payload.AsSpan(blocks)];
        }
        private async Task Run()
        {
            try
            {
                using var connection = await _listen.AcceptTcpClientAsync(_stop.Token);
                var stream = connection.GetStream();
                var buffer = new List<byte>(); var bytes = new byte[8192];
                while (!_stop.IsCancellationRequested)
                {
                    var request = HttpMessage.TryParse(buffer);
                    if (request == null)
                    {
                        int count = await stream.ReadAsync(bytes, _stop.Token);
                        if (count == 0) return;
                        buffer.AddRange(bytes.AsSpan(0, count).ToArray()); continue;
                    }
                    string method = request.StartLine.Split(' ')[0]; Methods.Add(method);
                    Assert.NotEmpty(request.Header("Client-Instance")); Assert.NotEmpty(request.Header("DACP-ID")); Assert.NotEmpty(request.Header("Active-Remote"));
                    var headers = new List<KeyValuePair<string, string>> { new("CSeq", request.Header("CSeq")), new("Content-Length", "0") };
                    int status = 200;
                    if (method == "POST")
                    {
                        Assert.Equal("POST /auth-setup HTTP/1.1", request.StartLine);
                        Assert.Equal(33, request.Body.Length); Assert.Equal(1, request.Body[0]);
                    }
                    if (method == "ANNOUNCE")
                    {
                        Assert.Equal("application/sdp", request.Header("Content-Type"));
                        string sdp = Encoding.UTF8.GetString(request.Body);
                        Assert.Contains("a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100", sdp);
                        if (_encrypted)
                        {
                            byte[] Field(string name)
                            {
                                string value = sdp.Split("\r\n").Single(l => l.StartsWith(name))[name.Length..];
                                return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
                            }
                            _aesKey = Key.Decrypt(Field("a=rsaaeskey:"), RSAEncryptionPadding.OaepSHA1); _iv = Field("a=aesiv:");
                        }
                        if (Busy) status = 453;
                        else if (_password && request.Header("Authorization").Length == 0)
                        {
                            status = 401; headers.Add(new("WWW-Authenticate", "Digest realm=\"AirPlay\", nonce=\"12345\""));
                        }
                        else if (_password)
                            Assert.Equal(new RaopDigest("Digest realm=\"AirPlay\", nonce=\"12345\"").Authorization(method, request.StartLine.Split(' ')[1], "secret"), request.Header("Authorization"));
                    }
                    if (method == "SETUP")
                    {
                        Assert.Contains("RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port=", request.Header("Transport"));
                        if (!NoTransport)
                            headers.Add(new("Transport", $"RTP/AVP/UDP;unicast;server_port={((IPEndPoint)Audio.Client.LocalEndPoint!).Port};control_port={((IPEndPoint)Control.Client.LocalEndPoint!).Port};timing_port=0"));
                        if (!NoSession) headers.Add(new("Session", "1"));
                        _senderTimingPort = int.Parse(request.Header("Transport").Split(';').Single(s => s.StartsWith("timing_port=")).Split('=')[1]);
                    }
                    if (method == "RECORD")
                    {
                        Assert.Equal(NoSession ? "" : "1", request.Header("Session")); Assert.StartsWith("seq=", request.Header("RTP-Info"));
                        var timing = new byte[32]; timing[0] = 0x80; timing[1] = 0xd2;
                        BinaryPrimitives.WriteUInt64BigEndian(timing.AsSpan(24), MediaClock.NtpNow());
                        await _timing.SendAsync(timing, new IPEndPoint(IPAddress.Loopback, _senderTimingPort), _stop.Token);
                        var reply = (await _timing.ReceiveAsync(_stop.Token)).Buffer;
                        Assert.Equal(0xd3, reply[1]); Assert.Equal(timing[24..32], reply[8..16]);
                    }
                    if (method == "SET_PARAMETER") { Assert.Equal("text/parameters", request.Header("Content-Type")); LastVolume = Encoding.ASCII.GetString(request.Body); }
                    await stream.WriteAsync(HttpMessage.Format($"RTSP/1.0 {status} Test", headers, []), _stop.Token);
                    if (method == "TEARDOWN") { TornDown.TrySetResult(); return; }
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { TornDown.TrySetException(ex); }
        }
        public void Dispose() { _stop.Cancel(); _listen.Stop(); Audio.Dispose(); Control.Dispose(); _timing.Dispose(); Key.Dispose(); }
    }
}
