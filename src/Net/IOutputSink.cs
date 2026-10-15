using HomePodCast.Audio;

namespace HomePodCast.Net;

internal enum OutputKind { AirPlay2, Raop }
internal enum OutputState { Streaming, Lost, Stopped }
internal readonly record struct OutputStats(long PacketsSent, long RetransmitRequests, long Retransmitted, long RetransmitMisses);

/// <summary>
/// A connected output owns its transport and pacing. Factories retain their protocol-specific setup/teardown;
/// coordinators use this common lifetime, loss and volume contract. The single-HomePod path does not use it.
/// </summary>
internal interface IOutputSink : IDisposable
{
    OutputKind Kind { get; }
    string Id { get; }
    string Name { get; }
    int MinLatencyMs { get; }
    OutputState State { get; }
    Task<string> Lost { get; }
    string? LostReason { get; }
    OutputStats Stats { get; }
    TimelineBuffer? Timeline { get; }
    void SetVolume(double percent, bool mute = false);
}
