using System.Net;
using System.Text.RegularExpressions;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class NetworkWatchTests
{
    private const string Pings = @"\d+/\d+/\d+ms lost=(\d+)/(\d+)";

    [Fact]
    public async Task Summarizes_round_trips_and_resets_after_each_summary()
    {
        using var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Parse("127.0.0.2"));
        // CI runners can be slow to start the ping loops: wait (up to 5 s) until both targets have answered.
        Match first = Match.Empty;
        for (int i = 0; i < 50 && !first.Success; i++)
        {
            await Task.Delay(100);
            first = Regex.Match(watch.TakeSummary(), $"^ping={Pings} router={Pings}$");
        }
        Assert.True(first.Success, "no answers from the loopback addresses");
        Assert.Equal("0", first.Groups[1].Value); // nothing lost
        Assert.Equal("0", first.Groups[3].Value);

        // Each summary starts over: right after one, at most a couple of pings are counted.
        var again = Regex.Match(watch.TakeSummary(), $@"^ping=(-|{Pings}) router=(-|{Pings})$");
        Assert.True(again.Success);
        foreach (int g in new[] { 3, 6 })
            if (again.Groups[g].Success) Assert.True(int.Parse(again.Groups[g].Value) <= 2);
    }

    [Fact]
    public void Same_subnet_uses_the_mask()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.True(NetworkWatch.SameSubnet(IPAddress.Parse("192.168.50.10"), IPAddress.Parse("192.168.50.226"), mask));
        Assert.False(NetworkWatch.SameSubnet(IPAddress.Parse("192.168.51.10"), IPAddress.Parse("192.168.50.226"), mask));
    }
}
