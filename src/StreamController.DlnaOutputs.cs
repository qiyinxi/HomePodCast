using System.Net;
using System.Net.Sockets;
using HomePodCast.Net;

namespace HomePodCast;

public sealed partial class StreamController
{
    /// <summary>Tests resolve only owned loopback fakes; production resolves UDN against fresh SSDP metadata.</summary>
    internal Func<string, CancellationToken, Task<DlnaConnectionTarget>>? ResolveGroupDlnaOutput { get; set; }
    internal Func<DlnaGroupOutputOptions, DlnaConnectionTarget, DlnaClientOptions, CancellationToken, Task<DlnaClient>>? ConnectGroupDlnaOutput { get; set; }
    internal Func<TimeSpan, CancellationToken, Task>? RetryGroupDlnaOutput { get; set; }
    internal IReadOnlyList<DlnaGroupOutputStatus> DlnaGroupOutputs => _groupRunner?.Current?.DlnaOutputs ?? [];

    private DlnaGroupOutputOptions DlnaGroupOptions(DlnaOutputSettings settings) => new(settings.Udn, settings.Name,
        ct => (ResolveGroupDlnaOutput ?? ResolveDlnaOutputAsync)(settings.Udn, ct))
    {
        Required = settings.Required, EstimatedLatencyMs = settings.EstimatedLatencyMs, TrimMs = settings.TrimMs,
        Channels = settings.Channels, VolumeOffset = settings.VolumeOffset,
        PreferredFormat = settings.PreferredFormat?.ToLowerInvariant() switch
        {
            "l16" => DlnaAudioFormat.L16, "wav" => DlnaAudioFormat.Wav,
            "mp3" => DlnaAudioFormat.Mp3, "aac" => DlnaAudioFormat.Aac, _ => null,
        },
        Framing = settings.Framing == "length" ? DlnaHttpFraming.LargeContentLength : DlnaHttpFraming.Chunked,
    };

    private static async Task<DlnaConnectionTarget> ResolveDlnaOutputAsync(string udn, CancellationToken ct)
    {
        var devices = await DlnaDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        var device = devices.SingleOrDefault(d => string.Equals(d.Udn, udn, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("The selected DLNA renderer was not discovered.");
        // UDP Connect sends nothing: the route chooses the explicit address for HTTP and SOAP.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(device.Address, 1900));
        var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
        return new(device, local, () => NetworkProfile.ForLocalAddress(local).Category == NetworkCategory.Private);
    }

    internal static string DlnaMessage(DlnaFailureKind kind) => kind switch
    {
        DlnaFailureKind.Busy => L.T("DLNA 设备正在被其他设备使用，请先停止该设备上的播放。"),
        DlnaFailureKind.TakenOver => L.T("DLNA 设备已被其他控制器接管。"),
        DlnaFailureKind.Unsupported => L.T("这台 DLNA 设备不支持可用的音频格式。"),
        DlnaFailureKind.Policy => L.T("DLNA 输出仅支持专用网络。"),
        DlnaFailureKind.Stopped => L.T("DLNA 设备已停止播放。"),
        _ => L.T("DLNA 输出连接已断开。"),
    };
}
