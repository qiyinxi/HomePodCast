using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>
/// One 5.1 surround session (experimental since 1.4) against the fake HomePod: the field-verified sequence, the data
/// frames, every refusal before and after pairing, and every way the session is lost. Loopback only, nothing plays.
/// </summary>
public sealed class SurroundSessionTests
{
    private const ulong Gm = 0x00A1B2C3D4E5F607;
    private const long Offset = 3_000_000_000_000; // the fake HomePod's clock minus ours: 3000 s
    private const int LatencyMs = 1800;

    private sealed class Rig : IDisposable
    {
        public required FakeAirPlayReceiver Fake { get; init; }
        public required TcpListener Data { get; init; }
        public FakeAacEncoder Encoder { get; } = new();
        public AudioFifo Fifo { get; } = new(48000, 40, 250, channels: 6);
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(20));

        public SurroundSetup Setup(Func<SurroundSetup, SurroundSetup>? change = null)
        {
            var setup = new SurroundSetup(Fifo) { Ports = Fake.TimingPorts, Encoder = () => Encoder };
            return change?.Invoke(setup) ?? setup;
        }

        public Task<AirPlayClient> Connect(SurroundSetup? setup = null, double volume = 50) =>
            AirPlayClient.ConnectSurroundAsync(IPAddress.Loopback, Fake.Port, new StreamOptions(LatencyMs, volume) { VolumeCapPercent = 10 },
                setup ?? Setup(), Timeout.Token);

