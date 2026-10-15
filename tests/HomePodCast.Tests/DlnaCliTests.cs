namespace HomePodCast.Tests;

public class DlnaCliTests
{
    [Fact]
    public void Playback_requires_an_explicit_source_and_device()
    {
        Assert.Throws<ArgumentException>(() => DlnaCli.Parse(["dlna"]));
        Assert.Throws<ArgumentException>(() => DlnaCli.Parse(["dlna", "--udn", "uuid:test"]));
        Assert.Throws<ArgumentException>(() => DlnaCli.Parse(["dlna", "--silent"]));
        Assert.Throws<ArgumentException>(() => DlnaCli.Parse(["dlna", "--udn", "uuid:test", "--silent", "--capture", "endpoint"]));
        Assert.Throws<ArgumentException>(() => DlnaCli.Parse(["dlna", "--scan", "--take-over"]));
        Assert.True(DlnaCli.Parse(["dlna", "--scan"]).Scan);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Blank_capture_never_falls_back_to_the_default_endpoint(string capture) => Assert.Throws<ArgumentException>(() =>
        DlnaCli.Parse(["dlna", "--udn", "uuid:test", "--capture", capture]));

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("101")]
    public void Rejects_invalid_volume_before_any_discovery(string volume) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        DlnaCli.Parse(["dlna", "--udn", "uuid:test", "--silent", "--volume", volume]));

    [Fact]
    public void Command_settings_are_explicit_and_bounded()
    {
        var options = DlnaCli.Parse(["dlna", "--udn", "uuid:test", "--capture", "exact-id", "--format", "wav", "--large-length"]);
        Assert.Equal("exact-id", options.CaptureId);
        Assert.False(options.Silent);
        Assert.Equal(25, options.Cap);
        Assert.False(options.TakeOver);
        Assert.Equal(Net.DlnaAudioFormat.Wav, options.Format);
        Assert.Equal(Net.DlnaHttpFraming.LargeContentLength, options.Framing);
    }
}
