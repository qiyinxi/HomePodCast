using HomePodCast.Net;

if (args is ["--self-test"])
{
    if (ReceiverKinds.Features("0x11223344,0x55667788") != 0x5566778811223344UL)
        throw new Exception("64-bit features lost their high word");
    if (ReceiverKinds.Classify("AppleTV3,2", new Dictionary<string, string> { ["features"] = "10879", ["srcvers"] = "220.68" })
        != ReceiverKind.ThirdPartyAirPlay1) throw new Exception("legacy emulator classification");
    Console.WriteLine("Protocol harness self-test passed");
    return;
}

if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Receiver integration tests only run in GitHub Actions");
if (args is not ["--discover", var name, var portText] || !int.TryParse(portText, out int port))
    throw new ArgumentException("--discover NAME PORT required");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (!deadline.IsCancellationRequested)
{
    var found = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), deadline.Token);
    foreach (var device in found) Console.WriteLine(device);
    var receiver = found.FirstOrDefault(d => d.Name == name);
    if (receiver != null)
    {
        if (receiver.Port != port || receiver.RaopPort != port || receiver.Kind != ReceiverKind.ThirdPartyAirPlay1)
            throw new Exception($"Unexpected RAOP advertisement: {receiver}, raop={receiver.RaopPort}");
        Console.WriteLine("shairport-sync RAOP discovery passed");
        return;
    }
}
throw new Exception("shairport-sync was not discovered");

namespace HomePodCast
{
    // The console harness compiles the production protocol sources without WinForms or embedded UI resources.
    internal static class L { public static string T(string text) => text; }
}