        public void Dispose()
        {
            Fake.Dispose(); Data.Stop(); Data.Dispose(); Timeout.Dispose();
        }
    }

    private static Rig Start(bool grandmaster = true, bool bit39 = true, bool appleTv = false, long flags = FakeAirPlayReceiver.HomePodFlags,
        Func<HttpMessage, int?>? refuse = null)
    {
        var data = new TcpListener(IPAddress.Loopback, 0);
        data.Start();
        var fake = new FakeAirPlayReceiver(appleTv)
        {
            Name = "卧室",
            BufferedDataPort = ((IPEndPoint)data.LocalEndpoint).Port,
            SupportedFormats = new() { ["bufferStream"] = bit39 ? (1L << 39) | (1L << 22) | (1L << 23) : (1L << 40) | (1L << 22) },
            RequireAnchorFlags = true,
            RefuseRequest = refuse,
            StatusFlags = flags,
        };
        if (grandmaster) fake.StartGrandmaster(Gm, Offset);
        return new Rig { Fake = fake, Data = data };
    }

    private static TaskCompletionSource<string> LostOf(AirPlayClient client)
    {
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Lost += reason => lost.TrySetResult(reason);
        if (client.LostReason is { } already) lost.TrySetResult(already);
        return lost;
    }

    private static ulong AnchorNs(Dictionary<string, object?> anchor) =>
        (ulong)(long)anchor["networkTimeSecs"]! * 1_000_000_000
        + (ulong)(((UInt128)unchecked((ulong)(long)anchor["networkTimeFrac"]!) * 1_000_000_000) >> 64);

    [Fact]
    public async Task The_verified_sequence_runs_in_order_and_anchors_in_the_HomePods_timeline()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        using var client = await rig.Connect();
        using var data = await accept;
        long homePodNow = PtpClockObserver.LocalNs(Stopwatch.GetTimestamp()) + Offset;

        Assert.Equal(new[]
        {
            "GET /info", "POST /pair-pin-start", "POST /pair-setup M1 transient", "POST /pair-setup M3",
            "SETUP session [encrypted]", "SETPEERS [encrypted]", "SETUP stream [encrypted]", "RECORD [encrypted]",
            "SET_PARAMETER [encrypted]", "FLUSHBUFFERED [encrypted]", "SETRATEANCHORTIME [encrypted]",
        }, rig.Fake.Requests.Where(r => r != "POST /feedback [encrypted]").Take(11));
        Assert.DoesNotContain("SETRATEANCHORTIME refused [encrypted]", rig.Fake.Requests);

        var session = rig.Fake.SessionSetup!;
        Assert.Equal("PTP", session["timingProtocol"]);
        Assert.False(session.ContainsKey("timingPort"));
        Assert.Equal("HomePodCast", session["name"]);
        var peer = Assert.IsType<Dictionary<string, object?>>(session["timingPeerInfo"]);
        Assert.Equal("127.0.0.1", Assert.Single(Assert.IsType<List<object?>>(peer["Addresses"])));

        var stream = rig.Fake.StreamSetup!;
        Assert.Equal(103L, stream["type"]);
        Assert.Equal(1L << 39, stream["audioFormat"]);
        Assert.Equal(4L, stream["ct"]);
        Assert.Equal(48000L, stream["sr"]);
        Assert.Equal(1024L, stream["spf"]);
        Assert.Equal(0L, stream["controlPort"]);
        Assert.Equal((long)LatencyMs * 48, stream["latencyMin"]);
        Assert.Equal(LatencyMs * 48, client.LatencyFrames);
        Assert.Null(client.ArrivalToRenderMs); // the fake answers arrivalToRenderLatencyMs: never read here

        double db = double.Parse(Assert.Single(rig.Fake.Parameters).Split(':')[1], CultureInfo.InvariantCulture);
        Assert.True(db <= AirPlayClient.PercentToDb(10) + 1e-6, $"volume {db} dB above the 10 % cap");

        var anchor = rig.Fake.RateAnchor!;
        Assert.Equal(0L, anchor["networkTimeFlags"]);
        Assert.Equal((long)Gm, anchor["networkTimeTimelineID"]);
        var sender = client.SurroundSender!;
        Assert.Equal((long)sender.RtpBase, anchor["rtpTime"]);
        Assert.Equal(sender.RtpBase, Convert.ToUInt32(rig.Fake.BufferedFlush!["flushUntilTS"]));
        Assert.InRange((long)AnchorNs(anchor) - homePodNow, (LatencyMs - 50) * 1_000_000L, (LatencyMs + 50) * 1_000_000L);
        Assert.Equal(Gm, client.SurroundClock!.TimelineId);
        Assert.Empty(rig.Fake.Errors);
    }

    [Fact]
    public async Task Data_frames_are_encrypted_type_103_with_the_format_SSRC_and_steps_of_1024()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        using var client = await rig.Connect();
        using var data = await accept;
        var sender = client.SurroundSender!;
        using var aead = new ChaCha20Poly1305(rig.Fake.StreamKey!);
        var wire = data.GetStream();
        for (int n = 0; n < 5; n++)
        {
            byte[] size = new byte[2];
            await wire.ReadExactlyAsync(size, rig.Timeout.Token);
            byte[] packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(size) - 2];
            await wire.ReadExactlyAsync(packet, rig.Timeout.Token);
            Assert.Equal(0x80, packet[0]);
            Assert.Equal(n, packet[1] << 16 | packet[2] << 8 | packet[3]); // 24-bit sequence
            Assert.Equal(unchecked(sender.RtpBase + (uint)n * 1024), BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
            Assert.Equal(0x27000000u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));
            byte[] nonce = new byte[12], aac = new byte[packet.Length - 36];
            packet.AsSpan(packet.Length - 8).CopyTo(nonce.AsSpan(4));
            aead.Decrypt(nonce, packet.AsSpan(12, aac.Length), packet.AsSpan(12 + aac.Length, 16), aac, packet.AsSpan(4, 8));
            Assert.Equal(FakeAacEncoder.Unit(n), aac);
        }
        Assert.Equal(0, rig.Encoder.Peak); // nothing was captured: digital silence
        Assert.True(sender.PacketsSent >= 5);
        Assert.Null(client.LostReason);
    }

    [Fact]
    public async Task Dispose_tears_the_session_down()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        var client = await rig.Connect();
        using var data = await accept;
        client.Dispose();
        Assert.Contains("TEARDOWN [encrypted]", rig.Fake.Requests);
        Assert.Null(client.LostReason);
    }

    [Theory]
    [InlineData(false, false)] // a HomePod without bufferStream bit 39 (a HomePod mini, an older OS)
    [InlineData(true, true)]   // an Apple TV, even one that lists bit 39
    public async Task Without_5_1_it_refuses_before_pairing(bool bit39, bool appleTv)
    {
        using var rig = Start(bit39: bit39, appleTv: appleTv);
        var error = await Assert.ThrowsAsync<SurroundUnavailableException>(() => rig.Connect());
        Assert.Equal(L.T("这台音箱不支持 5.1"), error.Message);
        Assert.DoesNotContain(rig.Fake.Requests, r => r.StartsWith("POST /pair-", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Fake.Requests, r => r.StartsWith("SETUP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_PTP_from_the_HomePod_refuses_5_1_and_tears_down()
    {
        using var rig = Start(grandmaster: false);
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        var error = await Assert.ThrowsAsync<SurroundUnavailableException>(() =>
            rig.Connect(rig.Setup(s => s with { SettleTimeout = TimeSpan.FromMilliseconds(300) })));
        Assert.Equal(L.T("没有收到 HomePod 的 PTP 对时，可能被防火墙拦截"), error.Message);
        Assert.Contains("TEARDOWN [encrypted]", rig.Fake.Requests);
        Assert.DoesNotContain(rig.Fake.Requests, r => r.StartsWith("SETRATEANCHORTIME", StringComparison.Ordinal));
        (await accept).Dispose();
    }

    [Fact]
    public async Task An_anchor_refused_three_times_refuses_5_1_and_tears_down()
    {
        using var rig = Start(refuse: r => r.StartLine.StartsWith("SETRATEANCHORTIME", StringComparison.Ordinal) ? 400 : null);
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        var error = await Assert.ThrowsAsync<SurroundUnavailableException>(() => rig.Connect());
        Assert.Equal(L.F("5.1 连接失败：{0}", "SETRATEANCHORTIME: RTSP/1.0 400 Refused"), error.Message);
        Assert.Equal(3, rig.Fake.Requests.Count(r => r.StartsWith("SETRATEANCHORTIME", StringComparison.Ordinal)));
        Assert.Contains("TEARDOWN [encrypted]", rig.Fake.Requests);
        (await accept).Dispose();
    }

    [Fact]
    public async Task A_step_the_HomePod_refuses_is_a_5_1_refusal_with_the_step()
    {
        using var rig = Start(refuse: r => r.StartLine.StartsWith("RECORD", StringComparison.Ordinal) ? 500 : null);
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        var error = await Assert.ThrowsAsync<SurroundUnavailableException>(() => rig.Connect());
        Assert.Equal(L.F("5.1 连接失败：{0}", "RECORD failed: RTSP/1.0 500 Refused"), error.Message);
        (await accept).Dispose();
    }

    [Fact]
    public async Task The_HomePod_closing_the_data_connection_is_a_loss_a_second_later()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        using var client = await rig.Connect();
        var lost = LostOf(client);
        var watch = Stopwatch.StartNew();
        (await accept).Dispose();
        Assert.Equal(L.F("5.1 环绕声中断：{0}", L.T("HomePod 关闭了音频连接")), await lost.Task.WaitAsync(rig.Timeout.Token));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), $"lost after {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_takeover_closes_the_event_channel_too_and_wins()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        using var client = await rig.Connect();
        var lost = LostOf(client);
        (await accept).Dispose();
        rig.Fake.CloseEventChannels();
        Assert.Equal(EventChannel.ClosedBySpeaker, await lost.Task.WaitAsync(rig.Timeout.Token));
        await Task.Delay(1300);
        Assert.Equal(EventChannel.ClosedBySpeaker, client.LostReason); // the data connection's loss came second: ignored
    }

    [Fact]
    public async Task A_changed_grandmaster_ends_the_session()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        using var client = await rig.Connect();
        using var data = await accept;
        var lost = LostOf(client);
        rig.Fake.ChangeGrandmaster(0x77);
        Assert.Equal(L.F("5.1 环绕声中断：{0}", L.T("HomePod 的 PTP 时钟变了")), await lost.Task.WaitAsync(rig.Timeout.Token));
    }

    [Fact]
    public async Task A_grandmaster_that_goes_quiet_ends_the_session()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        using var client = await rig.Connect(rig.Setup(s => s with { StaleAfter = TimeSpan.FromMilliseconds(500) }));
        using var data = await accept;
        var lost = LostOf(client);
        rig.Fake.StopGrandmaster();
        Assert.Equal(L.F("5.1 环绕声中断：{0}", L.T("HomePod 的 PTP 对时中断了")), await lost.Task.WaitAsync(rig.Timeout.Token));
    }

    [Fact]
    public async Task A_source_that_is_no_longer_5_1_ends_the_session()
    {
        using var rig = Start();
        var accept = rig.Data.AcceptTcpClientAsync(rig.Timeout.Token).AsTask();
        string? problem = null;
        using var client = await rig.Connect(rig.Setup(s => s with { SourceProblem = () => problem }));
        using var data = await accept;
        var lost = LostOf(client);
        problem = "test: the endpoint is stereo now";
        Assert.Equal(L.F("5.1 环绕声中断：{0}", "test: the endpoint is stereo now"), await lost.Task.WaitAsync(rig.Timeout.Token));
    }

    [Fact]
    public async Task The_CLI_never_takes_a_speaker_another_sender_is_using()
    {
        using var rig = Start(flags: 0x1A0404); // bit 17: another sender's session
        var error = await Assert.ThrowsAsync<AirPlayException>(() => rig.Connect(rig.Setup(s => s with { RequireIdle = true })));
        Assert.IsNotType<SurroundUnavailableException>(error);
        Assert.Contains("speaker in use", error.Message);
        Assert.DoesNotContain(rig.Fake.Requests, r => r.StartsWith("POST /pair-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ports_in_use_refuse_5_1_before_the_session_setup()
    {
        using var rig = Start();
        using var held = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)held.Client.LocalEndPoint!).Port;
        var error = await Assert.ThrowsAsync<SurroundUnavailableException>(() =>
            rig.Connect(rig.Setup(s => s with { Ports = s.Ports with { LocalEvent = port } })));
        Assert.Equal(L.T("UDP 端口 319 和 320 被其他程序占用"), error.Message);
        Assert.DoesNotContain(rig.Fake.Requests, r => r.StartsWith("SETUP", StringComparison.Ordinal));
    }
}
