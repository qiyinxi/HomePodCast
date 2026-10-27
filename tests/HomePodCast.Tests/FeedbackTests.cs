using System.Diagnostics;
using System.Net;
using System.Reflection;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>The /feedback heartbeat of an AirPlay 2 session, against <see cref="FakeAirPlayReceiver"/> as the speaker.</summary>
public class FeedbackTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static Task<AirPlayClient> Prepare(FakeAirPlayReceiver fake, CancellationToken ct) =>
        AirPlayClient.PrepareAsync(IPAddress.Loopback, fake.Port, new StreamOptions(250, 0), ChannelMode.Stereo, ct);

    private static int Feedbacks(FakeAirPlayReceiver fake) => fake.Requests.Count(r => r.StartsWith("POST /feedback"));

    private static async Task Until(Func<bool> condition, Func<string> what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > Guard) throw new TimeoutException(what());
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_tick_while_a_heartbeat_waits_for_its_answer_is_skipped_and_is_no_failure()
    {
        // A periodic timer runs its callbacks side by side: with a speaker that stopped answering, each 2 s tick queued
        // another request on the RTSP lock behind one waiting its 5 s receive timeout, and the TEARDOWN behind them all.
        int asked = 0; // heartbeats that reached the speaker (it records a request only once it is answered)
        using var answer = new ManualResetEventSlim();
        using var fake = new FakeAirPlayReceiver(false)
        {
            RefuseRequest = request =>
            {
                if (!request.StartLine.StartsWith("POST /feedback")) return null;
                Interlocked.Increment(ref asked);
                answer.Wait(Guard);
                return null;
            },
        };
        using var guard = new CancellationTokenSource(Guard);
        using var client = await Prepare(fake, guard.Token); // its first heartbeat goes out at once and waits
        await Until(() => Volatile.Read(ref asked) == 1, () => $"the first heartbeat: {string.Join(", ", fake.Requests)}");

        var took = Stopwatch.StartNew();
        client.Feedback();
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(1), $"the second tick waited {took.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(1, Volatile.Read(ref asked));

        answer.Set();
        await Until(() => Feedbacks(fake) == 1, () => "the first heartbeat's answer");
        // Once that answer is in, a tick sends its own heartbeat again (these calls, or the timer's next tick).
        await Until(() => { client.Feedback(); return Feedbacks(fake) >= 2; }, () => "a heartbeat after the first one");
        Assert.Null(client.LostReason);
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task A_session_lost_to_its_heartbeats_ends_without_a_TEARDOWN()
    {
        using var fake = new FakeAirPlayReceiver(false)
        { RefuseRequest = request => request.StartLine.StartsWith("POST /feedback") ? 503 : null };
        using var guard = new CancellationTokenSource(Guard);
        var client = await Prepare(fake, guard.Token);
        try
        {
            // The timer's ticks and these calls take turns; three failures in a row end the session.
            await Until(() => { client.Feedback(); return client.LostReason != null; }, () => "the session to be lost");
        }
        finally
        {
            client.Dispose();
        }
        Assert.DoesNotContain("TEARDOWN [encrypted]", fake.Requests);
    }

    [Fact]
    public async Task One_failed_heartbeat_and_then_a_stop_still_sends_the_TEARDOWN()
    {
        int refused = 0;
        using var fake = new FakeAirPlayReceiver(false)
        {
            RefuseRequest = request =>
                request.StartLine.StartsWith("POST /feedback") && Interlocked.Increment(ref refused) == 1 ? 503 : null,
        };
        using var guard = new CancellationTokenSource(Guard);
        var client = await Prepare(fake, guard.Token);
        await Until(() => Volatile.Read(ref refused) >= 1, () => "the first heartbeat");
        client.Dispose();
        Assert.Null(client.LostReason);
        Assert.Contains("TEARDOWN [encrypted]", fake.Requests);
    }

    private sealed class Silence : IGroupPacketSource
    {
        public bool ReadPacket(long sourceTicks) => true;
        public void ReadTarget(int index, Span<float> stereo) => stereo.Clear();
        public void SkipPackets(int packets) { }
        public void Dispose() { }
    }

    private static object? FeedbackTimer(AirPlayClient client) =>
        typeof(AirPlayClient).GetField("_feedbackTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Starting_a_prepared_stream_keeps_the_heartbeat_the_preparation_started(bool coordinated)
    {
        // PrepareAsync starts the heartbeat, and the third-party paths start it again with the stream: a second timer
        // doubled the requests and the failures, and kept running after Dispose, which disposed only the second one.
        using var fake = new FakeAirPlayReceiver(false);
        using var guard = new CancellationTokenSource(Guard);
        var options = new StreamOptions(250, 0);
        using var client = await Prepare(fake, guard.Token);
        var timer = FeedbackTimer(client);
        Assert.NotNull(timer);

        if (coordinated) client.StartCoordinatedNtp(options, new Silence(), MediaClock.Now + MediaClock.FromMs(250));
        else client.StartPreparedStream(options, new AudioFifo(44100, 0, 0));

        Assert.Single(fake.Requests, r => r == "RECORD [encrypted]");
        Assert.Same(timer, FeedbackTimer(client));
        Assert.Empty(fake.Errors);
    }
}
