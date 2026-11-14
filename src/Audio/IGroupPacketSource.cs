namespace HomePodCast.Audio;

/// <summary>One multi-channel packet clock; every speaker is read from that exact packet before any RTP send.</summary>
public interface IGroupPacketSource : IDisposable
{
    bool ReadPacket(long sourceTicks);
    void ReadTarget(int index, Span<float> stereo);
    void SkipPackets(int packets);
}

/// <summary>Optional, explicit microphone calibration controls for a live multi-output timeline.</summary>
internal interface IGroupAcousticCalibration
{
    bool CanCalibrateAcoustically { get; }
    long BeginAcousticPulse(string targetId);
    bool RenderCalibrationPulse(string targetId, long firstFrameTicks, int sampleRate, int trimMs, Span<float> stereo);
    void SetAcousticCalibrationActive(bool active);
    void EndAcousticCalibration();
}
