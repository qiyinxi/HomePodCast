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
    [InlineData("http-get:*:*:*", "L16")]
    [InlineData("*:*:audio/L16;rate=*;channels=*:*", "L16")]
    [InlineData("http-get:*:audio/wav:*", "Wav")]
    [InlineData("http-get:*:audio/L16;rate=48000;channels=2:*,http-get:*:audio/wav:*", "Wav")]
    public void Negotiation_matches_MIME_wildcards_parameters_and_supported_rate(string protocols, string expected) =>
        Assert.Equal(expected, DlnaClient.SelectFormat(protocols.Split(',')).ToString());

    [Theory]
    [InlineData("http-get:*:audio/mpeg:*")]
    [InlineData("rtsp-rtp-udp:*:audio/L16:*")]
    [InlineData("http-get:*:audio/L16;rate=48000:*")]
    [InlineData("http-get:*:audio/L16;channels=1:*")]
    [InlineData("http-get:*:audio/L16;rate=44100;rate=48000:*")]
    [InlineData("http-get:*:audio/L16;broken:*")]
    [InlineData("http-get:audio/L16:*")]
    public void Negotiation_rejects_compressed_wrong_transport_rate_channels_and_malformed_fields(string protocol) =>
        Assert.Throws<NotSupportedException>(() => DlnaClient.SelectFormat([protocol]));

    [Fact]
    public void Negotiation_honors_explicit_supported_format_and_rejects_unsupported_preference()
    {
        string[] protocols = ["http-get:*:audio/L16:*", "http-get:*:audio/wav:*"];
        Assert.Equal(DlnaAudioFormat.L16, DlnaClient.SelectFormat(protocols));
        Assert.Equal(DlnaAudioFormat.Wav, DlnaClient.SelectFormat(protocols, DlnaAudioFormat.Wav));
        Assert.Throws<NotSupportedException>(() => DlnaClient.SelectFormat([protocols[0]], DlnaAudioFormat.Wav));
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
        string[] actions = Actions(fake);
        Assert.Equal(new[] { "GetTransportInfo", "GetMediaInfo", "GetProtocolInfo", "SetVolume", "SetMute", "SetAVTransportURI", "Play" }, actions);
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
    [InlineData("PLAYING")]
    [InlineData("PAUSED_PLAYBACK")]
    [InlineData("TRANSITIONING")]
    [InlineData("UNKNOWN")]
    public async Task Busy_renderer_is_inspected_without_any_write_command(string state)
    {
        await using var fake = new FakeDlnaRenderer { TransportState = state, CurrentUri = "http://192.0.2.10/phone" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => DlnaClient.ConnectAsync(fake.Device, Options()));
        Assert.Equal(new[] { "GetTransportInfo", "GetMediaInfo" }, Actions(fake));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => DlnaClient.ConnectAsync(fake.Device,
            Options() with { IsPrivateNetwork = () => false }));
        Assert.Empty(fake.Requests);
    }

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static DlnaClientOptions Options() => new(IPAddress.Loopback, (_, samples) => samples.Fill(10000), () => true)
    {
        PollInterval = TimeSpan.FromSeconds(30), PullTimeout = Guard,
    };

    private static string[] Actions(FakeDlnaRenderer fake) => fake.Requests.Where(r => r.Method == "POST")
        .Select(r => r.SoapAction[(r.SoapAction.LastIndexOf('#') + 1)..]).ToArray();

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

        public ValueTask DisposeAsync()
        {
            _response?.Dispose();
            _http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
