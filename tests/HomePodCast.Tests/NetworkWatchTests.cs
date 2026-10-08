using System.Net;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class NetworkWatchTests
{
    [Fact]
    public async Task Summarizes_round_trips_and_resets_after_each_summary()
    {
        using var watch = new NetworkWatch(IPAddress.Loopback);
        await Task.Delay(600);
        var first = watch.TakeSummary();
        Assert.Matches(@"^ping=\d+/\d+/\d+ms lost=0/[3-9]$", first);  // ~6 pings in 600 ms
        Assert.Matches(@"^ping=(-|\d+/\d+/\d+ms lost=0/1)$", watch.TakeSummary()); // reset: at most one since
    }
}
