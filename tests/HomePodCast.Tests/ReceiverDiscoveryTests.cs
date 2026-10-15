using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public class ReceiverDiscoveryTests
{
    [Theory]
    [InlineData("AudioAccessory6,1", "0x0", "", true, false, ReceiverKind.HomePod)]
    [InlineData("AudioAccessory5,1", "", "", true, true, ReceiverKind.HomePod)]
    [InlineData("AppleTV14,1", "", "", true, false, ReceiverKind.AppleTv)]
    [InlineData("AppleTV5,3", "", "", true, true, ReceiverKind.AppleTv)]
    [InlineData("AirPort10,115", "", "220.68", false, true, ReceiverKind.AppleAirPlay1)]
    [InlineData("AppleTV3,2", "10879", "220.68", true, false, ReceiverKind.ThirdPartyAirPlay1)]
    [InlineData("AppleTV2,1", "", "220.68", false, true, ReceiverKind.ThirdPartyAirPlay1)]
    [InlineData("ShairportSync", "", "105.1", false, true, ReceiverKind.ThirdPartyAirPlay1)]
    [InlineData("ShairportSync", "0x0,0x300", "", true, true, ReceiverKind.ThirdPartyAirPlay2)]
    [InlineData("JBL Authentics 500", "0x0,0x40", "", true, true, ReceiverKind.ThirdPartyAirPlay2)]
    [InlineData("Sonos", "0x0,0x10000", "", true, true, ReceiverKind.ThirdPartyAirPlay2)]
    [InlineData("Denon", "", "370.1", true, true, ReceiverKind.ThirdPartyAirPlay2)]
    [InlineData("", "", "", true, false, ReceiverKind.Unknown)]
    public void Classification_is_pure_and_preserves_Apple_paths(string model, string features, string version,
        bool airplay, bool raop, ReceiverKind expected) =>
        Assert.Equal(expected, ReceiverKinds.Classify(model, new Dictionary<string, string>
            { ["features"] = features, ["srcvers"] = version }, airplay, raop));

    [Fact]
    public void Manufacturer_evidence_can_distinguish_older_Apple_receivers()
    {
        Assert.Equal(ReceiverKind.AppleAirPlay1, ReceiverKinds.Classify("AppleTV3,2",
            new Dictionary<string, string> { ["manufacturer"] = "Apple" }));
        Assert.Equal(ReceiverKind.ThirdPartyAirPlay2, ReceiverKinds.Classify("AudioAccessory6,1",
            new Dictionary<string, string> { ["manufacturer"] = "Other", ["srcvers"] = "300.1" }));
    }

    [Theory]
    [InlineData("0x11223344,0x55667788", 0x5566778811223344UL)]
    [InlineData("10879", 10879UL)]
    [InlineData("0x1000000000000", 1UL << 48)]
    [InlineData("0x0,0x100000000", 0UL)]
    [InlineData("bad", 0UL)]
    [InlineData("0x1,0x2,0x3", 0UL)]
    public void Features_keep_both_words(string text, ulong expected) => Assert.Equal(expected, ReceiverKinds.Features(text));

    [Theory]
    [InlineData(true, 0x8001)]
    [InlineData(false, 1)]
    public void Both_query_modes_request_RAOP_PTR(bool unicast, int expected)
    {
        var packet = Mdns.BuildQuery(Mdns.RaopService, unicast);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(packet.Length - 2)));
        Assert.Contains("_raop", Encoding.UTF8.GetString(packet));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RAOP_and_AirPlay_records_merge_by_MAC_in_either_order(bool raopFirst)
    {
        var records = new Mdns.Records();
        var raop = Answer(Mdns.RaopService, "021122334455@Living room", 5000,
            "am=ShairportSync", "cn=0,1", "et=0,1");
        var airplay = Answer(Mdns.Service, "Living room", 7000,
            "deviceid=02:11:22:33:44:55", "model=ShairportSync", "features=0x0,0x300");
        foreach (var packet in raopFirst ? new[] { raop, airplay } : new[] { airplay, raop })
            Mdns.Parse(packet, records, IPAddress.Loopback);
        var device = Assert.Single(records.Devices());
        Assert.Equal("Living room", device.Name);
        Assert.Equal(7000, device.Port);
        Assert.Equal(5000, device.RaopPort);
        Assert.Equal("0,1", device.RaopTxt!["et"]);
        Assert.Equal(ReceiverKind.ThirdPartyAirPlay2, device.Kind);
    }

    [Fact]
    public void RAOP_only_receiver_keeps_name_model_and_nonstandard_port()
    {
        var records = new Mdns.Records();
        Mdns.Parse(Answer(Mdns.RaopService, "021122334455@Projector", 7001, "am=AppleTV3,2", "srcvers=220.68"),
            records, IPAddress.Loopback);
        var device = Assert.Single(records.Devices());
        Assert.False(device.HasAirPlay);
        Assert.Equal(7001, device.Port);
        Assert.Equal("Projector", device.Name);
        Assert.Equal(ReceiverKind.ThirdPartyAirPlay1, device.Kind);
    }

    [Fact]
    public async Task Multicast_only_fake_is_discovered_by_QM_query()
    {
        using var responder = new UdpClient(AddressFamily.InterNetwork);
        responder.ExclusiveAddressUse = false;
        responder.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        responder.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var group = new IPEndPoint(IPAddress.Parse("239.255.77.77"), ((IPEndPoint)responder.Client.LocalEndPoint!).Port);
        responder.JoinMulticastGroup(group.Address, IPAddress.Loopback);
        responder.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        using var stop = new CancellationTokenSource(Guard);
        var respond = Task.Run(async () =>
        {
            while (true)
            {
                var request = (await responder.ReceiveAsync(stop.Token)).Buffer;
                if ((request[2] & 0x80) != 0 || !Encoding.UTF8.GetString(request).Contains("_raop")) continue;
                if (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(request.Length - 2)) != 1) continue;
                await responder.SendAsync(Answer(Mdns.RaopService, "021122334455@Multicast only", 7001, "am=AppleTV3,2"), group, stop.Token);
                return;
            }
        });
        var found = await Mdns.BrowseNetworkAsync(TimeSpan.FromSeconds(1), [IPAddress.Loopback], group, stop.Token);
        await respond;
        Assert.Equal("Multicast only", Assert.Single(found).Name);
    }

    /// <summary>
    /// Only a guard against a hang: 2026-10-14, a CI run passed 5 s before the first request was written (a busy runner),
    /// and the token closed the connection under it.
    /// </summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    internal const string ProjectorPlist = "<?xml version=\"1.0\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>model</key><string>AppleTV3,2</string><key>features</key><integer>10879</integer><key>protovers</key><string>1.0</string><key>srcvers</key><string>220.68</string><key>vv</key><integer>2</integer></dict></plist>";

    [Fact]
    public void Server_info_XML_fixture_and_binary_info_classify_identically()
    {
        var info = SpeakerInfoReader.Parse(Encoding.UTF8.GetBytes(ProjectorPlist));
        Assert.Equal(10879L, info["features"]);
        Assert.Equal(ReceiverKind.ThirdPartyAirPlay1, ReceiverKinds.Classify("", null, info: info));
        var binary = SpeakerInfoReader.Parse(BPlist.Write(new Dictionary<string, object?>(info)));
        Assert.Equal(info["model"], binary["model"]);
    }

    [Fact]
    public async Task Empty_RTSP_info_falls_back_to_HTTP_on_the_same_advertised_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(Guard);
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                requests.Add((await reader.ReadLineAsync(stop.Token))!);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                var body = i == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(ProjectorPlist);
                await stream.WriteAsync(HttpMessage.Format("HTTP/1.1 200 OK", [new("Content-Length", body.Length.ToString())], body), stop.Token);
            }
        });
        var info = await SpeakerInfoReader.ReadAsync(IPAddress.Loopback, port, stop.Token);
        await server;
        Assert.Equal(new[] { "GET /info RTSP/1.0", "GET /server-info HTTP/1.1" }, requests);
        Assert.Equal("AppleTV3,2", info["model"]);
    }

    [Theory]
    [InlineData("127.0.0.1", "", true, 7000)]
    [InlineData("192.0.2.1", "7001", true, 7001)]
    [InlineData("239.1.1.1", "7000", false, 7000)]
    [InlineData("0.0.0.0", "7000", false, 7000)]
    [InlineData("::1", "7000", false, 7000)]
    [InlineData("192.0.2.1", "65536", false, 65536)]
    [InlineData("hostname", "", false, 7000)]
    public void Manual_endpoint_validation(string host, string text, bool expected, int port)
    {
        Assert.Equal(expected, ManualAirPlayDevice.TryEndpoint(host, text, out _, out int actual));
        Assert.Equal(port, actual);
    }

    [Fact]
    public void Manual_device_round_trips_and_does_not_duplicate_discovered_receiver()
    {
        var info = SpeakerInfoReader.Parse(Encoding.UTF8.GetBytes(ProjectorPlist));
        var manual = ManualAirPlayDevice.FromInfo(IPAddress.Loopback, 7001, info);
        var cfg = new AppConfig { DeviceId = manual.DeviceId, ManualDevices = [manual] };
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Assert.Equal(7001, Assert.Single(SpeakerSelection.Of(copy, [])).Port);
        Assert.Equal(ReceiverKind.ThirdPartyAirPlay1, copy.ManualDevices[0].ToDevice()!.Kind);
        Assert.Single(ManualAirPlayDevice.Include([manual.ToDevice()!], copy.ManualDevices));
    }

    internal static byte[] Answer(string service, string instance, int port, params string[] txt)
    {
        using var message = new MemoryStream();
        message.Write(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 3, 0, 0, 0, 0 });
        byte[] Name(string value)
        {
            using var buffer = new MemoryStream();
            foreach (string label in value.Split('.')) { var data = Encoding.UTF8.GetBytes(label); buffer.WriteByte((byte)data.Length); buffer.Write(data); }
            buffer.WriteByte(0);
            return buffer.ToArray();
        }
        void Record(string name, int type, byte[] data)
        {
            message.Write(Name(name));
            var head = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(head, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), 120);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8), (ushort)data.Length);
            message.Write(head); message.Write(data);
        }
        string full = instance + "." + service;
        Record(service, 12, Name(full));
        var srv = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(4), (ushort)port);
        Record(full, 33, [.. srv, .. Name("fake.local")]);
        using var text = new MemoryStream();
        foreach (var entry in txt) { var bytes = Encoding.UTF8.GetBytes(entry); text.WriteByte((byte)bytes.Length); text.Write(bytes); }
        Record(full, 16, text.ToArray());
        return message.ToArray();
    }
}
