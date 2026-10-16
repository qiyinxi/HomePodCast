using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

public sealed partial class AirPlayClient
{
    private int _coordinatedStarted;
    internal void StartCoordinatedNtp(StreamOptions options, IGroupPacketSource source, long startAt)
    {
        if (StreamTarget == null || _sender != null) throw new InvalidOperationException("No prepared realtime stream");
        if (Interlocked.Exchange(ref _coordinatedStarted, 1) != 0) throw new InvalidOperationException("Coordinated stream already started");
        var target = StreamTarget;
        // A history reader owns no capture FIFO; the legacy sender's stall discard stays private to this leg.
        _sender = new RtpSender(target.Control, target.Remote, target.DataPort, target.ControlPort, target.StreamKey,
            target.Ssrc, LatencyFrames, new AudioFifo(44100, 0, 0)) { GroupSource = source, LatencyInSync = options.LatencyInSync };
        SetVolumePercent(options.VolumePercent ?? 0);
        Record();
        Flush(_sender.FirstSeq, _sender.RtpBase);
        _sender.Start(startAt);
        StartFeedback();
    }

    internal void StartCoordinatedPtp(StreamOptions options, ReceiverTimelineSource source, long startAt, bool realtime)
        => StartCoordinatedPtp(options, source.Read, startAt, realtime);

    internal void StartCoordinatedPtp(StreamOptions options, ReceiverPcmReader source, long startAt, bool realtime)
    {
        if (_ptp == null || realtime && PtpRealtime == null || !realtime && _buffered == null)
            throw new InvalidOperationException("No prepared PTP stream");
        if (Interlocked.Exchange(ref _coordinatedStarted, 1) != 0) throw new InvalidOperationException("Coordinated stream already started");
        SetVolumePercent(options.VolumePercent ?? 0);
        Record();
        if (realtime)
        {
            Flush(PtpRealtime!.FirstSeq, PtpRealtime.RtpBase);
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
            _buffered.Start(startAt, source);
        }
        StartFeedback();
    }

    internal void StopCoordinatedSending()
    {
        _sender?.Dispose();
        PtpRealtime?.StopSending();
        _buffered?.StopSending();
    }
}
