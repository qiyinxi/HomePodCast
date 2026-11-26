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
        TimeSpan? sinceConnect = null, TimeSpan? sinceSound = null, bool soundNow = false) =>
        AppleTvIdlePolicy.Decide(seconds, appleTv, streaming, released, sinceConnect ?? Long, sinceSound ?? Long, soundNow);

    // ---------------------------------------------------------------- releasing

    [Fact]
    public void A_silent_stream_to_an_Apple_TV_is_released()
    {
        Assert.Equal(AppleTvIdleAction.Release, Decide());
        Assert.Equal(AppleTvIdleAction.Release, Decide(sinceConnect: TimeSpan.FromSeconds(60), sinceSound: TimeSpan.FromSeconds(60)));
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
        Assert.Equal(AppleTvIdleAction.None, Decide(sinceConnect: Short, sinceSound: TimeSpan.MaxValue));
        Assert.Equal(AppleTvIdleAction.None, Decide(sinceConnect: TimeSpan.FromSeconds(59)));
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
    public void Old_configs_without_the_setting_release_after_a_minute()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>("""{ "DeviceId": "AA:BB", "LatencyMs": 120 }""")!;
        Assert.Equal(60, cfg.AppleTvIdleReleaseSeconds);
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
