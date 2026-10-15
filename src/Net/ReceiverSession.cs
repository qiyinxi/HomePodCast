using HomePodCast.Audio;

namespace HomePodCast.Net;

/// <summary>A non-Apple session, independent of the original HomePod controller path.</summary>
public interface IReceiverSession : IDisposable
{
    ReceiverProtocol Protocol { get; }
    IReadOnlyDictionary<string, object?> Info { get; }
    int LatencyMs { get; }
    int? ArrivalToRenderMs { get; }
    double? InitialVolumeDb { get; }
    double VolumeCapPercent { get; set; }
    long PacketsSent { get; }
    string? LostReason { get; }
    Action<long>? OnsetSent { get; set; }
    event Action<string>? Lost;
    void SetVolume(double percent);
}

internal sealed class RealtimeReceiverSession(AirPlayClient client) : IReceiverSession
{
    public ReceiverProtocol Protocol => ReceiverProtocol.AirPlay2Realtime;
    public IReadOnlyDictionary<string, object?> Info => client.Info;
    public int LatencyMs => client.LatencyFrames * 1000 / RtpSender.SampleRate;
    public int? ArrivalToRenderMs => client.ArrivalToRenderMs;
    public double? InitialVolumeDb => client.InitialVolumeDb;
    public double VolumeCapPercent { get => client.VolumeCapPercent; set => client.VolumeCapPercent = value; }
    public long PacketsSent => client.Sender?.PacketsSent ?? client.PtpRealtime?.PacketsSent ?? 0;
    public string? LostReason => client.LostReason;
    public Action<long>? OnsetSent { get => client.Sender?.OnsetSent ?? client.PtpRealtime?.OnsetSent; set { if (client.Sender is { } sender) sender.OnsetSent = value; else if (client.PtpRealtime is { } ptp) ptp.OnsetSent = value; } }
    public event Action<string>? Lost { add => client.Lost += value; remove => client.Lost -= value; }
    public void SetVolume(double percent) => client.SetVolumePercent(percent);
    public void Dispose() => client.Dispose();
}

internal sealed class LegacyReceiverSession(RaopOutputSink output, AirPlayDevice device) : IReceiverSession
{
    private RaopClient client => output.Client;
    public ReceiverProtocol Protocol => ReceiverProtocol.Raop;
    public IReadOnlyDictionary<string, object?> Info { get; } = new Dictionary<string, object?> { ["name"] = device.Name, ["model"] = device.Model };
    public int LatencyMs => client.LatencyFrames * 1000 / HomePodCast.Protocol.RaopAudio.SampleRate;
    public int? ArrivalToRenderMs => null;
    public double? InitialVolumeDb => null;
    public double VolumeCapPercent { get => client.VolumeCapPercent; set => client.VolumeCapPercent = value; }
    public long PacketsSent => client.PacketsSent;
    public string? LostReason => client.LostReason;
    public Action<long>? OnsetSent { get => client.OnsetSent; set => client.OnsetSent = value; }
    public event Action<string>? Lost { add => client.Lost += value; remove => client.Lost -= value; }
    public void SetVolume(double percent) => output.SetVolume(percent);
    public void Dispose() => output.Dispose();
}

public sealed record ReceiverConnectOptions(int LatencyMs, double? VolumePercent, double VolumeCapPercent,
    PairingStore? Pairings, string? Password, IAudioEffect Effects, Action<string>? Log = null)
{
    internal PtpPorts? TimingPorts { get; init; }
}
