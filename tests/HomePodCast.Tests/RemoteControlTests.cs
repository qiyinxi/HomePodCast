using System.Text;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public class ProtoBufTests
{
    [Fact]
    public void Writes_the_wire_format_of_the_protobuf_documentation()
    {
        // The encoding guide's examples: field 1 = 150, and field 2 = "testing".
        Assert.Equal(new byte[] { 0x08, 0x96, 0x01 }, new ProtoWriter().Varint(1, 150).ToArray());
        Assert.Equal(new byte[] { 0x12, 0x07, 0x74, 0x65, 0x73, 0x74, 0x69, 0x6E, 0x67 }, new ProtoWriter().String(2, "testing").ToArray());
        // A field number above 15 takes a two-byte tag: 85 << 3 | 2 = 682.
        Assert.Equal(new byte[] { 0xAA, 0x05, 0x01, 0x41 }, new ProtoWriter().String(85, "A").ToArray());
    }

    [Fact]
    public void Reads_back_what_it_wrote_field_by_field()
    {
        var bytes = new ProtoWriter()
            .Varint(1, 48)
            .String(2, "卧室")
            .Bool(9, true)
            .Float(24, 0.42f)
            .Message(41, new ProtoWriter().String(1, "left").Varint(11, 12))
            .Message(41, new ProtoWriter().String(1, "right").Varint(11, 12))
            .Varint(300, ulong.MaxValue)
            .ToArray();

        var message = ProtoMessage.Parse(bytes);

        Assert.Equal(48ul, message.Varint(1));
        Assert.Equal("卧室", message.String(2));
        Assert.True(message.Bool(9));
        Assert.False(message.Bool(10)); // not there
        Assert.Equal(0.42f, message.Float(24));
        Assert.Equal(["left", "right"], message.Messages(41).Select(m => m.String(1)));
        Assert.Equal(ulong.MaxValue, message.Varint(300));
        Assert.Null(message.String(1)); // a number, not text
        Assert.Null(message.Message(77));
    }

    [Theory]
    [InlineData(new byte[] { 0x08 })]                   // a varint that never comes
    [InlineData(new byte[] { 0x12, 0x05, 0x41 })]       // text longer than the message
    [InlineData(new byte[] { 0x0D, 0x01, 0x02 })]       // a cut-off 32-bit field
    [InlineData(new byte[] { 0x00, 0x01 })]             // field number 0
    [InlineData(new byte[] { 0x0B })]                   // wire type 3 (groups): not used by MediaRemote
    public void Refuses_what_is_not_a_message(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => ProtoMessage.Parse(bytes));
        Assert.False(ProtoMessage.TryParse(bytes, out _));
    }

    [Fact]
    public void Describes_a_message_by_field_number_without_showing_bytes_whole()
    {
        var key = Enumerable.Range(0xF0, 16).Select(i => (byte)i).ToArray(); // no text, no message
        var message = ProtoMessage.Parse(new ProtoWriter()
            .Varint(1, 65)
            .String(2, "AAD51CBC")
            .Bytes(5, key)
            .Message(69, new ProtoWriter().String(1, "书房电视").Float(24, 0.5f))
            .ToArray());

        Assert.Equal("{1=65, 2=\"AAD51CBC\", 5=<16 bytes F0F1F2F3F4F5F6F7…>, 69={1=\"书房电视\", 24=0.5f}}", message.Describe());
    }
}

public class MediaRemoteTests
{
    [Fact]
    public void An_output_change_names_the_devices_in_the_cluster_aware_list()
    {
        var bytes = MediaRemote.ModifyOutputContextMessage(MediaRemote.OutputChange.Set, ["AB"]);

        // type 48, errorCode 0, extension 52 {type = SharedAudioPresentation, clusterAwareSettingDevices = ["AB"]}, then our UUID.
        Assert.Equal(new byte[] { 0x08, 0x30, 0x20, 0x00, 0xA2, 0x03, 0x06, 0x08, 0x01, 0x3A, 0x02, 0x41, 0x42, 0xAA, 0x05, 0x24 }, bytes[..16]);
        Assert.Equal(16 + 36, bytes.Length);

        ProtoMessage Inner(MediaRemote.OutputChange change) =>
            MediaRemote.Received.From(ProtoMessage.Parse(MediaRemote.ModifyOutputContextMessage(change, ["one", "two"]))).Inner;
        Assert.Equal(["one", "two"], Inner(MediaRemote.OutputChange.Add).Strings(5));
        Assert.Equal(["one", "two"], Inner(MediaRemote.OutputChange.Remove).Strings(6));
        Assert.Equal(["one", "two"], Inner(MediaRemote.OutputChange.Set).Strings(7));

        // With an identifier the Apple TV answers (2026-11-12: error 28 for a home-theater member); the plain lists are fields 2 to 4.
        var plain = MediaRemote.Received.From(ProtoMessage.Parse(
            MediaRemote.ModifyOutputContextMessage(MediaRemote.OutputChange.Remove, ["one"], "REQUEST-2", clusterAware: false)));
        Assert.Equal("REQUEST-2", plain.Identifier);
        Assert.Equal(["one"], plain.Inner.Strings(3));
        Assert.Empty(plain.Inner.Strings(6));
    }

