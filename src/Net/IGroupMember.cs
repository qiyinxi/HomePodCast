namespace HomePodCast.Net;

/// <summary>
/// One speaker's session as a SpeakerGroup drives it (AirPlayClient.PrepareAsync in the app, fakes in
/// tests): pairing, keys, SSRC, NTP timing, event channel and /feedback are all its own, but it has no
/// sender of its own.
/// </summary>
internal interface IGroupMember : IDisposable
{
    string Name { get; }
    RtpTarget Target { get; }
    int LatencyFrames { get; }
    int? ArrivalToRenderMs { get; }
    double? InitialVolumeDb { get; }

    /// <summary>Why the session ended, if it already has (Lost may have fired before anyone listened).</summary>
    string? LostReason { get; }
    event Action<string>? Lost;
    void Record();
    void Flush(ushort firstSeq, uint rtpBase);
    void SetVolumePercent(double percent);
}
