using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// The MediaRemote protocol (MRP) messages an Apple TV exchanges over the AirPlay 2 remote-control channel
/// (<see cref="RemoteControlSession"/>): who we are, which updates we want, and the request that changes the Apple TV's
/// current audio outputs, which is how the Control Centre of an iPhone moves its sound from the home-theater HomePods
/// to the TV and back. Message and field numbers are those of pyatv's protobuf definitions (pyatv/protocols/mrp/protobuf).
/// </summary>
internal static class MediaRemote
{
    // ProtocolMessage.Type
    public const int SetState = 4, DeviceInfo = 15, ClientUpdatesConfig = 16, VolumeControlAvailability = 17,
        DeviceInfoUpdate = 37, SetConnectionState = 38, Generic = 42, ModifyOutputContextRequest = 48, GetVolume = 49,
        GetVolumeResult = 50, SetVolume = 51, VolumeDidChange = 52, UpdateOutputDevice = 65, RemoveOutputDevices = 66;

    // ProtocolMessage's own fields; every other field is the one inner message (an extension).
    private const int TypeField = 1, IdentifierField = 2, ErrorCodeField = 4, ErrorDescriptionField = 78, UniqueIdentifierField = 85;

    // The extension field each inner message travels in.
    private const int DeviceInfoField = 20, ClientUpdatesConfigField = 21, SetConnectionStateField = 42,
        ModifyOutputContextField = 52, GetVolumeField = 53;

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "SendCommand", [2] = "SendCommandResult", [3] = "GetState", [SetState] = "SetState", [5] = "SetArtwork",
        [11] = "Notification", [12] = "ContentItemsChanged", [DeviceInfo] = "DeviceInfo", [ClientUpdatesConfig] = "ClientUpdatesConfig",
        [VolumeControlAvailability] = "VolumeControlAvailability", [23] = "Keyboard", [32] = "PlaybackQueueRequest",
        [33] = "Transaction", [34] = "CryptoPairing", [36] = "SetReadyState", [DeviceInfoUpdate] = "DeviceInfoUpdate",
        [SetConnectionState] = "SetConnectionState", [41] = "WakeDevice", [Generic] = "Generic", [46] = "SetNowPlayingClient",
        [47] = "SetNowPlayingPlayer", [ModifyOutputContextRequest] = "ModifyOutputContextRequest", [GetVolume] = "GetVolume",
        [GetVolumeResult] = "GetVolumeResult", [SetVolume] = "SetVolume", [VolumeDidChange] = "VolumeDidChange",
        [53] = "RemoveClient", [54] = "RemovePlayer", [55] = "UpdateClient", [56] = "UpdateContentItem",
        [57] = "UpdateContentItemArtwork", [58] = "UpdatePlayer", [62] = "GetVolumeControlCapabilities",
        [63] = "GetVolumeControlCapabilitiesResult", [64] = "VolumeControlCapabilitiesDidChange",
        [UpdateOutputDevice] = "UpdateOutputDevice", [RemoveOutputDevices] = "RemoveOutputDevices", [69] = "RemoveOutputDevices2",
        [72] = "SetDefaultSupportedCommands", [77] = "UpdateActiveSystemEndpoint", [101] = "SetDiscoveryMode",
        [102] = "UpdateEndPoints", [103] = "RemoveEndpoints", [104] = "PlayerClientProperties", [105] = "OriginClientProperties",
        [106] = "AudioFade", [107] = "AudioFadeResponse", [120] = "ConfigureConnection",
    };

    public static string TypeName(int type) => Names.TryGetValue(type, out var name) ? name : $"type {type}";

    /// <summary>A received ProtocolMessage: its type, the identifier a reply repeats, the error, and the inner message.</summary>
    public sealed record Received(int Type, string? Identifier, int ErrorCode, string? ErrorDescription, ProtoMessage Inner, ProtoMessage Whole)
    {
        public static Received From(ProtoMessage message)
        {
            var inner = message.Fields.FirstOrDefault(f => f.WireType == ProtoField.LengthType &&
                f.Number is not (IdentifierField or 3 or ErrorDescriptionField or UniqueIdentifierField));
            return new Received((int)(message.Varint(TypeField) ?? 0), message.String(IdentifierField),
                (int)(message.Varint(ErrorCodeField) ?? 0), message.String(ErrorDescriptionField),
                inner.Data != null && ProtoMessage.TryParse(inner.Data, out var parsed) ? parsed : ProtoMessage.Empty, message);
        }
    }

    /// <summary>The frame every message has: type, the identifier to answer with, error 0, the inner message, a UUID of its own.</summary>
    private static byte[] Message(int type, string? identifier, int innerField = 0, ProtoWriter? inner = null)
    {
        var message = new ProtoWriter().Varint(TypeField, (ulong)type);
        if (identifier != null) message.String(IdentifierField, identifier);
        message.Varint(ErrorCodeField, 0);
        if (inner != null) message.Message(innerField, inner);
        return message.String(UniqueIdentifierField, NewId()).ToArray();
    }

    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>
    /// Must be the first message: the Apple TV answers nothing before it. The values are the ones pyatv sends (the
    /// iPhone's Remote app); <paramref name="uniqueIdentifier"/> is ours.
    /// </summary>
    public static byte[] DeviceInfoMessage(string identifier, string uniqueIdentifier, string name) =>
        Message(DeviceInfo, identifier, DeviceInfoField, new ProtoWriter()
            .String(1, uniqueIdentifier)
            .String(2, name)
            .String(3, "iPhone")                 // localizedModelName
            .String(4, "18G82")                  // systemBuildVersion
            .String(5, "com.apple.TVRemote")     // applicationBundleIdentifier
            .String(6, "344.28")                 // applicationBundleVersion
            .Varint(7, 1)                        // protocolVersion
            .Varint(8, 108)                      // lastSupportedMessageType
            .Bool(9, true)                       // supportsSystemPairing
            .Bool(10, true)                      // allowsPairing
            .String(12, "com.apple.TVMusic")     // systemMediaApplication
            .Bool(13, true)                      // supportsACL
            .Bool(14, true)                      // supportsSharedQueue
            .Bool(15, true)                      // supportsExtendedMotion
            .Varint(17, 2)                       // sharedQueueVersion
            .Varint(21, 1)                       // deviceClass: iPhone
            .Varint(22, 1));                     // logicalDeviceCount

    public static byte[] SetConnectionStateMessage() =>
        Message(SetConnectionState, null, SetConnectionStateField, new ProtoWriter().Varint(1, 2)); // Connected

    /// <summary>Which updates the Apple TV pushes to us. No artwork and no keyboard: only what this app reads.</summary>
    public static byte[] ClientUpdatesConfigMessage(string identifier, bool nowPlaying) =>
        Message(ClientUpdatesConfig, identifier, ClientUpdatesConfigField, new ProtoWriter()
            .Bool(1, false).Bool(2, nowPlaying).Bool(3, true).Bool(4, false).Bool(5, true));

    public enum OutputChange { Add, Remove, Set }

    /// <summary>
    /// Changes the Apple TV's current audio outputs (pyatv's add_output_devices / remove_output_devices /
    /// set_output_devices): the "cluster aware" lists, where a stereo pair or a home-theater set is one device.
    /// </summary>
    public static byte[] ModifyOutputContextMessage(OutputChange change, IEnumerable<string> deviceUids)
    {
        var inner = new ProtoWriter().Varint(1, 1); // SharedAudioPresentation
        int field = change switch { OutputChange.Add => 5, OutputChange.Remove => 6, _ => 7 };
        foreach (var uid in deviceUids) inner.String(field, uid);
        return Message(ModifyOutputContextRequest, null, ModifyOutputContextField, inner);
    }

    public static byte[] GetVolumeMessage(string identifier, string outputDeviceUid) =>
        Message(GetVolume, identifier, GetVolumeField, new ProtoWriter().String(1, outputDeviceUid));

    /// <summary>The keep-alive pyatv sends on a MediaRemote connection: an empty message that is answered.</summary>
    public static byte[] GenericMessage(string identifier) => Message(Generic, identifier);

    /// <summary>What a DeviceInfoMessage says about the Apple TV and the devices grouped with it.</summary>
    public sealed record Device(string Name, string? UniqueIdentifier, string? DeviceUid, string? ModelId, string? ClusterId,
        int? ClusterType, string? GroupUid, string? GroupName, string? AirPlayGroupId, string? RoutingContextId,
        bool IsGroupLeader, bool IsProxyGroupPlayer, bool IsAirPlayActive, IReadOnlyList<string> AirPlayReceivers,
        IReadOnlyList<Device> Grouped, IReadOnlyList<Device> Clustered)
    {
        /// <summary>What an output change names this device by (pyatv: clusterID, else deviceUID).</summary>
        public string? OutputUid => !string.IsNullOrEmpty(ClusterId) ? ClusterId : DeviceUid;

        public static Device From(ProtoMessage m) => new(m.String(2) ?? "", m.String(1), m.String(19), m.String(39), m.String(35),
            (int?)m.Varint(37), m.String(26), m.String(27), m.String(42), m.String(41), m.Bool(29), m.Bool(24), m.Bool(30),
            m.Strings(33).ToList(), m.Messages(28).Select(From).ToList(), m.Messages(44).Select(From).ToList());

        public IEnumerable<string> Lines(string indent = "")
        {
            yield return $"{indent}{Log.Name(Name)} model={ModelId} deviceUID={DeviceUid} cluster={ClusterId} (type {ClusterType}) " +
                         $"group={GroupUid} \"{GroupName}\" airPlayGroup={AirPlayGroupId} routingContext={RoutingContextId} " +
                         $"leader={IsGroupLeader} proxy={IsProxyGroupPlayer} airplayActive={IsAirPlayActive}" +
                         (AirPlayReceivers.Count > 0 ? $" receivers=[{string.Join(", ", AirPlayReceivers)}]" : "");
            foreach (var device in Grouped)
                foreach (var line in device.Lines(indent + "  grouped: ")) yield return line;
            foreach (var device in Clustered)
                foreach (var line in device.Lines(indent + "  clustered: ")) yield return line;
        }
    }

    /// <summary>One AVOutputDeviceDescriptor of an UpdateOutputDevice message.</summary>
    public sealed record OutputDevice(string Name, string? Uid, string? GroupId, string? ModelId, int? DeviceType, int? SubType,
        bool IsLocal, float? Volume, bool ReceiverSessionActive, string? ClusterId, int? ClusterType, string? PrimaryUid,
        IReadOnlyList<OutputDevice> Members)
    {
        public static OutputDevice From(ProtoMessage m) => new(m.String(1) ?? "", m.String(2), m.String(3), m.String(4),
            (int?)m.Varint(10), (int?)m.Varint(11), m.Bool(14), m.Float(24), m.Bool(33), m.String(58), (int?)m.Varint(42),
            m.String(43), m.Messages(41).Concat(m.Messages(55)).Concat(m.Messages(64)).Select(From).ToList());

        public IEnumerable<string> Lines(string indent = "")
        {
            yield return $"{indent}{Log.Name(Name)} uid={Uid} model={ModelId} type={DeviceType}/{SubType} local={IsLocal} " +
                         $"volume={Percent(Volume)} sessionActive={ReceiverSessionActive} group={GroupId} " +
                         $"cluster={ClusterId} (type {ClusterType}) primary={PrimaryUid}";
            foreach (var member in Members)
                foreach (var line in member.Lines(indent + "  member: ")) yield return line;
        }
    }

    /// <summary>A volume of 0…1 as the log writes it ("42 %"); "?" when the message has none.</summary>
    private static string Percent(float? volume) =>
        volume is { } v ? (v * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %" : "?";

    /// <summary>The lines the log gets for a received message: what this app understands of it, else its fields by number.</summary>
    public static IEnumerable<string> Describe(Received message)
    {
        string head = TypeName(message.Type) + (message.ErrorCode != 0 ? $" error {message.ErrorCode} {message.ErrorDescription}" : "");
        switch (message.Type)
        {
            case DeviceInfo or DeviceInfoUpdate:
                yield return head + ":";
                foreach (var line in Device.From(message.Inner).Lines("  ")) yield return line;
                break;
            case UpdateOutputDevice:
                yield return $"{head}: endpoint={message.Inner.String(2)}";
                foreach (var device in message.Inner.Messages(1))
                    foreach (var line in OutputDevice.From(device).Lines("  output: ")) yield return line;
                foreach (var device in message.Inner.Messages(3))
                    foreach (var line in OutputDevice.From(device).Lines("  cluster-aware output: ")) yield return line;
                break;
            case VolumeDidChange:
                yield return $"{head}: volume={Percent(message.Inner.Float(1))} endpoint={message.Inner.String(2)} output={message.Inner.String(3)}";
                break;
            case GetVolumeResult:
                yield return $"{head}: volume={Percent(message.Inner.Float(1))}";
                break;
            case SetState:
                var path = message.Inner.Message(9);
                yield return $"{head}: playbackState={message.Inner.Varint(6)} client={path?.Message(2)?.String(2)} " +
                             $"player={path?.Message(3)?.String(1)} origin={path?.Message(1)?.String(2)}";
                break;
            default:
                string fields = message.Inner.Describe();
                yield return $"{head}: {(fields.Length > 400 && !Log.Verbose ? fields[..400] + "…" : fields)}";
                break;
        }
    }
}