    [Fact]
    public void The_first_message_says_who_we_are_and_asks_for_an_answer()
    {
        var received = MediaRemote.Received.From(ProtoMessage.Parse(MediaRemote.DeviceInfoMessage("REQUEST-1", "OUR-ID", "电脑")));

        Assert.Equal(MediaRemote.DeviceInfo, received.Type);
        Assert.Equal("REQUEST-1", received.Identifier);
        Assert.Equal(0, received.ErrorCode);
        Assert.Equal("OUR-ID", received.Inner.String(1));
        Assert.Equal("电脑", received.Inner.String(2));
        Assert.Equal(1ul, received.Inner.Varint(7));   // protocolVersion
        Assert.Equal(36, received.Whole.String(85)!.Length); // every message carries a UUID of its own
    }

    [Fact]
    public void The_updates_asked_for_leave_out_artwork_and_the_keyboard()
    {
        var inner = MediaRemote.Received.From(ProtoMessage.Parse(MediaRemote.ClientUpdatesConfigMessage("R", nowPlaying: true))).Inner;

        Assert.False(inner.Bool(1)); // artwork
        Assert.True(inner.Bool(2));  // now playing
        Assert.True(inner.Bool(3));  // volume
        Assert.False(inner.Bool(4)); // keyboard
        Assert.True(inner.Bool(5));  // output devices
    }

    /// <summary>
    /// 2026-11-12, the maintainer's Apple TV 4K (tvOS build 24J361) with a HomePod pair as home-theater speakers: its
    /// DeviceInfo names a cluster of type 2 that the Apple TV leads, with itself and both HomePods as clustered devices.
    /// </summary>
    private static ProtoWriter HomeTheaterDeviceInfo() => new ProtoWriter()
        .String(1, "3DB78E9F").String(2, "书房电视").String(19, "3DB78E9F").String(26, "AAD51CBC").String(27, "书房电视")
        .Bool(29, true).Bool(30, false).String(33, "TVAirPlay").String(35, "AAD51CBC").Varint(37, 2).String(39, "AppleTV14,1")
        .String(41, "577E1BCA").String(42, "AAD51CBC")
        .Message(44, new ProtoWriter().String(2, "卧室").String(19, "52E4AA3F").String(39, "AudioAccessory6,1"))
        .Message(44, new ProtoWriter().String(2, "书房电视").String(19, "3DB78E9F").String(39, "AppleTV14,1"))
        .Message(44, new ProtoWriter().String(2, "卧室").String(19, "02DC8E68").String(39, "AudioAccessory6,1"));

    [Fact]
    public void A_home_theater_Apple_TV_is_named_by_its_cluster()
    {
        var device = MediaRemote.Device.From(ProtoMessage.Parse(HomeTheaterDeviceInfo().ToArray()));

        Assert.Equal("书房电视", device.Name);
        Assert.Equal("AAD51CBC", device.OutputUid);
        Assert.Equal(2, device.ClusterType);
        Assert.True(device.IsGroupLeader);
        Assert.False(device.IsAirPlayActive);
        Assert.Equal(["TVAirPlay"], device.AirPlayReceivers);
        Assert.Equal(["52E4AA3F", "3DB78E9F", "02DC8E68"], device.Clustered.Select(d => d.DeviceUid));
        Assert.Empty(device.Grouped);

        var alone = MediaRemote.Device.From(ProtoMessage.Parse(new ProtoWriter().String(2, "客厅").String(19, "ABCDEF01").ToArray()));
        Assert.Equal("ABCDEF01", alone.OutputUid); // no cluster: the device's own UID
    }

