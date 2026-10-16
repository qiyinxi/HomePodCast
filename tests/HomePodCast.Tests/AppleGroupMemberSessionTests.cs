using System.Collections.Concurrent;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class AppleGroupMemberSessionTests
{
    private sealed class Source : IReceiverAudioSource
    {
        public int Disposals;
        public bool Read(long ticks, Span<float> stereo) { stereo.Fill(.25f); return true; }
        public bool ReadPacket(long ticks) => true;
        public void ReadTarget(int index, Span<float> stereo) => stereo.Fill(.25f);
        public void SkipPackets(int packets) { }
        public void Dispose() => Disposals++;
    }
    private sealed class Member(FakeReceiver receiver) : IGroupMember
    {
        public string Name => "Apple";
        public RtpTarget Target => receiver.Target(ChannelMode.RightOnly);
        public int LatencyFrames => 4410;
        public int? ArrivalToRenderMs => 85;
        public double? InitialVolumeDb => -20;
        public string? LostReason { get; set; }
        public event Action<string>? Lost;
        public readonly ConcurrentQueue<double> Volumes = new();
        public Action? Disposing;
        public int Disposals;
        public void Record() { }
        public void Flush(ushort seq, uint rtp) { }
        public void SetVolumePercent(double volume) => Volumes.Enqueue(volume);
        public void Dispose() { Disposing?.Invoke(); Disposals++; }
        public void Lose(string reason) { LostReason = reason; Lost?.Invoke(reason); }
    }

    [Fact]
    public void Adapter_preserves_typed_orderly_close_even_when_lost_before_subscription()
    {
        using var receiver = new FakeReceiver();
        var member = new Member(receiver) { LostReason = EventChannel.ClosedBySpeaker };
        using var adapter = new AppleGroupMemberSession(member);
        Assert.True(adapter.Lost.IsCompleted);
        Assert.IsType<ReceiverGroupStoppedException>(adapter.LostException);
        Assert.True(ReceiverGroupFailures.RequiresUserAction(adapter.LostException));
    }

    [Fact]
    public void Adapter_owns_only_sender_and_member_and_stops_packets_before_teardown()
    {
        using var receiver = new FakeReceiver();
        var member = new Member(receiver); var source = new Source();
        var adapter = new AppleGroupMemberSession(member);
        member.Disposing = () => Assert.Null(adapter.Sender);
        adapter.Start(source, MediaClock.Now + MediaClock.FromMs(1000), 80, 25);
        Assert.Same(source, adapter.Sender!.GroupSource);
        Assert.Null(adapter.Sender.Effects);
        Assert.Equal(ChannelMode.Stereo, Assert.Single(adapter.Sender.Streams).Channels);
        Assert.Equal(25, Assert.Single(member.Volumes));
        adapter.StopSending(); adapter.StopSending(); adapter.Dispose(); adapter.Dispose();
        Assert.Equal(1, member.Disposals); Assert.Equal(0, source.Disposals);
    }

    [Fact]
    public void Coordinated_Apple_timeline_maps_channels_and_signed_trim_once()
    {
        long epoch = MediaClock.Now;
        var history = new TimelineBuffer(44100, 44100, epoch);
        var pcm = new float[44100 * 2];
        for (int i = 0; i < 44100; i++) { pcm[i * 2] = i / 44100f; pcm[i * 2 + 1] = -i / 44100f; }
        history.WriteFrames(0, pcm);
        using var reader = new ReceiverTimelineSource(history, 500, -100, 100, ChannelMode.RightOnly);
        var packet = new float[704];
        reader.Read(epoch + MediaClock.FromMs(500), packet);
        Assert.Equal(-.2f, packet[0], 4); Assert.Equal(packet[0], packet[1]);
        Assert.Equal(packet[2], packet[3]); Assert.True(packet[2] < packet[0]);
    }
}
