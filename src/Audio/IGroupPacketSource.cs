namespace HomePodCast.Audio;

/// <summary>One multi-channel packet clock; every speaker is read from that exact packet before any RTP send.</summary>
public interface IGroupPacketSource : IDisposable
{
    bool ReadPacket(long sourceTicks);
    void ReadTarget(int index, Span<float> stereo);
    void SkipPackets(int packets);
}
