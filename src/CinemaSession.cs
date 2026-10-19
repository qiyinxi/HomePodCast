using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>What the cinema window's playback is doing, as the window shows it.</summary>
internal enum CinemaState
{
    /// <summary>Stopped, finished, or replaced by another connection: nothing of the window's is playing.</summary>
    Ended,
    /// <summary>Its start is queued, connecting, or waiting for a busy speaker or to retry.</summary>
    Waiting,
    Playing,
}

/// <summary>
/// The wireless cinema's hold on the StreamController (experimental since 1.1, never tested on real hardware): which file
/// source the window plays and whether its queued start has reached the controller. Any other connection request (连接,
/// 断开, a profile, the night disconnect) supersedes it first (<see cref="Supersede"/>), and the controller's
/// <see cref="StreamController.GroupSource"/> tells whose stream is running; so the window never takes the user's own
/// stream for the cinema, and its Stop and closing only ever stop a controller that still plays the cinema's source
/// (review of #22: a 连接 made while the cinema waited left the window "playing", and its Stop then disconnected it).
/// Thread-safe: the UI thread calls it, the connection queue runs the starts and stops.
/// </summary>
internal sealed class CinemaSession
{
    private readonly StreamController _controller;
    private readonly LatestRequestQueue _connection;
    private readonly object _lock = new();
    private IGroupPacketSource? _source;   // the window's playback, until it stops or is superseded
    private IGroupPacketSource? _handed;   // its queued start ran: the controller has it (or is about to)
    private bool _active;                  // the controller has left Idle with it (connecting, streaming, retrying)

    public CinemaSession(StreamController controller, LatestRequestQueue connection)
    {
        _controller = controller;
        _connection = connection;
        // The controller is Idle for a moment between StartGroup publishing the source and its loop saying 正在连接…:
        // that Idle is not the end of the cinema; one after it is (another sender took a speaker, the loop gave up).
        controller.Changed += () =>
        {
            lock (_lock)
                if (_handed != null && ReferenceEquals(controller.GroupSource, _handed) && controller.State != StreamState.Idle) _active = true;
        };
    }

    /// <summary>A group of the cinema's speakers (multi-room: every speaker gets the full stereo packet its source makes).</summary>
    public static GroupPlan Plan(IReadOnlyList<CinemaSpeaker> speakers) => new(GroupKind.MultiRoom, L.T("无线影院"), null,
        speakers.Select(s => new GroupMemberRef(s.DeviceId, s.Name, s.Host)).ToArray(), false, false,
        speakers.ToDictionary(s => GroupPlan.Key(s.DeviceId), s => s.VolumeOffset));

    /// <summary>
    /// Queue the cinema's start, replacing whatever the controller plays. <paramref name="volume"/> is the window's slider;
    /// a volume set after this call, before the queued start runs, is kept instead (volumeAsOf, as 连接 does).
    /// </summary>
    public void Start(IGroupPacketSource source, IReadOnlyList<CinemaSpeaker> speakers, int latencyMs, double volume)
    {
        var plan = Plan(speakers);
        int asOf = _controller.VolumeChanges;
        IGroupPacketSource? previous;
        lock (_lock)
        {
            previous = ReferenceEquals(_source, source) ? null : _source;
            if (previous != null && !ReferenceEquals(_handed, previous)) previous.Dispose(); // its start never ran
            _source = source;
            _handed = null;
            _active = false;
        }
        _connection.Post(() =>
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_source, source)) return; // stopped or superseded before it ran
                _handed = source;
            }
            // Automatic: a speaker another device plays to is waited for, never taken over.
            _controller.StartGroup(plan, latencyMs, volume, asOf, ConnectTrigger.Automatic, source: source);
        });
    }

    /// <summary>
    /// The window stops its playback. The controller is stopped only while it still runs this source; a start still
    /// queued is dropped. Returns whether the start ever reached the controller.
    /// </summary>
    public bool Stop(IGroupPacketSource source)
    {
        bool handed;
        lock (_lock)
        {
            if (!ReferenceEquals(_source, source))
            {
                source.Dispose(); // superseded earlier: the controller let go of it already (or never had it)
                return true;
            }
            handed = ReferenceEquals(_handed, source);
            _source = _handed = null;
            _active = false;
        }
        if (!handed)
        {
            source.Dispose();
            return false;
        }
        _connection.Post(() =>
        {
            if (ReferenceEquals(_controller.GroupSource, source)) _controller.Stop(); // disposes the source
            else source.Dispose();
        });
        return true;
    }

    /// <summary>
    /// Another connection request is about to be posted: the cinema's playback ends with it. A start that reached the
    /// controller is stopped by that request (its Stop disposes the source); one still queued never runs.
    /// </summary>
    /// <returns>Whether a cinema playback was superseded.</returns>
    public bool Supersede()
    {
        lock (_lock)
        {
            if (_source is not { } source) return false;
            if (!ReferenceEquals(_handed, source)) source.Dispose();
            _source = _handed = null;
            _active = false;
            return true;
        }
    }

    /// <summary>The window's view of <paramref name="source"/>.</summary>
    public CinemaState StateOf(IGroupPacketSource source)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_source, source)) return CinemaState.Ended;
            if (!ReferenceEquals(_handed, source) || !ReferenceEquals(_controller.GroupSource, source)) return CinemaState.Waiting;
            return _controller.State switch
            {
                StreamState.Streaming => CinemaState.Playing,
                StreamState.Idle => _active ? CinemaState.Ended : CinemaState.Waiting,
                _ => CinemaState.Waiting,
            };
        }
    }

    /// <summary>Whether a cinema playback holds the controller (queued, connecting or playing).</summary>
    public bool Holding
    {
        get { lock (_lock) return _source != null; }
    }
}
