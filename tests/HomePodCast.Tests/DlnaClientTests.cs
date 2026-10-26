using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaClientTests
{
    [Theory]
    [InlineData("http-get:*:audio/L16;rate=44100;channels=2:*", "L16")]
    [InlineData("http-get:*:audio/L16:*", "L16")]
    [InlineData("http-get:*:audio/L16;RATE=\"44100\";CHANNELS=2:*", "L16")]
    [InlineData("http-get:*:audio/*:*", "L16")]
    [InlineData("http-get:*:AUDIO/*:*", "L16")]
    [InlineData("http-get:*:*:*", "L16")]
    [InlineData("*:*:audio/L16;rate=*;channels=*:*", "L16")]
    [InlineData("http-get:*:audio/wav:*", "Wav")]
    [InlineData("http-get:*:audio/mpeg:*", "Mp3")]
    [InlineData("http-get:*:audio/aac:*", "Aac")]
    [InlineData("http-get:*:audio/L16;rate=48000;channels=2:*,http-get:*:audio/wav:*", "Wav")]
    public void Negotiation_matches_MIME_wildcards_parameters_and_supported_rate(string protocols, string expected) =>
        Assert.Equal(expected, DlnaClient.SelectFormat(protocols.Split(',')).ToString());

    [Theory]
    [InlineData("rtsp-rtp-udp:*:audio/L16:*")]
    [InlineData("http-get:*:audio/L16;rate=48000:*")]
    [InlineData("http-get:*:audio/L16;channels=1:*")]
    [InlineData("http-get:*:audio/L16;rate=44100;rate=48000:*")]
    [InlineData("http-get:*:audio/L16;broken:*")]
    [InlineData("http-get:audio/L16:*")]
    public void Negotiation_rejects_wrong_transport_rate_channels_and_malformed_fields(string protocol) =>
        Assert.Throws<DlnaUnsupportedException>(() => DlnaClient.SelectFormat([protocol]));

    [Fact]
    public void Negotiation_honors_explicit_supported_format_and_rejects_unsupported_preference()
    {
        string[] protocols = ["http-get:*:audio/L16:*", "http-get:*:audio/wav:*"];
        Assert.Equal(DlnaAudioFormat.L16, DlnaClient.SelectFormat(protocols));
        Assert.Equal(DlnaAudioFormat.Wav, DlnaClient.SelectFormat(protocols, DlnaAudioFormat.Wav));
        Assert.Throws<DlnaUnsupportedException>(() => DlnaClient.SelectFormat([protocols[0]], DlnaAudioFormat.Wav));
    }

    [Fact]
    public void Negotiation_prefers_lossless_then_MP3_then_AAC()
    {
        string[] protocols = ["http-get:*:audio/aac:*", "http-get:*:audio/mpeg:*", "http-get:*:audio/wav:*", "http-get:*:audio/L16:*"];
        Assert.Equal(DlnaAudioFormat.L16, DlnaClient.SelectFormat(protocols));
        Assert.Equal(DlnaAudioFormat.Wav, DlnaClient.SelectFormat(protocols[..3]));
        Assert.Equal(DlnaAudioFormat.Mp3, DlnaClient.SelectFormat(protocols[..2]));
        Assert.Equal(DlnaAudioFormat.Aac, DlnaClient.SelectFormat(protocols[..1]));
        Assert.Equal(DlnaAudioFormat.Aac, DlnaClient.SelectFormat(protocols, DlnaAudioFormat.Aac));
    }

    [Fact]
    public void Metadata_receiver_pacing_matches_its_HTTP_DLNA_flags()
    {
        var document = XDocument.Parse(DlnaClient.Metadata(new Uri("http://127.0.0.1/audio"), DlnaAudioFormat.Mp3, DlnaTransferPacing.Receiver));
        Assert.Equal(DlnaAudioFormats.ProtocolInfo(DlnaAudioFormat.Mp3, DlnaTransferPacing.Receiver),
            document.Descendants().Single(e => e.Name.LocalName == "res").Attribute("protocolInfo")!.Value);
    }

    [Fact]
    public async Task A_session_runs_on_a_renderer_that_closes_every_connection_after_one_reply()
    {
        // 2026-10-18 CI: connecting to gmrender-resurrect failed in the ownership check after SetVolume.
        await using var fake = new FakeDlnaRenderer { CloseLikeLibupnp = true };
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        var client = await DlnaClient.ConnectAsync(fake.Device, Options());
        await client.SetVolumeAsync(15, false);
        Assert.Equal(("PLAYING", 15), (fake.TransportState, fake.Volume));
        await client.StopAsync();
        Assert.Equal("STOPPED", fake.TransportState);
        Assert.All(fake.Requests.Where(r => r.Method == "POST"), request => Assert.True(request.CloseRequested));
        Assert.Equal(0, fake.UnansweredRequests);
    }

    [Fact]
    public async Task A_lowered_live_cap_attenuates_PCM_without_waiting_for_remote_volume()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        int produce = 0;
        double cap = 25;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with
        {
            VolumePercent = 20, LiveVolumeCapPercent = () => Volatile.Read(ref cap),
            ReadAudio = (_, pcm) => pcm.Fill(Volatile.Read(ref produce) == 0 ? (short)0 : (short)10000),
        });
        int remoteWrites = fake.Requests.Count(r => r.SoapAction.EndsWith("#SetVolume"));
        Volatile.Write(ref cap, 5);
        Volatile.Write(ref produce, 1);
        Assert.Equal(2500, await pull.FirstNonzeroAsync());
        Assert.Equal(remoteWrites, fake.Requests.Count(r => r.SoapAction.EndsWith("#SetVolume")));
    }

    [Fact]
    public void Metadata_is_valid_DIDL_with_escaped_URI_and_matching_protocol()
    {
        var uri = new Uri("http://127.0.0.1:12345/stream/audio?one=1&two=2");
        var metadata = XDocument.Parse(DlnaClient.Metadata(uri, DlnaAudioFormat.L16));
        Assert.Equal("urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/", metadata.Root!.Name.NamespaceName);
        var resource = Assert.Single(metadata.Descendants(), e => e.Name.LocalName == "res");
        Assert.Equal(uri.AbsoluteUri, resource.Value);
        Assert.Equal(DlnaAudioFormats.ProtocolInfo(DlnaAudioFormat.L16), resource.Attribute("protocolInfo")?.Value);
    }

    [Fact]
    public async Task Connect_caps_remote_volume_before_URI_and_Play_and_serves_owned_PCM()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with { VolumePercent = 80 });
        Assert.Equal(25, fake.Volume);
        Assert.Equal(DlnaAudioFormat.L16, client.Format);
        Assert.Equal(10000, pull.FirstSample);
        var requests = fake.Requests.Where(r => r.Method == "POST").ToArray();
        string[] actions = Actions(fake);
        Assert.Equal(new[] { "GetTransportInfo", "GetMediaInfo", "GetProtocolInfo" }, actions.Take(3));
        int assignIndex = Array.IndexOf(actions, "SetAVTransportURI"), playIndex = Array.IndexOf(actions, "Play");
        Assert.True(Array.IndexOf(actions, "SetVolume") < assignIndex);
        Assert.True(assignIndex < playIndex);
        Assert.All(requests.Take(playIndex).Where(r => r.SoapAction.EndsWith("#SetVolume")), r =>
            Assert.Equal("25", Argument(r, "DesiredVolume")));
        Assert.All(requests.Take(assignIndex).Where(r => r.SoapAction.EndsWith("#SetMute")), r =>
            Assert.Equal("1", Argument(r, "DesiredMute")));
        Assert.Contains(requests.Skip(assignIndex + 1).Take(playIndex - assignIndex - 1), r =>
            r.SoapAction.EndsWith("#SetMute") && Argument(r, "DesiredMute") == "0");
        Assert.Equal(client.StreamUri.AbsoluteUri, fake.CurrentUri);
        Assert.Equal("PLAYING", fake.TransportState);
        var setUri = Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#SetAVTransportURI"));
        var metadata = XDocument.Parse(setUri.Body).Descendants().Single(e => e.Name.LocalName == "CurrentURIMetaData").Value;
        var didl = XDocument.Parse(metadata);
        Assert.Equal(client.StreamUri.AbsoluteUri, didl.Descendants().Single(e => e.Name.LocalName == "res").Value);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(602)]
    public async Task Unsupported_remote_volume_uses_capped_digital_gain(int fault)
    {
        await using var fake = new FakeDlnaRenderer();
        fake.Faults["SetVolume"] = fault;
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with { VolumePercent = 80 });
        Assert.Equal(2500, pull.FirstSample);
        await client.SetVolumeAsync(10, false);
        Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#SetVolume"));
    }

    [Fact]
    public async Task Missing_RenderingControl_uses_digital_volume_and_mute()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device with { RenderingControl = null },
            Options() with { Muted = true, VolumePercent = 80 });
        Assert.Equal(0, pull.FirstSample);
        Assert.DoesNotContain(Actions(fake), action => action is "SetVolume" or "SetMute");
        await client.SetVolumeAsync(10, false);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(602)]
    public async Task Unsupported_remote_mute_still_silences_PCM(int fault)
    {
        await using var fake = new FakeDlnaRenderer();
        fake.Faults["SetMute"] = fault;
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with { Muted = true });
        Assert.Equal(0, pull.FirstSample);
    }

    [Theory]
    [InlineData("SetVolume", 501)]
    [InlineData("SetVolume", 402)]
    [InlineData("SetMute", 501)]
    public async Task Other_volume_faults_abort_before_assigning_or_Playing(string action, int fault)
    {
        await using var fake = new FakeDlnaRenderer();
        fake.Faults[action] = fault;
        var error = await Assert.ThrowsAsync<DlnaActionException>(() => DlnaClient.ConnectAsync(fake.Device, Options()));
        Assert.False(error.IsUnsupportedAction);
        Assert.DoesNotContain(Actions(fake), a => a is "SetAVTransportURI" or "Play" or "Stop");
    }

    [Theory]
    [InlineData("PLAYING", "http://192.0.2.10/phone")]
    [InlineData("PAUSED_PLAYBACK", "http://192.0.2.10/phone")]
    [InlineData("TRANSITIONING", "http://192.0.2.10/phone")]
    [InlineData("UNKNOWN", "http://192.0.2.10/phone")]
    [InlineData("UNKNOWN", "")]
    public async Task Busy_renderer_is_inspected_without_any_write_command(string state, string currentUri)
    {
        await using var fake = new FakeDlnaRenderer { TransportState = state, CurrentUri = currentUri };
        await Assert.ThrowsAsync<DlnaBusyException>(() => DlnaClient.ConnectAsync(fake.Device, Options()));
        Assert.Equal(new[] { "GetTransportInfo", "GetMediaInfo" }, Actions(fake));
    }

    [Theory]
    [InlineData("PLAYING")]
    [InlineData("STOPPED")]
    public async Task Renderer_changed_during_protocol_negotiation_is_refused_before_any_write(string newState)
    {
        await using var fake = new FakeDlnaRenderer();
        var inspectingProtocols = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProtocols = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.AfterActionAsync = async (action, ct) =>
        {
            if (action != "GetProtocolInfo") return;
            inspectingProtocols.TrySetResult();
            await releaseProtocols.Task.WaitAsync(ct);
        };
        var pending = DlnaClient.ConnectAsync(fake.Device, Options());
        await inspectingProtocols.Task.WaitAsync(Guard);
        fake.CurrentUri = "http://192.0.2.10/replacement";
        fake.TransportState = newState;
        releaseProtocols.TrySetResult();
        await Assert.ThrowsAsync<DlnaBusyException>(() => pending);
        Assert.DoesNotContain(Actions(fake), action => action is "SetVolume" or "SetMute" or "SetAVTransportURI" or "Play" or "Stop");
    }

    [Fact]
    public async Task Explicit_takeover_assigns_a_busy_renderer()
    {
        await using var fake = new FakeDlnaRenderer { TransportState = "PLAYING", CurrentUri = "http://192.0.2.10/phone" };
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with { TakeOver = true });
        Assert.Equal(client.StreamUri.AbsoluteUri, fake.CurrentUri);
        Assert.Equal("PLAYING", fake.TransportState);
    }

    [Fact]
    public async Task Takeover_with_unsupported_volume_mutes_old_content_before_assigning_our_URI()
    {
        await using var fake = new FakeDlnaRenderer
        {
            TransportState = "PLAYING", CurrentUri = "http://192.0.2.10/phone", Volume = 100,
        };
        fake.Faults["SetVolume"] = 401;
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device,
            Options() with { TakeOver = true, VolumePercent = 80 });
        var requests = fake.Requests.ToArray();
        int assignIndex = Array.FindIndex(requests, r => r.SoapAction.EndsWith("#SetAVTransportURI"));
        Assert.True(assignIndex > 0);
        Assert.Contains(requests.Take(assignIndex), r => r.SoapAction.EndsWith("#SetMute") && Argument(r, "DesiredMute") == "1");
        Assert.DoesNotContain(requests.Take(assignIndex), r => r.SoapAction.EndsWith("#SetMute") && Argument(r, "DesiredMute") == "0");
        Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#SetVolume"));
        Assert.Equal(2500, pull.FirstSample);
    }

    [Fact]
    public async Task Volume_change_after_replacement_detects_loss_without_writing_remote_controls()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options());
        int before = fake.Requests.Count;
        fake.CurrentUri = "http://192.0.2.10/replacement";
        await Assert.ThrowsAsync<DlnaTakenOverException>(() => client.SetVolumeAsync(15, false));
        Assert.Contains("taken over", await client.Lost.WaitAsync(Guard));
        Assert.DoesNotContain(fake.Requests.Skip(before), r => r.SoapAction.EndsWith("#SetVolume") || r.SoapAction.EndsWith("#SetMute"));
        Assert.True(client.StreamServer.IsStopped);
        await client.DisposeAsync();
        Assert.DoesNotContain(Actions(fake), a => a == "Stop");
    }

    [Fact]
    public async Task Replacement_between_volume_and_mute_prevents_the_second_remote_write()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options());
        int before = fake.Requests.Count;
        fake.AfterActionAsync = (action, _) =>
        {
            if (action == "SetVolume") fake.CurrentUri = "http://192.0.2.10/replacement";
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<DlnaTakenOverException>(() => client.SetVolumeAsync(15, true));
        Assert.Contains("taken over", await client.Lost.WaitAsync(Guard));
        Assert.Single(fake.Requests.Skip(before), r => r.SoapAction.EndsWith("#SetVolume"));
        Assert.DoesNotContain(fake.Requests.Skip(before), r => r.SoapAction.EndsWith("#SetMute"));
        await client.DisposeAsync();
        Assert.DoesNotContain(Actions(fake), a => a == "Stop");
    }

    [Fact]
    public async Task Pull_timeout_closes_listener_and_stops_only_the_assigned_URI()
    {
        await using var fake = new FakeDlnaRenderer();
        await Assert.ThrowsAsync<TimeoutException>(() => DlnaClient.ConnectAsync(fake.Device,
            Options() with { PullTimeout = TimeSpan.FromMilliseconds(100) }));
        Assert.Equal("STOPPED", fake.TransportState);
        Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#Stop"));
        await AssertListenerClosedAsync(new Uri(fake.CurrentUri));
    }

    [Fact]
    public async Task Cancellation_after_assignment_closes_listener_and_stops_owned_URI()
    {
        await using var fake = new FakeDlnaRenderer();
        using var cancel = new CancellationTokenSource();
        fake.OnPlayAsync = (_, _) => { cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DlnaClient.ConnectAsync(fake.Device, Options(), cancel.Token));
        Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#Stop"));
        Assert.Equal("STOPPED", fake.TransportState);
        await AssertListenerClosedAsync(new Uri(fake.CurrentUri));
    }

    [Fact]
    public async Task Replacement_URI_during_failed_connect_is_never_stopped()
    {
        await using var fake = new FakeDlnaRenderer();
        Uri? assigned = null;
        fake.OnPlayAsync = (uri, _) =>
        {
            assigned = uri;
            fake.CurrentUri = "http://192.0.2.10/replacement";
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<TimeoutException>(() => DlnaClient.ConnectAsync(fake.Device,
            Options() with { PullTimeout = TimeSpan.FromMilliseconds(100) }));
        Assert.DoesNotContain(Actions(fake), a => a == "Stop");
        Assert.Equal("PLAYING", fake.TransportState);
        await AssertListenerClosedAsync(Assert.IsType<Uri>(assigned));
    }

    [Fact]
    public async Task Stop_is_idempotent_closes_HTTP_and_stops_owned_renderer_once()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        var client = await DlnaClient.ConnectAsync(fake.Device, Options());
        var uri = client.StreamUri;
        await Task.WhenAll(client.StopAsync(), client.StopAsync(), client.DisposeAsync().AsTask());
        Assert.Equal("STOPPED", fake.TransportState);
        Assert.True(client.StreamServer.IsStopped);
        Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#Stop"));
        await AssertListenerClosedAsync(uri);
        await pull.AssertClosedAsync();
    }

    [Fact]
    public async Task Replacement_URI_is_reported_as_loss_and_never_stopped_on_dispose()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with { PollInterval = TimeSpan.FromMilliseconds(20) });
        fake.CurrentUri = "http://192.0.2.10/replacement";
        Assert.Contains("taken over", await client.Lost.WaitAsync(Guard));
        Assert.True(client.StreamServer.IsStopped);
        await client.DisposeAsync();
        Assert.DoesNotContain(Actions(fake), a => a == "Stop");
        Assert.Equal("PLAYING", fake.TransportState);
        await pull.AssertClosedAsync();
    }

    [Fact]
    public async Task Renderer_STOPPED_is_reported_as_loss_and_closes_HTTP()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with { PollInterval = TimeSpan.FromMilliseconds(20) });
        fake.TransportState = "STOPPED";
        Assert.Contains("stopped", await client.Lost.WaitAsync(Guard));
        Assert.True(client.StreamServer.IsStopped);
        await pull.AssertClosedAsync();
    }

    [Fact]
    public async Task Private_policy_closure_is_reported_as_loss_and_closes_HTTP()
    {
        await using var fake = new FakeDlnaRenderer();
        await using var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        int isPrivate = 1;
        await using var client = await DlnaClient.ConnectAsync(fake.Device,
            Options() with { IsPrivateNetwork = () => Volatile.Read(ref isPrivate) == 1, PollInterval = TimeSpan.FromMilliseconds(20) });
        Volatile.Write(ref isPrivate, 0);
        Assert.Contains("closed", await client.Lost.WaitAsync(Guard));
        Assert.True(client.StreamServer.IsStopped);
        await AssertListenerClosedAsync(client.StreamUri);
        await pull.AssertClosedAsync();
    }

    [Fact]
    public async Task Nonprivate_network_is_rejected_before_any_renderer_request()
    {
        await using var fake = new FakeDlnaRenderer();
        await Assert.ThrowsAsync<DlnaPolicyException>(() => DlnaClient.ConnectAsync(fake.Device,
            Options() with { IsPrivateNetwork = () => false }));
        Assert.Empty(fake.Requests);
    }

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_stalled_HTTP_pull_is_lost_even_if_transport_still_reports_PLAYING(bool activeButStalled)
    {
        await using var fake = new FakeDlnaRenderer();
        var pull = new Pull();
        fake.OnPlayAsync = pull.StartAsync;
        int reads = 0;
        try
        {
            await using var client = await DlnaClient.ConnectAsync(fake.Device, Options() with
            {
                PollInterval = TimeSpan.FromMilliseconds(20), PullTimeout = TimeSpan.FromMilliseconds(200),
                BeforeReadAsync = (_, ct) => activeButStalled && Interlocked.Increment(ref reads) > 1
                    ? new ValueTask(Task.Delay(Timeout.Infinite, ct)) : ValueTask.CompletedTask,
            });
            if (!activeButStalled) await pull.DisposeAsync();
            await client.Lost.WaitAsync(Guard);
            Assert.Equal("PLAYING", fake.TransportState);
            Assert.IsType<IOException>(client.LostException);
            Assert.False(DlnaFailures.RequiresUserAction(client.LostException));
            Assert.True(client.StreamServer.IsStopped);
        }
        finally { await pull.DisposeAsync(); }
    }

    private static DlnaClientOptions Options() => new(IPAddress.Loopback, (_, samples) => samples.Fill(10000), () => true)
    {
        PollInterval = TimeSpan.FromSeconds(30), PullTimeout = Guard,
    };

    private static string[] Actions(FakeDlnaRenderer fake) => fake.Requests.Where(r => r.Method == "POST")
        .Select(r => r.SoapAction[(r.SoapAction.LastIndexOf('#') + 1)..]).ToArray();

    private static string Argument(FakeDlnaRenderer.Request request, string name) =>
        XDocument.Parse(request.Body).Descendants().Single(e => e.Name.LocalName == name).Value;

    private static async Task AssertListenerClosedAsync(Uri uri)
    {
        using var socket = new TcpClient();
        using var guard = new CancellationTokenSource(Guard);
        await Assert.ThrowsAnyAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, uri.Port, guard.Token));
    }

    /// <summary>Only the first PCM sample is pulled inside Play; the open response is held by the test.</summary>
    private sealed class Pull : IAsyncDisposable
    {
        private readonly HttpClient _http = DlnaHttp.CreateClient(IPAddress.Loopback);
        private HttpResponseMessage? _response;
        private Stream? _stream;
        internal short FirstSample { get; private set; }

        internal async Task StartAsync(Uri uri, CancellationToken ct)
        {
            _response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync(ct);
            byte[] sample = new byte[2];
            await _stream.ReadExactlyAsync(sample, ct);
            FirstSample = BinaryPrimitives.ReadInt16BigEndian(sample);
        }

        internal async Task AssertClosedAsync()
        {
            Assert.NotNull(_stream);
            using var guard = new CancellationTokenSource(Guard);
            byte[] buffer = new byte[8192];
            int total = 0;
            try
            {
                int read;
                while ((read = await _stream.ReadAsync(buffer, guard.Token)) != 0)
                {
                    total += read;
                    Assert.True(total < 512 * 1024, "A stopped HTTP stream continued producing PCM.");
                }
            }
            catch (IOException) { } // A closed chunked response has no final chunk and HttpClient reports truncation.
        }

        internal async Task<short> FirstNonzeroAsync()
        {
            Assert.NotNull(_stream);
            using var guard = new CancellationTokenSource(Guard);
            byte[] bytes = new byte[2];
            while (true)
            {
                await _stream.ReadExactlyAsync(bytes, guard.Token);
                short sample = BinaryPrimitives.ReadInt16BigEndian(bytes);
                if (sample != 0) return sample;
            }
        }

        public ValueTask DisposeAsync()
        {
            _response?.Dispose();
            _http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