    [Fact]
    public void Describes_what_the_Apple_TV_reports()
    {
        static MediaRemote.Received Of(int type, int field, ProtoWriter inner) => MediaRemote.Received.From(
            ProtoMessage.Parse(new ProtoWriter().Varint(1, (ulong)type).Message(field, inner).String(85, "U").ToArray()));

        var info = MediaRemote.Describe(Of(MediaRemote.DeviceInfoUpdate, 20, HomeTheaterDeviceInfo().Bool(30, true))).ToList();
        Assert.Equal(5, info.Count); // the head, the Apple TV, its three clustered devices
        Assert.Contains("cluster=AAD51CBC (type 2)", info[1]);
        Assert.Contains("airplayActive=True", info[1]);
        Assert.StartsWith("    clustered: ", info[2]);

        var volume = MediaRemote.Describe(Of(MediaRemote.VolumeDidChange, 56,
            new ProtoWriter().Float(1, 0.42f).String(2, "AAD51CBC").String(3, "52E4AA3F")));
        Assert.Equal(["VolumeDidChange: volume=42 % endpoint=AAD51CBC output=52E4AA3F"], volume);

        var outputs = MediaRemote.Describe(Of(MediaRemote.UpdateOutputDevice, 69, new ProtoWriter().Message(3, new ProtoWriter()
            .String(1, "书房电视").String(2, "AAD51CBC").Varint(10, 1).Varint(11, 15).Varint(42, 2)
            .Message(41, new ProtoWriter().String(1, "卧室").String(2, "52E4AA3F").Varint(11, 12))))).ToList();
        Assert.Equal(3, outputs.Count);
        Assert.Contains("uid=AAD51CBC", outputs[1]);
        Assert.Contains("member: ", outputs[2]);

        var unknown = MediaRemote.Describe(Of(64, 68, new ProtoWriter().String(3, "AAD51CBC")));
        Assert.Equal(["VolumeControlCapabilitiesDidChange: {3=\"AAD51CBC\"}"], unknown);

        var error = MediaRemote.Received.From(ProtoMessage.Parse(new ProtoWriter().Varint(1, 48).Varint(4, 28).String(78, "failed").ToArray()));
        Assert.Equal(28, error.ErrorCode);
        Assert.StartsWith("ModifyOutputContextRequest error 28 failed", MediaRemote.Describe(error).Single());
    }
}

public class RemoteControlFrameTests
{
    [Fact]
    public void A_frame_has_its_size_type_command_and_sequence_number_big_endian()
    {
        var frame = RemoteControlSession.Frame("sync", "comm", 0x0102030405060708, [0xAA]);

        Assert.Equal(33, frame.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 33 }, frame[..4]);
        Assert.Equal("sync", Encoding.ASCII.GetString(frame, 4, 4));
        Assert.All(frame[8..16], b => Assert.Equal(0, b));
        Assert.Equal("comm", Encoding.ASCII.GetString(frame, 16, 4));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame[20..28]);
        Assert.All(frame[28..32], b => Assert.Equal(0, b));
        Assert.Equal(0xAA, frame[32]);

        // The reply to a request: no command, no payload.
        var reply = RemoteControlSession.Frame("rply", "\0\0\0\0", 7, []);
        Assert.Equal(32, reply.Length);
        Assert.All(reply[16..20], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Splits_a_frame_into_its_messages()
    {
        var first = new ProtoWriter().Varint(1, 15).String(85, "A").ToArray();
        var second = new ProtoWriter().Varint(1, 52).String(85, "B").ToArray();
        byte[] both = [(byte)first.Length, .. first, (byte)second.Length, .. second];

        Assert.Equal([first, second], RemoteControlSession.SplitMessages(both));
        // One message without its length in front starts with its own type field (pyatv: ConfigureConnection does this).
        Assert.Equal([first], RemoteControlSession.SplitMessages(first));
        // A length that runs past the frame ends the list instead of throwing on the reader thread.
        Assert.Equal([first], RemoteControlSession.SplitMessages([(byte)first.Length, .. first, 0x40, 0x08]));
        Assert.Empty(RemoteControlSession.SplitMessages([]));
    }
}

public class RemoteCliTests
{
    [Fact]
    public void Reads_only_unless_an_output_option_is_given()
    {
        Assert.Null(RemoteCli.OutputChangeOf(["remote", "--host", "192.168.1.2", "--seconds", "5"]));

        var set = RemoteCli.OutputChangeOf(["remote", "--host", "192.168.1.2", "--set-outputs", "AAA, BBB"]);
        Assert.Equal(MediaRemote.OutputChange.Set, set!.Value.Change);
        Assert.Equal(["AAA", "BBB"], set.Value.Uids);
        Assert.Equal(MediaRemote.OutputChange.Add, RemoteCli.OutputChangeOf(["remote", "--add-outputs", "AAA"])!.Value.Change);
        Assert.Equal(MediaRemote.OutputChange.Remove, RemoteCli.OutputChangeOf(["remote", "--remove-outputs", "AAA"])!.Value.Change);
    }

    [Theory]
    [InlineData("--set-outputs")]                              // no UIDs
    [InlineData("--set-outputs", "--verbose")]                 // the next option is not a UID
    [InlineData("--set-outputs", "A", "--remove-outputs", "B")] // two changes at once
    public void Refuses_an_output_change_it_cannot_read(params string[] options) =>
        Assert.Throws<ArgumentException>(() => RemoteCli.OutputChangeOf(["remote", .. options]));
}
