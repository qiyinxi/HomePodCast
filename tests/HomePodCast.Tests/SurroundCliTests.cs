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
