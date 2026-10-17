using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

public sealed partial class AirPlayClient
{
    private int _coordinatedStarted;
    private int _coordinatedStopped;
    private void CheckCoordinatedSending() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _coordinatedStopped) != 0, this);
    internal void StartCoordinatedNtp(StreamOptions options, IGroupPacketSource source, long startAt)
    {
        CheckCoordinatedSending();
        if (StreamTarget == null || _sender != null) throw new InvalidOperationException("No prepared realtime stream");
        if (Interlocked.Exchange(ref _coordinatedStarted, 1) != 0) throw new InvalidOperationException("Coordinated stream already started");
        var target = StreamTarget;
        // A history reader owns no capture FIFO; the legacy sender's stall discard stays private to this leg.
        _sender = new RtpSender(target.Control, target.Remote, target.DataPort, target.ControlPort, target.StreamKey,
            target.Ssrc, LatencyFrames, new AudioFifo(44100, 0, 0)) { GroupSource = source, LatencyInSync = options.LatencyInSync };
        try
        {
            CheckCoordinatedSending();
            SetVolumePercent(options.VolumePercent ?? 0);
            CheckCoordinatedSending(); Record();
            CheckCoordinatedSending(); Flush(_sender.FirstSeq, _sender.RtpBase);
            CheckCoordinatedSending(); _sender.Start(startAt);
            CheckCoordinatedSending(); StartFeedback();
        }
        catch { _sender.Dispose(); throw; }
    }

    internal void StartCoordinatedPtp(StreamOptions options, ReceiverTimelineSource source, long startAt, bool realtime)
        => StartCoordinatedPtp(options, source.Read, startAt, realtime);

    internal void StartCoordinatedPtp(StreamOptions options, ReceiverPcmReader source, long startAt, bool realtime)
    {
        CheckCoordinatedSending();
        if (_ptp == null || realtime && PtpRealtime == null || !realtime && _buffered == null)
            throw new InvalidOperationException("No prepared PTP stream");
        if (Interlocked.Exchange(ref _coordinatedStarted, 1) != 0) throw new InvalidOperationException("Coordinated stream already started");
        SetVolumePercent(options.VolumePercent ?? 0);
        CheckCoordinatedSending(); Record();
        CheckCoordinatedSending();
        if (realtime)
        {
            Flush(PtpRealtime!.FirstSeq, PtpRealtime.RtpBase);
            CheckCoordinatedSending();
            PtpRealtime.Start(startAt, source);
        }
        else
        {
            Expect(_rtsp.RtspPlist("FLUSHBUFFERED", new()
            {
                ["flushUntilSeq"] = 0, ["flushUntilTS"] = _buffered!.RtpBase,
            }), "FLUSHBUFFERED");
            ulong presentation = PtpMaster.NanosecondsAt(checked(startAt
                + (long)((Int128)LatencyFrames * MediaClock.Frequency / BufferedAudio.SampleRate)));
            Expect(_rtsp.RtspPlist("SETRATEANCHORTIME", BufferedAudio.Anchor(_ptp.ClockId, presentation, _buffered.RtpBase)), "SETRATEANCHORTIME");
            CheckCoordinatedSending();
            _buffered.Start(startAt, source);
        }
        CheckCoordinatedSending(); StartFeedback();
    }

    internal void StopCoordinatedSending()
    {
        Volatile.Write(ref _coordinatedStopped, 1);
        _sender?.Dispose();
        PtpRealtime?.StopSending();
        _buffered?.StopSending();
    }
}
