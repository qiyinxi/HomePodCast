using System.Text.Json;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// The Apple TV idle release: when a silent stream to an Apple TV is ended (its home-theater HomePods ignore "Hey Siri"
/// while the session is up), when it connects again, the setting, and the status line it leaves.
/// </summary>
public class AppleTvIdleTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10), Short = TimeSpan.FromSeconds(5);

    private static AppleTvIdleAction Decide(int seconds = 60, bool appleTv = true, bool streaming = true, bool released = false,
        TimeSpan? streamingFor = null, TimeSpan? sinceSound = null, bool soundNow = false) =>
        AppleTvIdlePolicy.Decide(seconds, appleTv, streaming, released, streamingFor ?? Long, sinceSound ?? Long, soundNow);

    // ---------------------------------------------------------------- releasing

    [Fact]
    public void A_silent_stream_to_an_Apple_TV_is_released()
    {
        Assert.Equal(AppleTvIdleAction.Release, Decide());
        Assert.Equal(AppleTvIdleAction.Release, Decide(streamingFor: TimeSpan.FromSeconds(60), sinceSound: TimeSpan.FromSeconds(60)));
        Assert.Equal(AppleTvIdleAction.Release, Decide(sinceSound: TimeSpan.MaxValue)); // nothing played in this run
    }

    [Fact]
    public void Sound_within_the_time_keeps_the_stream()
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(sinceSound: Short));
        Assert.Equal(AppleTvIdleAction.None, Decide(sinceSound: TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void A_new_session_gets_the_whole_time_before_it_counts_as_silent()
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(streamingFor: Short, sinceSound: TimeSpan.MaxValue));
        Assert.Equal(AppleTvIdleAction.None, Decide(streamingFor: TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void Only_a_stream_that_is_up_is_released()
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(streaming: false)); // connecting, waiting for a busy Apple TV, retrying
    }

    [Fact]
    public void Other_speakers_are_never_released()
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(appleTv: false)); // a HomePod's own session leaves Siri alone
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Turned_off_it_never_releases(int seconds)
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(seconds));
    }

    // ---------------------------------------------------------------- connecting again

    [Fact]
    public void Released_it_waits_for_sound()
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(streaming: false, released: true));
        Assert.Equal(AppleTvIdleAction.Reconnect, Decide(streaming: false, released: true, soundNow: true));
    }

    [Fact]
    public void Released_and_then_turned_off_it_connects_again()
    {
        Assert.Equal(AppleTvIdleAction.Reconnect, Decide(seconds: 0, streaming: false, released: true));
    }

    [Fact]
    public void Sound_alone_never_connects_what_it_did_not_release()
    {
        Assert.Equal(AppleTvIdleAction.None, Decide(streaming: false, released: false, soundNow: true)); // the user's own 断开
    }

    // ---------------------------------------------------------------- the setting

    [Fact]
    public void It_is_off_unless_turned_on()
    {
        Assert.False(new AppConfig().AppleTvIdleRelease);
        Assert.Equal(0, AppleTvIdlePolicy.SecondsFor(new AppConfig()));
        var old = JsonSerializer.Deserialize<AppConfig>("""{ "DeviceId": "AA:BB", "LatencyMs": 120 }""")!;
        Assert.Equal(0, AppleTvIdlePolicy.SecondsFor(old));
        // 1.5.0 beta 4 released for everyone and saved its 60 seconds: that config stays off too.
        var beta4 = JsonSerializer.Deserialize<AppConfig>("""{ "DeviceId": "AA:BB", "AppleTvIdleReleaseSeconds": 60 }""")!;
        Assert.Equal(0, AppleTvIdlePolicy.SecondsFor(beta4));
    }

    [Fact]
    public void Turned_on_it_uses_the_time_of_the_config()
    {
        Assert.Equal(60, AppleTvIdlePolicy.SecondsFor(new AppConfig { AppleTvIdleRelease = true }));
        Assert.Equal(30, AppleTvIdlePolicy.SecondsFor(new AppConfig { AppleTvIdleRelease = true, AppleTvIdleReleaseSeconds = 30 }));
        Assert.Equal(0, AppleTvIdlePolicy.SecondsFor(new AppConfig { AppleTvIdleRelease = true, AppleTvIdleReleaseSeconds = -5 }));
    }

    [Fact]
    public void The_switch_is_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-atvidle-{Guid.NewGuid():N}", "config.json");
        try
        {
            new AppConfig { AppleTvIdleRelease = true }.Save(path);
            Assert.True(AppConfig.Load(path).AppleTvIdleRelease);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ---------------------------------------------------------------- what it sees of the stream

    [Fact]
    public void The_time_counts_from_when_the_stream_came_up()
    {
        var watch = new AppleTvIdleWatch();
        Assert.Equal(TimeSpan.Zero, watch.Observe(1_000, streaming: false, idle: true));
        Assert.Equal(TimeSpan.Zero, watch.Observe(70_000, streaming: false, idle: false)); // connecting, a minute after the app started
        Assert.Equal(TimeSpan.FromMilliseconds(250), watch.Observe(70_250, streaming: true, idle: false));
        Assert.Equal(TimeSpan.FromSeconds(60), watch.Observe(130_000, streaming: true, idle: false));
    }

    [Fact]
    public void A_stream_already_up_at_the_first_check_starts_its_time_there()
    {
        var watch = new AppleTvIdleWatch();
        Assert.Equal(TimeSpan.Zero, watch.Observe(5_000, streaming: true, idle: false));
        Assert.Equal(TimeSpan.FromSeconds(1), watch.Observe(6_000, streaming: true, idle: false));
    }

    [Fact]
    public void A_stream_that_came_up_again_gets_the_whole_time_again()
    {
        // 2026-11-26: the session ended by itself and the maintainer connected again through a path that did not tell the
        // idle release; counting from the connect before, it ended the new session 60 ms after it came up.
        var watch = new AppleTvIdleWatch();
        watch.Observe(0, streaming: false, idle: true);
        Assert.Equal(TimeSpan.FromSeconds(20), watch.Observe(20_000, streaming: true, idle: false));
        watch.Observe(21_000, streaming: false, idle: false); // session lost
        var up = watch.Observe(62_000, streaming: true, idle: false);
        Assert.Equal(TimeSpan.FromSeconds(41), up); // since the last check that saw it down
        Assert.Equal(AppleTvIdleAction.None, AppleTvIdlePolicy.Decide(60, true, true, watch.Released, up, TimeSpan.MaxValue, false));
    }

    [Fact]
    public void Released_stays_released_while_its_stream_stops_and_while_nothing_is_connected()
    {
        var watch = new AppleTvIdleWatch();
        watch.Observe(0, streaming: true, idle: false);
        watch.Release();
        watch.Observe(250, streaming: false, idle: false); // the stop it asked for is still under way
        Assert.True(watch.Released);
        watch.Observe(500, streaming: false, idle: true);
        watch.Observe(60_000, streaming: false, idle: true);
        Assert.True(watch.Released);
    }

    [Fact]
    public void A_connection_made_some_other_way_is_not_connected_again()
    {
        var watch = new AppleTvIdleWatch();
        watch.Observe(0, streaming: true, idle: false);
        watch.Release();
        watch.Observe(500, streaming: false, idle: true);
        watch.Observe(5_000, streaming: false, idle: false); // someone connects, without Reset being called
        Assert.False(watch.Released);
    }

    [Fact]
    public void A_connect_or_disconnect_of_the_user_ends_the_release()
    {
        var watch = new AppleTvIdleWatch();
        watch.Release();
        watch.Reset();
        Assert.False(watch.Released);
    }

    [Theory]
    [InlineData(0, 0)] // off stays off
    [InlineData(-5, 0)]
    [InlineData(30, 30)]
    [InlineData(1_000_000, 86_400)]
    public void A_hand_edited_time_is_kept_sensible(int written, int loaded)
    {
        var path = Path.Combine(Path.GetTempPath(), $"homepodcast-atvidle-{Guid.NewGuid():N}", "config.json");
        try
        {
            new AppConfig { AppleTvIdleReleaseSeconds = written }.Save(path);
            Assert.Equal(loaded, AppConfig.Load(path).AppleTvIdleReleaseSeconds);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ---------------------------------------------------------------- the status line

    [Fact]
    public void Releasing_shows_why_nothing_is_connected()
    {
        using var c = new StreamController();
        string status = TrayApp.AppleTvReleasedText();
        Assert.Equal(L.T("没有声音，已暂时断开 Apple TV，有声音时自动连接"), status);
        c.Stop(status);
        Assert.Equal(StreamState.Idle, c.State);
        Assert.Equal(status, c.StatusText);
    }
}
