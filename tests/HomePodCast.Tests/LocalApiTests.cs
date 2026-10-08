using System.Net;
using System.Net.Sockets;
using System.Text;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class LocalApiTests
{
    /// <summary>Send one raw request to the API and return the status code and body.</summary>
    private static async Task<(int Code, string Body)> Send(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var statusLine = response.Split("\r\n")[0];
        return (int.Parse(statusLine.Split(' ')[1]), response[(response.IndexOf("\r\n\r\n") + 4)..]);
    }

    private static string Get(string path, params string[] headers) =>
        $"GET {path} HTTP/1.1\r\n" + string.Concat(headers.Select(h => h + "\r\n")) + "\r\n";

    [Fact]
    public async Task Status_is_served_to_the_extension_on_127_0_0_1_and_localhost()
    {
        using var api = new LocalApi(0, () => new { app = "HomePodCast", streaming = true });
        var (code, body) = await Send(api.Port, Get("/v1/status", $"Host: 127.0.0.1:{api.Port}", "Accept: */*"));
        Assert.Equal(200, code);
        Assert.Contains("\"streaming\":true", body);
        Assert.Equal(200, (await Send(api.Port, Get("/v1/status?x=1", $"host: LOCALHOST:{api.Port}"))).Code);
        Assert.Equal(404, (await Send(api.Port, Get("/v2/other", $"Host: 127.0.0.1:{api.Port}"))).Code);
    }

    [Theory]
    [InlineData("Host: evil.example:{0}")]       // DNS rebinding: a page whose name now resolves to 127.0.0.1
    [InlineData("Host: 127.0.0.1")]               // no port
    [InlineData("Host: 127.0.0.1:1")]             // another port
    [InlineData("Host: 127.0.0.1.evil.example:{0}")]
    [InlineData("Host: [::1]:{0}")]
    [InlineData("X-Host: 127.0.0.1:{0}")]         // no Host header at all
    public async Task Any_other_host_is_refused(string header)
    {
        bool called = false;
        using var api = new LocalApi(0, () => { called = true; return new { streaming = true }; });
        var (code, body) = await Send(api.Port, Get("/v1/status", string.Format(header, api.Port)));
        Assert.Equal(403, code);
        Assert.DoesNotContain("streaming", body);
        Assert.False(called);
    }

    [Fact]
    public async Task Two_host_headers_are_a_bad_request()
    {
        using var api = new LocalApi(0, () => new { streaming = true });
        var (code, _) = await Send(api.Port, Get("/v1/status", $"Host: 127.0.0.1:{api.Port}", "Host: evil.example"));
        Assert.Equal(400, code);
    }

    [Fact]
    public async Task A_failing_request_does_not_end_the_api()
    {
        int calls = 0;
        using var api = new LocalApi(0, () => ++calls == 1 ? throw new InvalidOperationException("boom") : new { streaming = false });
        string ok = Get("/v1/status", $"Host: 127.0.0.1:{api.Port}");

        Assert.Equal(500, (await Send(api.Port, ok)).Code);         // the status callback threw
        using (var dropped = new TcpClient())                          // connects and resets without a request
        {
            await dropped.ConnectAsync(IPAddress.Loopback, api.Port);
            dropped.Client.LingerState = new LingerOption(true, 0);
        }
        using (var garbage = new TcpClient())                          // not HTTP at all
        {
            await garbage.ConnectAsync(IPAddress.Loopback, api.Port);
            await garbage.GetStream().WriteAsync(new byte[] { 0, 1, 2, 3, 13, 10, 13, 10 });
        }
        var (code, body) = await Send(api.Port, ok);
        Assert.Equal(200, code);
        Assert.Contains("\"streaming\":false", body);
    }

    [Fact]
    public void Host_check_is_exact()
    {
        Assert.True(LocalApi.IsOurHost("127.0.0.1:47100", 47100));
        Assert.True(LocalApi.IsOurHost("localhost:47100", 47100));
        Assert.False(LocalApi.IsOurHost(null, 47100));
        Assert.False(LocalApi.IsOurHost("127.0.0.1:471000", 47100));
        Assert.False(LocalApi.IsOurHost("localhost.:47100", 47100));
        Assert.False(LocalApi.IsOurHost("", 47100));
        Assert.Equal(403, LocalApi.StatusCodeFor("GET /v1/status HTTP/1.0\r\n\r\n", 47100));
        Assert.Equal(200, LocalApi.StatusCodeFor("GET /v1/status HTTP/1.1\r\nHost:127.0.0.1:47100\r\n\r\n", 47100));
        // A Host line after the blank line is body, not a header.
        Assert.Equal(403, LocalApi.StatusCodeFor("GET /v1/status HTTP/1.1\r\n\r\nHost: 127.0.0.1:47100\r\n", 47100));
    }
}
