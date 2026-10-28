using System.Net;
using System.Text.Json;
using HomePodCast.Net;
using HomePodCast.Players;

namespace HomePodCast.Tests;

public sealed class MixerCallerIdentityTests
{
    [Fact]
    public async Task Owned_loopback_API_connection_identifies_the_requester_process_without_browser_name_guessing()
    {
        using var api = new LocalApi(0, (app, pid) => new { app = "HomePodCast", caller = pid }, PlayerWin32.ApiCaller);
        using var http = new HttpClient();
        using var status = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{api.Port}/v1/status"));
        Assert.Equal((uint)Environment.ProcessId, status.RootElement.GetProperty("caller").GetUInt32());
    }

    [Fact]
    public void Listener_wrong_endpoint_and_TIME_WAIT_rows_cannot_be_mistaken_for_a_browser()
    {
        uint loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
        var row = new PlayerWin32.ConnectionRow(5, loopback, 12345, loopback, 47100, 42);
        var client = new IPEndPoint(IPAddress.Loopback, 12345);
        Assert.True(PlayerWin32.MatchesCaller(row, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { State = 2 }, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { State = 11 }, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { Pid = 0 }, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { RemotePort = 47101 }, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { LocalPort = 12346 }, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { LocalAddress = 0 }, client, 47100));
        Assert.False(PlayerWin32.MatchesCaller(row with { RemoteAddress = 0 }, client, 47100));
    }

    [Fact]
    public async Task An_explicit_app_request_remains_read_only_and_does_not_probe_an_unrelated_client_identity()
    {
        int lookups = 0;
        using var api = new LocalApi(0, (app, pid) => new { app = "HomePodCast", requested = app, caller = pid },
            (_, _) => { lookups++; return 99; });
        using var http = new HttpClient();
        using var status = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{api.Port}/v1/status?app=chrome.exe"));
        Assert.Equal("chrome", status.RootElement.GetProperty("requested").GetString());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("caller").ValueKind);
        Assert.Equal(0, lookups);
    }
}
