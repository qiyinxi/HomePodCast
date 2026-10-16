using HomePodCast.Net;

namespace HomePodCast.Tests;

public class ReceiverGroupClockTests
{
    [Fact]
    public void NTP_and_PTP_share_the_Unix_epoch_and_fraction_without_millisecond_truncation()
    {
        const ulong epoch = 2_208_988_800UL << 32;
        Assert.Equal(0UL, PtpMaster.NanosecondsFromNtp(epoch));
        Assert.Equal(1_500_000_000UL, PtpMaster.NanosecondsFromNtp(epoch + (1UL << 32) + 0x80000000));
        Assert.Equal(999_999_999UL, PtpMaster.NanosecondsFromNtp(epoch + uint.MaxValue));
    }

    [Fact]
    public void NTP_era_rollover_remains_a_continuous_Unix_timestamp()
    {
        ulong before = PtpMaster.NanosecondsFromNtp(ulong.MaxValue);
        ulong after = PtpMaster.NanosecondsFromNtp(0);
        Assert.Equal(1UL, after - before);
        Assert.Equal(2_085_978_496_000_000_000UL, after);
    }

    [Fact]
    public void A_future_group_epoch_and_hour_of_media_keep_the_same_monotonic_timebase()
    {
        long start = MediaClock.Now + MediaClock.FromMs(250);
        ulong first = PtpMaster.NanosecondsAt(start);
        ulong hour = PtpMaster.NanosecondsAt(checked(start + MediaClock.Frequency * 3600));
        Assert.InRange(hour - first, 3_600_000_000_000UL - 1, 3_600_000_000_000UL + 1);
        ulong previous = first;
        for (int frame = 1; frame <= 44100; frame += 352)
        {
            long ticks = checked(start + (long)((Int128)frame * MediaClock.Frequency / 44100));
            ulong current = PtpMaster.NanosecondsAt(ticks);
            Assert.True(current > previous);
            ulong expected = (ulong)((UInt128)(ulong)frame * 1_000_000_000 / 44100);
            ulong actual = current - first;
            Assert.InRange(Math.Abs((long)actual - (long)expected), 0, (long)Math.Ceiling(1_000_000_000.0 / MediaClock.Frequency) + 1);
            previous = current;
        }
    }
}
