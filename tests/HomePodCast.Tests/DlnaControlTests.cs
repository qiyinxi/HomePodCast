using System.Net;
using System.Xml.Linq;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaControlTests
{
    [Fact]
    public async Task Protocol_info_keeps_L16_and_WAV_parameters()
    {
        await using var fake = new FakeDlnaRenderer();
        using var control = new DlnaControl(fake.Device);
        Assert.Equal(new[] { "http-get:*:audio/L16;rate=44100;channels=2:*", "http-get:*:audio/wav:*" },
            await control.GetProtocolInfoAsync());
        var request = Assert.Single(fake.Requests);
        Assert.Equal("urn:schemas-upnp-org:service:ConnectionManager:1#GetProtocolInfo", request.SoapAction);
        Assert.Equal("/connection", request.Path);
    }

    [Fact]
    public async Task Transport_actions_use_advertised_version_escape_values_and_preserve_argument_order()
    {
        await using var fake = new FakeDlnaRenderer();
        using var control = new DlnaControl(fake.Device);
        var uri = new Uri("http://127.0.0.1:23456/audio?one=1&two=2");
        const string didl = "<DIDL-Lite><title>A & B \"title\"</title></DIDL-Lite>";
        Assert.Equal("STOPPED", await control.GetTransportInfoAsync());
        Assert.Equal("", await control.GetMediaInfoAsync());
        await control.SetAvTransportUriAsync(uri, didl);
        await control.PlayAsync();
        Assert.Equal("PLAYING", await control.GetTransportInfoAsync());
        Assert.Equal(uri.AbsoluteUri, await control.GetMediaInfoAsync());
        await control.StopAsync();
        Assert.Equal("STOPPED", fake.TransportState);
        var request = Assert.Single(fake.Requests, r => r.SoapAction.EndsWith("#SetAVTransportURI"));
        Assert.Equal("urn:schemas-upnp-org:service:AVTransport:2#SetAVTransportURI", request.SoapAction);
        var action = XDocument.Parse(request.Body).Descendants().Single(e => e.Name.LocalName == "SetAVTransportURI");
        Assert.Equal("urn:schemas-upnp-org:service:AVTransport:2", action.Name.NamespaceName);
        Assert.Equal(new[] { "InstanceID", "CurrentURI", "CurrentURIMetaData" }, action.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(uri.AbsoluteUri, action.Elements().ElementAt(1).Value);
        Assert.Equal(didl, action.Elements().ElementAt(2).Value);
    }

    [Fact]
    public async Task Every_action_opens_a_fresh_connection_for_renderers_that_close_after_one_reply()
    {
        // 2026-10-17/18 CI: gmrender-resurrect closed the pooled connection that the request after Play or
        // SetVolume had already been sent on, and a POST is never resent by HttpClient.
        await using var fake = new FakeDlnaRenderer { CloseLikeLibupnp = true };
        using var control = new DlnaControl(fake.Device);
        await control.SetAvTransportUriAsync(new Uri("http://127.0.0.1:23456/audio"), "<DIDL-Lite/>");
        await control.PlayAsync();
        Assert.Equal("PLAYING", await control.GetTransportInfoAsync());
        await control.SetVolumeAsync(20);
        Assert.Equal("PLAYING", await control.GetTransportInfoAsync());
        await control.StopAsync();
        Assert.Equal(6, fake.Requests.Count);
        Assert.All(fake.Requests, request => Assert.True(request.CloseRequested));
        Assert.Equal(0, fake.UnansweredRequests);
        Assert.Equal(("STOPPED", 20), (fake.TransportState, fake.Volume));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(100)]
    public async Task Volume_and_mute_use_Master_instance_zero(int volume)
    {
        await using var fake = new FakeDlnaRenderer();
        using var control = new DlnaControl(fake.Device);
        await control.SetVolumeAsync(volume);
        Assert.Equal(volume, await control.GetVolumeAsync());
        await control.SetMuteAsync(true);
        Assert.True(await control.GetMuteAsync());
        await control.SetMuteAsync(false);
        Assert.False(await control.GetMuteAsync());
        foreach (var request in fake.Requests)
        {
            var action = XDocument.Parse(request.Body).Descendants().Single(e => e.Name.LocalName is "SetVolume" or "GetVolume" or "SetMute" or "GetMute");
            Assert.Equal("0", action.Elements().First().Value);
            Assert.Equal("Master", action.Elements().ElementAt(1).Value);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Invalid_volume_never_sends_a_request(int volume)
    {
        await using var fake = new FakeDlnaRenderer();
        using var control = new DlnaControl(fake.Device);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => control.SetVolumeAsync(volume));
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(602, true)]
    [InlineData(501, false)]
    [InlineData(402, false)]
    public async Task SOAP_fault_exposes_only_unsupported_action_as_fallback(int error, bool unsupported)
    {
        await using var fake = new FakeDlnaRenderer();
        fake.Faults["GetVolume"] = error;
        using var control = new DlnaControl(fake.Device);
        var fault = await Assert.ThrowsAsync<DlnaActionException>(() => control.GetVolumeAsync());
        Assert.Equal(error, fault.ErrorCode);
        Assert.Equal("GetVolume", fault.Action);
        Assert.Equal(HttpStatusCode.InternalServerError, fault.StatusCode);
        Assert.Equal(unsupported, fault.IsUnsupportedAction);
    }

    [Fact]
    public async Task Missing_RenderingControl_is_typed_and_does_not_send()
    {
        await using var fake = new FakeDlnaRenderer();
        using var control = new DlnaControl(fake.Device with { RenderingControl = null });
        var fault = await Assert.ThrowsAsync<DlnaActionException>(() => control.GetVolumeAsync());
        Assert.True(fault.IsUnsupportedAction);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData("<broken>")]
    [InlineData("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///test'>]><x>&y;</x>")]
    [InlineData("<Envelope/>")]
    public async Task Malformed_or_unsafe_SOAP_is_rejected(string response)
    {
        await using var fake = new FakeDlnaRenderer();
        fake.ResponseOverrides["GetProtocolInfo"] = response;
        using var control = new DlnaControl(fake.Device);
        await Assert.ThrowsAsync<FormatException>(() => control.GetProtocolInfoAsync());
    }

    [Fact]
    public async Task Oversized_SOAP_and_invalid_reported_volume_are_rejected()
    {
        await using var fake = new FakeDlnaRenderer();
        fake.ResponseOverrides["GetProtocolInfo"] = new string(' ', DlnaXml.MaxBytes + 1);
        fake.ResponseOverrides["GetVolume"] = FakeDlnaRenderer.Envelope(new XElement(
            XName.Get("GetVolumeResponse", fake.Device.RenderingControl!.ServiceType), new XElement("CurrentVolume", "101")));
        using var control = new DlnaControl(fake.Device);
        await Assert.ThrowsAsync<FormatException>(() => control.GetProtocolInfoAsync());
        await Assert.ThrowsAsync<FormatException>(() => control.GetVolumeAsync());
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_a_pending_SOAP_response()
    {
        await using var fake = new FakeDlnaRenderer();
        fake.Delays["GetTransportInfo"] = TimeSpan.FromSeconds(30);
        using var control = new DlnaControl(fake.Device);
        using var cancel = new CancellationTokenSource();
        var pending = control.GetTransportInfoAsync(cancel.Token);
        await WaitUntilAsync(() => !fake.Requests.IsEmpty);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(10, guard.Token);
    }
}
