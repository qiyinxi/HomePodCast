using HomePodCast.Net;

namespace HomePodCast.Tests;

public class VolumeCapTests
{
    [Theory]
    [InlineData(80, 60, 60)]
    [InlineData(40, 60, 40)]
    [InlineData(100, 100, 100)]
    [InlineData(-5, 60, 0)]
    [InlineData(80, 150, 80)]
    [InlineData(double.NaN, 60, 0)]
    public void Clamp_never_exceeds_the_cap(double percent, double cap, double expected)
    {
        Assert.Equal(expected, VolumeLimit.Clamp(percent, cap));
    }

    [Fact]
    public void Db_clamp_matches_the_percent_clamp_and_lets_mute_through()
    {
        Assert.Equal(AirPlayClient.PercentToDb(60), VolumeLimit.ClampDb(AirPlayClient.PercentToDb(90), 60));
        Assert.Equal(AirPlayClient.PercentToDb(40), VolumeLimit.ClampDb(AirPlayClient.PercentToDb(40), 60));
        Assert.Equal(0.0, VolumeLimit.ClampDb(0.0, 100));        // 0 dB = 100 %: no cap
        Assert.Equal(-144.0, VolumeLimit.ClampDb(-144.0, 25));   // mute always passes
        Assert.Equal(-12.0, VolumeLimit.ClampDb(-5.0, 60), 6);   // 60 % = -12 dB
    }

    [Fact]
    public void Cap_setting_is_kept_in_a_usable_range()
    {
        Assert.Equal(VolumeLimit.MinCap, VolumeLimit.NormalizeCap(0));
        Assert.Equal(100, VolumeLimit.NormalizeCap(250));
        Assert.Equal(55, VolumeLimit.NormalizeCap(55));
    }

    [Fact]
    public void Controller_clamps_every_volume_and_lowers_the_current_one_when_the_cap_drops()
    {
        using var c = new StreamController();
        c.SetVolumeCap(50);
        c.SetVolume(80);
        Assert.Equal(50, c.Volume);
        c.SetVolume(30);
        Assert.Equal(30, c.Volume);
        c.SetVolumeCap(20);
        Assert.Equal(20, c.Volume);
        c.SetMuted(true);
        Assert.True(c.Muted);
        c.SetVolume(10);                 // changing the volume unmutes
        Assert.False(c.Muted);
        Assert.Equal(10, c.Volume);
    }
}
