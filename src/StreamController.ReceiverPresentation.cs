using HomePodCast.Net;

namespace HomePodCast;

public sealed partial class StreamController
{
    public object? AudioSessionIdentity => (object?)_receiverSession ?? _raopClient ?? (object?)_groupRunner?.Current ?? ActiveSender
        ?? (object?)_client?.PtpRealtime ?? _client?.SurroundSender;

    internal (long Sent, long Requested)? RetransmissionStats => ActiveSender is { } sender
        ? (sender.Retransmitted, sender.RetransmitRequests)
        : _client?.PtpRealtime is { } ptp ? (ptp.Retransmitted, ptp.RetransmitRequests) : null;

    public void ObserveAudioOnset(Action<long>? observer)
    {
        if (_receiverSession is { } receiver) receiver.OnsetSent = observer;
        else if (_raopClient is { } raop) raop.OnsetSent = observer;
        else if (ActiveSender is { } realtime) realtime.OnsetSent = observer;
        else if (_client?.PtpRealtime is { } ptp) ptp.OnsetSent = observer;
    }
}
