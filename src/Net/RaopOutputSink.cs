using System.Net;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>Classic RAOP behind the output contract; its existing setup, timing, encoding and loss rules are retained.</summary>
internal sealed class RaopOutputSink : IOutputSink
{
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private RaopOutputSink(RaopClient client, TimelineBuffer timeline, string id, string name)
    {
        Client = client;
        Timeline = timeline;
        Id = id;
        Name = name;
        // RaopClient replays an already signalled loss to a late subscriber.
        client.Lost += OnLost;
    }

    public RaopClient Client { get; }
    public OutputKind Kind => OutputKind.Raop;
    public string Id { get; }
    public string Name { get; }
    public int MinLatencyMs => Client.LatencyFrames * 1000 / RaopAudio.SampleRate;
    public OutputState State => Volatile.Read(ref _disposed) != 0 ? OutputState.Stopped
        : _lost.Task.IsCompleted ? OutputState.Lost : OutputState.Streaming;
    public Task<string> Lost => _lost.Task;
    public string? LostReason => Client.LostReason;
    public OutputStats Stats => new(Client.PacketsSent, Client.RetransmitRequests, Client.Retransmitted, Client.RetransmitMisses);
    public TimelineBuffer Timeline { get; }

    public static async Task<RaopOutputSink> ConnectAsync(IPAddress host, int port, RaopOptions options, CancellationToken ct,
        string? id = null, string? name = null)
    {
        var timeline = new TimelineBuffer(RaopAudio.SampleRate, RaopAudio.SampleRate * 5, MediaClock.Now);
        var history = new PacketHistory(timeline);
        var client = await RaopClient.ConnectAsync(host, port, options with
        {
            ReadTimedAudio = (ticks, pcm) =>
            {
                if (options.ReadTimedAudio is { } read) read(ticks, pcm);
                else options.ReadAudio?.Invoke(pcm);
                history.Write(ticks, pcm);
            },
        }, ct);
        return new RaopOutputSink(client, timeline, id ?? $"{host}:{port}", name ?? host.ToString());
    }

    internal sealed class PacketHistory(TimelineBuffer timeline)
    {
        private readonly float[] _audio = new float[RaopAudio.FramesPerPacket * 2];
        private long? _nextFrame;

        public void Write(long ticks, ReadOnlySpan<short> pcm)
        {
            for (int i = 0; i < pcm.Length; i++) _audio[i] = pcm[i] / 32768f;
            // A truncated QPC packet deadline can round one frame backwards relative to the previous packet's end.
            // RAOP's RTP clock advances by whole packets, so anchor the history once and retain that same cadence.
            long frame = _nextFrame ?? timeline.FrameAt(ticks);
            timeline.WriteFrames(frame, _audio.AsSpan(0, pcm.Length));
            _nextFrame = checked(frame + pcm.Length / 2);
        }
    }

    private void OnLost(string reason) => _lost.TrySetResult(reason);
    public void SetVolume(double percent, bool mute = false) => Client.SetVolume(percent, mute);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Client.Lost -= OnLost;
        Client.Dispose();
    }
}
