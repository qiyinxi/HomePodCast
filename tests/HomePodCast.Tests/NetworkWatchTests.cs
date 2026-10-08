using System.Net;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class NetworkWatchTests
{
    [Fact]
    public async Task Summarizes_round_trips_and_resets_after_each_summary()
    {
        using var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Parse("127.0.0.2"));
        await Task.Delay(600);
        var first = watch.TakeSummary();
        // ~6 pings in 600 ms to each target
        Assert.Matches(@"^ping=\d+/\d+/\d+ms lost=0/[3-9] router=\d+/\d+/\d+ms lost=0/[3-9]$", first);
        // reset: at most one more since
        Assert.Matches(@"^ping=(-|\d+/\d+/\d+ms lost=0/1) router=(-|\d+/\d+/\d+ms lost=0/1)$", watch.TakeSummary());
    }

    [Fact]
    public void Same_subnet_uses_the_mask()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.True(NetworkWatch.SameSubnet(IPAddress.Parse("192.168.50.10"), IPAddress.Parse("192.168.50.226"), mask));
        Assert.False(NetworkWatch.SameSubnet(IPAddress.Parse("192.168.51.10"), IPAddress.Parse("192.168.50.226"), mask));
    }
}
