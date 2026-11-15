using System.Net;

namespace HomePodCast.Tests;

/// <summary>`stream --surround` (experimental since 1.4) talks to a real HomePod: silence only, and quietly.</summary>
public sealed class SurroundCliTests
{
    private static SurroundCliOptions? Parse(string line, out string? error) =>
        SurroundCliOptions.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), out error);

    [Fact]
    public void Silence_is_required()
    {
        Assert.Null(Parse("stream --surround --host 192.0.2.26", out var error));
        Assert.Contains("--silent", error);
    }

    [Theory]
    [InlineData("stream --surround --host 192.0.2.26 --silent --tone")]
    [InlineData("stream --surround --host 192.0.2.26 --silent --beeps 3")]
    public void Tones_are_refused(string line)
    {
        Assert.Null(Parse(line, out var error));
        Assert.Contains("refused", error);
    }

    [Theory]
    [InlineData("--cap 11")]
    [InlineData("--cap 25")]
    [InlineData("--cap 5 --volume 6")]
    [InlineData("--volume 11")]
    [InlineData("--cap -1")]
    [InlineData("--cap ten")]
    [InlineData("--cap")]
    public void Anything_louder_than_10_percent_is_refused(string extra)
    {
        Assert.Null(Parse("stream --surround --host 192.0.2.26 --silent " + extra, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_Apple_TV_experiment_takes_a_tone_at_35_percent_at_most_or_silence()
    {
        // 2026-11-11: the maintainer listens while a buffered PTP stream goes to the Apple TV whose output is its HomePods.
        var tone = Parse("stream --surround --apple-tv --host 192.0.2.110 --tone --beeps 3 --volume 20", out var error)!;
        Assert.Null(error);
        Assert.True(tone.AppleTv);
        Assert.Equal((3, 35.0, 20.0), (tone.Beeps, tone.CapPercent, tone.VolumePercent)); // 35 % since 2026-11-12 (AGENTS.md)
        Assert.Equal(1, Parse("stream --surround --apple-tv --host 192.0.2.110 --tone", out _)!.Beeps);

        var silent = Parse("stream --surround --apple-tv --host 192.0.2.110 --silent", out _)!;
        Assert.True(silent.AppleTv);
        Assert.Equal((0, 10.0), (silent.Beeps, silent.CapPercent)); // silence keeps the 10 % ceiling

        foreach (string refused in new[]
                 {
                     "stream --surround --apple-tv --host 192.0.2.110",                  // neither silence nor a tone
                     "stream --surround --apple-tv --host 192.0.2.110 --tone --silent",
                     "stream --surround --apple-tv --host 192.0.2.110 --tone --cap 36",
                     "stream --surround --apple-tv --host 192.0.2.110 --tone --volume 50",
                     "stream --surround --apple-tv --host 192.0.2.110 --tone --beeps 0",
                     "stream --surround --apple-tv --host 192.0.2.110 --silent --cap 25", // silence: 10 % as before
                 })
        {
            Assert.Null(Parse(refused, out var why));
            Assert.NotNull(why);
        }
        Assert.False(Parse("stream --surround --host 192.0.2.26 --silent", out _)!.AppleTv); // a HomePod: as before
    }

    [Fact]
    public void A_host_is_required()
    {
        Assert.Null(Parse("stream --surround --silent", out var error));
        Assert.Contains("--host", error);
        Assert.Null(Parse("stream --surround --silent --host bedroom", out _));
    }

    [Fact]
    public void Defaults_are_the_apps_latency_30_seconds_and_10_percent()
    {
        var o = Parse("stream --surround --host 192.0.2.26 --silent", out var error)!;
        Assert.Null(error);
        Assert.Equal(IPAddress.Parse("192.0.2.26"), o.Host);
        Assert.Equal(10, o.CapPercent);
        Assert.Equal(10, o.VolumePercent); // the volume defaults to the cap
        Assert.Equal(1800, o.LatencyMs);
        Assert.Equal(30, o.Seconds);
    }

    [Fact]
    public void Options_are_taken_and_the_latency_is_clamped()
    {
        var o = Parse("stream --surround --host 192.0.2.26 --silent --cap 5 --volume 2.5 --latency 2000 --seconds 8", out _)!;
        Assert.Equal((5.0, 2.5, 2000, 8), (o.CapPercent, o.VolumePercent, o.LatencyMs, o.Seconds));
        Assert.Equal(1000, Parse("stream --surround --host 192.0.2.26 --silent --latency 250", out _)!.LatencyMs);
        Assert.Equal(4000, Parse("stream --surround --host 192.0.2.26 --silent --latency 9000", out _)!.LatencyMs);
        Assert.Equal(5.0, Parse("stream --surround --host 192.0.2.26 --silent --cap 5", out _)!.VolumePercent);
    }
}
