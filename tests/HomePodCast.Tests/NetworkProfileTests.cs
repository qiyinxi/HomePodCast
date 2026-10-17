using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HomePodCast.Net;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

public class NetworkProfileTests(ITestOutputHelper output)
{
    private static readonly NetworkProfile Home = new("以太网", AdapterKind.Ethernet, "ASUS_D0", NetworkCategory.Private);
    private static readonly NetworkProfile Public = Home with { Category = NetworkCategory.Public };

    // Known, rule for Private networks, Public networks allowed (a rule or the firewall off there).
    private static FirewallRules Rules(bool privateRule, bool publicAllowed = false) => new(true, privateRule, publicAllowed);

    [Theory]
    // A Public network: the rule (Private only) doesn't apply, so that's what to fix, rule or no rule.
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Public, true, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Public, false, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Public, true, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Public, false, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Public, true, false, FirewallPrompt.PublicNetwork)]
    // ... unless the firewall lets us in on Public networks anyway: then something else blocks.
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Public, true, true, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Public, true, true, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Public, true, true, FirewallPrompt.OtherBlocker)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Public, false, true, FirewallPrompt.OtherBlocker)]
    // Startup as before: offer the rule when it's missing, whatever the network.
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Private, false, false, FirewallPrompt.AddRule)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Unknown, false, false, FirewallPrompt.AddRule)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Domain, false, false, FirewallPrompt.AddRule)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Private, true, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Unknown, true, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Domain, true, false, FirewallPrompt.None)]
    // Blocked: never a duplicate rule.
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Private, true, false, FirewallPrompt.OtherBlocker)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Private, false, false, FirewallPrompt.AddRule)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Unknown, false, false, FirewallPrompt.AddRule)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Unknown, true, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Domain, true, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Domain, false, false, FirewallPrompt.None)]
    // A network change only ever brings the Public network warning.
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Private, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Unknown, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Domain, true, false, FirewallPrompt.None)]
    public void Decides_which_prompt_to_show(FirewallTrigger trigger, NetworkCategory category, bool privateRule, bool publicAllowed,
        FirewallPrompt expected) =>
        Assert.Equal(expected, FirewallAdvisor.Decide(trigger, Rules(privateRule, publicAllowed), category));

    [Theory]
    [InlineData(FirewallTrigger.Startup)]
    [InlineData(FirewallTrigger.Blocked)]
    [InlineData(FirewallTrigger.NetworkChanged)]
    public void Unreadable_rules_never_offer_a_rule_but_still_warn_about_a_public_network(FirewallTrigger trigger)
    {
        foreach (var category in new[] { NetworkCategory.Private, NetworkCategory.Domain, NetworkCategory.Unknown })
            Assert.Equal(FirewallPrompt.None, FirewallAdvisor.Decide(trigger, FirewallRules.Unknown, category));
        Assert.Equal(FirewallPrompt.PublicNetwork, FirewallAdvisor.Decide(trigger, FirewallRules.Unknown, NetworkCategory.Public));
    }

    [Theory]
    [InlineData(AdapterKind.Ethernet, "ms-settings:network-ethernet")]
    [InlineData(AdapterKind.WiFi, "ms-settings:network-wifi")]
    [InlineData(AdapterKind.Other, "ms-settings:network-status")]
    public void Settings_page_matches_the_adapter(AdapterKind kind, string uri)
    {
        Assert.Equal(uri, NetworkProfile.SettingsUriFor(kind));
        Assert.Equal(uri, (Home with { Kind = kind }).SettingsUri);
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, AdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.GigabitEthernet, AdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.FastEthernetT, AdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Wireless80211, AdapterKind.WiFi)]
    [InlineData(NetworkInterfaceType.Ppp, AdapterKind.Other)]
    [InlineData(NetworkInterfaceType.Tunnel, AdapterKind.Other)]
    [InlineData(NetworkInterfaceType.Wwanpp, AdapterKind.Other)]
    public void Adapter_kind_from_the_interface_type(NetworkInterfaceType type, AdapterKind kind) =>
        Assert.Equal(kind, NetworkProfile.KindOf(type));

    [Fact]
    public void Categories_from_the_network_list_and_the_firewall_profiles()
    {
        Assert.Equal(NetworkCategory.Public, NetworkProfile.FromNlm(0));
        Assert.Equal(NetworkCategory.Private, NetworkProfile.FromNlm(1));
        Assert.Equal(NetworkCategory.Domain, NetworkProfile.FromNlm(2));
        Assert.Equal(NetworkCategory.Unknown, NetworkProfile.FromNlm(7));

        Assert.Equal(NetworkCategory.Domain, NetworkProfile.FromProfileTypes(1));
        Assert.Equal(NetworkCategory.Private, NetworkProfile.FromProfileTypes(2));
        Assert.Equal(NetworkCategory.Public, NetworkProfile.FromProfileTypes(4));
        // Two active profiles (Hyper-V's Default Switch sits in Public next to the home network): can't tell.
        Assert.Equal(NetworkCategory.Unknown, NetworkProfile.FromProfileTypes(6));
        Assert.Equal(NetworkCategory.Unknown, NetworkProfile.FromProfileTypes(0));
    }

    [Fact]
    public void Describes_the_network_for_the_log_and_the_user()
    {
        Assert.Equal("以太网 (Ethernet), profile \"ASUS_D0\" is Public", Public.ToString());
        Assert.Equal("WLAN (Wi-Fi), profile is Private",
            new NetworkProfile("WLAN", AdapterKind.WiFi, null, NetworkCategory.Private).ToString());
        Assert.Equal("? (other), profile unknown", NetworkProfile.Unknown.ToString());
        Assert.Equal("ASUS_D0", Public.DisplayName);
        Assert.Equal("WLAN", new NetworkProfile("WLAN", AdapterKind.WiFi, null, NetworkCategory.Public).DisplayName);
    }

    [Fact]
    public void A_public_network_prompt_shows_once_per_network_and_never_two_at_a_time()
    {
        var advisor = new FirewallAdvisor(() => Rules(true), _ => Public);
        var (prompt, network) = advisor.Check(FirewallTrigger.Startup, null);
        Assert.Equal(FirewallPrompt.PublicNetwork, prompt);

        Assert.True(advisor.TryOpen(prompt, network));
        Assert.True(advisor.IsOpen);
        // While it is open, nothing else opens (and the rule offer isn't used up by trying).
        Assert.False(advisor.TryOpen(FirewallPrompt.AddRule, network));
        Assert.False(advisor.TryOpen(FirewallPrompt.PublicNetwork, Public with { Name = "Cafe" }));
        advisor.Close();
        Assert.False(advisor.IsOpen);

        // A later failure on the same network: same answer, no second dialog.
        (prompt, network) = advisor.Check(FirewallTrigger.Blocked, IPAddress.Parse("192.168.50.226"));
        Assert.Equal(FirewallPrompt.PublicNetwork, prompt);
        Assert.False(advisor.TryOpen(prompt, network));

        // Another Public network gets its own, once; the rule offer still can be shown, once.
        Assert.True(advisor.TryOpen(FirewallPrompt.PublicNetwork, Public with { Name = "Cafe" }));
        advisor.Close();
        Assert.False(advisor.TryOpen(FirewallPrompt.PublicNetwork, Public with { Name = "Cafe" }));
        Assert.True(advisor.TryOpen(FirewallPrompt.AddRule, Home));
        advisor.Close();
        Assert.False(advisor.TryOpen(FirewallPrompt.AddRule, Home));
        Assert.False(advisor.TryOpen(FirewallPrompt.None, Home));
    }

    [Fact]
    public void Check_reads_the_speakers_network_and_survives_failing_lookups()
    {
        IPAddress? asked = null;
        var speaker = IPAddress.Parse("192.168.50.226");
        var advisor = new FirewallAdvisor(() => Rules(true), ip => { asked = ip; return Home; });
        Assert.Equal((FirewallPrompt.OtherBlocker, Home), advisor.Check(FirewallTrigger.Blocked, speaker));
        Assert.Equal(speaker, asked);

        var broken = new FirewallAdvisor(() => throw new InvalidOperationException("no firewall"),
            _ => throw new InvalidOperationException("no network list"));
        foreach (var trigger in Enum.GetValues<FirewallTrigger>())
            Assert.Equal((FirewallPrompt.None, NetworkProfile.Unknown), broken.Check(trigger, speaker));
    }

    [Fact]
    public void Blocked_message_names_a_public_network()
    {
        Assert.Equal("音箱发来的对时请求没有到达本程序，通常是 Windows 防火墙拦截了 HomePodCast.exe 的入站连接",
            new FirewallBlockedException(Home).Message);
        Assert.Equal(new FirewallBlockedException(Home).Message, new FirewallBlockedException().Message);
        // On a Public network it says so, unless this machine's firewall lets the test host in there anyway.
        if (!Firewall.Inspect().PublicAllowed)
            Assert.Equal("网络「ASUS_D0」是公用网络，Windows 防火墙拦截了音箱连回本程序的连接", new FirewallBlockedException(Public).Message);
    }

    [Fact]
    public void Detection_never_throws()
    {
        // The real thing (Network List Manager over COM): whatever this machine's networks are, an answer comes back.
        var route = NetworkProfile.ForSpeaker(null);
        output.WriteLine($"default route: {route}");
        Assert.NotNull(route);
        Assert.Equal(NetworkProfile.Unknown, NetworkProfile.ForSpeaker(IPAddress.Loopback));
        Assert.Equal(NetworkProfile.Unknown, NetworkProfile.ForLocalAddress(IPAddress.Parse("192.0.2.123"))); // no adapter has it
        output.WriteLine($"firewall: {Firewall.Inspect()}, active profiles {Firewall.CurrentProfileTypes()}");
    }
}

/// <summary>
/// Alone, never beside LoopbackOnlyTests: a UDP socket that only Connect bound is listed as 0.0.0.0 (every address) in
/// Windows' UDP table while it is open, though its own local address is the one Connect chose. 2026-10-16: connected to
/// 192.0.2.1, one said 192.168.50.200:51782 and the table had 0.0.0.0:51782; connected to 127.0.0.1, the same.
/// </summary>
[CollectionDefinition("Connected UDP sockets", DisableParallelization = true)]
public sealed class ConnectedUdpSocketsCollection;

[Collection("Connected UDP sockets")]
public class RouteLookupTests
{
    /// <summary>The route table's answer is the one NetworkProfile.ForSpeaker got from a connected socket before.</summary>
    [Theory]
    [InlineData("192.0.2.1")]   // TEST-NET-1: the default route, as ForSpeaker(null) asks
    [InlineData("127.0.0.1")]   // the tests' fakes
    [InlineData("::1")]
    [InlineData("2001:db8::1")] // the default IPv6 route, or none
    public void Picks_the_local_address_a_connected_socket_gets(string destination)
    {
        var to = IPAddress.Parse(destination);
        IPAddress? connected = null, routed = null;
        try
        {
            using var socket = new Socket(to.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(to, 7000)); // sends nothing
            connected = ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (SocketException) { } // no route (or no IPv6 at all)
        try { routed = Native.SourceAddressFor(to); }
        catch (Win32Exception) { }
        Assert.Equal(connected, routed);
    }

    /// <summary>
    /// The network lookup leaves nothing in the UDP table while it runs. 2026-10-16: LoopbackOnlyTests failed on
    /// 0.0.0.0:51095 in a full test run. The lookup's probe socket was the only one of that kind the tests reach, open
    /// through the Network List Manager query (about 100 ms); a sampler like this one saw it about 1300 times a second.
    /// </summary>
    [Fact]
    public async Task Looking_up_the_network_opens_no_socket()
    {
        using var done = new CancellationTokenSource();
        var seen = new ConcurrentBag<IPEndPoint>();
        var sampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = Task.Factory.StartNew(() =>
        {
            while (!done.IsCancellationRequested)
            {
                foreach (var e in LoopbackOnlyTests.Listening().Where(e => !IPAddress.IsLoopback(e.Address))) seen.Add(e);
                sampling.TrySetResult();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); // its own thread: a busy pool doesn't hold it up
        try
        {
            await Task.WhenAny(sampling.Task, sampler); // a sampler that failed is awaited below
            for (int i = 0; i < 3; i++) NetworkProfile.ForSpeaker(null);
            for (int i = 0; i < 100; i++) NetworkProfile.ForSpeaker(IPAddress.Loopback); // as every connection to a fake asks
        }
        finally
        {
            done.Cancel();
            await sampler;
        }
        Assert.Empty(seen);
    }
}
