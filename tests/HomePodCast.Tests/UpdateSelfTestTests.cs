using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Update;

namespace HomePodCast.Tests;

/// <summary>The release-list override of update-selftest: http://127.0.0.1 and http://localhost only.</summary>
public class ReleaseFeedTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8123/releases", "http://127.0.0.1:8123/releases", "http://127.0.0.1:8123/")]
    [InlineData("http://localhost:8123/repos/qiyinxi/HomePodCast/releases?per_page=20", "http://localhost:8123/repos/qiyinxi/HomePodCast/releases?per_page=20", "http://localhost:8123/")]
    [InlineData("http://LOCALHOST/releases", "http://localhost/releases", "http://localhost/")]
    [InlineData("http://127.0.0.1", "http://127.0.0.1/", "http://127.0.0.1/")]
    [InlineData("HTTP://127.0.0.1:1/x", "http://127.0.0.1:1/x", "http://127.0.0.1:1/")]
    public void Loopback_accepts_http_on_127_0_0_1_and_localhost(string url, string api, string downloads)
    {
        var feed = ReleaseFeed.Loopback(url);
        Assert.Equal(api, feed.ApiUrl);
        Assert.Equal(downloads, feed.DownloadPrefix);
        Assert.False(feed.FollowRedirects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:8123/releases")]
    [InlineData("https://127.0.0.1:8123/releases")]                                    // http only
    [InlineData("https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20")] // the real one too: never through the command line
    [InlineData("http://api.github.com/repos/qiyinxi/HomePodCast/releases")]
    [InlineData("http://127.0.0.2/releases")]
    [InlineData("http://0.0.0.0/releases")]
    [InlineData("http://[::1]/releases")]
    [InlineData("http://192.168.50.1/releases")]
    [InlineData("http://localhost.example.com/releases")]
    [InlineData("http://127.0.0.1.nip.io/releases")]
    [InlineData("http://127.0.0.1@example.com/releases")]
    [InlineData("http://127.0.0.1:8123@example.com/releases")]
    [InlineData("http://localhost:pw@example.com/")]
    [InlineData("http://user:pw@127.0.0.1/releases")]
    [InlineData("http://example.com/?u=http://127.0.0.1/")]
    [InlineData("http://example.com/#http://localhost/")]
    [InlineData("http://127.0.0.1\\@example.com/")]
    [InlineData(" http://127.0.0.1/releases")]
    [InlineData("http://127.0.0.1/rel eases")]
    [InlineData("ftp://127.0.0.1/releases")]
    [InlineData("file:///C:/releases.json")]
    [InlineData("C:\\releases.json")]
    [InlineData("\\\\127.0.0.1\\share\\releases.json")]
    public void Loopback_refuses_everything_else_with_a_clear_error(string? url)
    {
        var ex = Assert.Throws<ArgumentException>(() => ReleaseFeed.Loopback(url));
        Assert.Contains("http://127.0.0.1 or http://localhost", ex.Message);
    }

    [Fact]
    public void The_app_uses_githubs_feed_unchanged()
    {
        Assert.Equal("https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20", ReleaseFeed.GitHub.ApiUrl);
        Assert.Equal("https://github.com/qiyinxi/HomePodCast/releases/download/", ReleaseFeed.GitHub.DownloadPrefix);
        Assert.True(ReleaseFeed.GitHub.FollowRedirects); // GitHub's downloads redirect to its file host
        var byDefault = GitHubReleases.Parse(ReleaseSelectionTests.Fixture);
        var github = GitHubReleases.Parse(ReleaseSelectionTests.Fixture, ReleaseFeed.GitHub);
        Assert.Equal(byDefault.Select(r => (r.Tag, r.Msi?.Url, r.Msi?.Sha256)), github.Select(r => (r.Tag, r.Msi?.Url, r.Msi?.Sha256)));
    }

    private static string Feed(string msiUrl) => $$"""
        [ { "tag_name": "v9.0.0", "prerelease": false, "assets": [
            { "name": "HomePodCast-9.0.0-x64.msi", "state": "uploaded", "size": 10,
              "digest": "sha256:475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e", "browser_download_url": "{{msiUrl}}" } ] } ]
        """;

    [Theory]
    [InlineData("http://127.0.0.1:8123/download/v9.0.0/HomePodCast-9.0.0-x64.msi", true)]
    [InlineData("http://127.0.0.1:8123/HomePodCast-9.0.0-x64.msi", true)]
    [InlineData("https://github.com/qiyinxi/HomePodCast/releases/download/v9.0.0/HomePodCast-9.0.0-x64.msi", false)] // only from the feed's own origin
    [InlineData("http://127.0.0.1:8124/download/v9.0.0/HomePodCast-9.0.0-x64.msi", false)]
    [InlineData("http://localhost:8123/download/v9.0.0/HomePodCast-9.0.0-x64.msi", false)]
    [InlineData("https://127.0.0.1:8123/download/v9.0.0/HomePodCast-9.0.0-x64.msi", false)]
    [InlineData("http://127.0.0.1:8123@example.com/HomePodCast-9.0.0-x64.msi", false)]
    [InlineData("http://example.com/HomePodCast-9.0.0-x64.msi", false)]
    public void A_loopback_feed_downloads_only_from_its_own_origin(string msiUrl, bool accepted)
    {
        var release = Assert.Single(GitHubReleases.Parse(Feed(msiUrl), ReleaseFeed.Loopback("http://127.0.0.1:8123/releases")));
        Assert.Equal(accepted, release.Msi != null);
        if (accepted) Assert.Equal(msiUrl, release.Msi!.Url);
    }

    [Fact]
    public void Githubs_feed_never_downloads_from_this_pc()
    {
        Assert.Null(Assert.Single(GitHubReleases.Parse(Feed("http://127.0.0.1:8123/download/v9.0.0/HomePodCast-9.0.0-x64.msi"))).Msi);
        Assert.Null(Assert.Single(GitHubReleases.Parse(Feed("http://localhost/HomePodCast-9.0.0-x64.msi"), ReleaseFeed.GitHub)).Msi);
    }

    private sealed class Api(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_loopback_feed_gets_the_same_request_as_github()
    {
        var api = new Api(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var http = new HttpClient(api);
        Assert.Equal("[]", await GitHubReleases.FetchAsync(http, ReleaseFeed.Loopback("http://127.0.0.1:8123/releases"), CancellationToken.None));
        Assert.Equal(HttpMethod.Get, api.Request!.Method);
        Assert.Equal("http://127.0.0.1:8123/releases", api.Request.RequestUri!.ToString());
        Assert.Equal("application/vnd.github+json", api.Request.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", Assert.Single(api.Request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Null(api.Request.Headers.Authorization);
    }
}

public class UpdateSelfTestArgumentTests
{
    [Fact]
    public void Reads_the_feed_the_channel_and_no_install()
    {
        var o = UpdateSelfTest.ParseArguments(["update-selftest", "--feed", "http://127.0.0.1:8123/releases", "--channel", "beta"]);
        Assert.Equal("http://127.0.0.1:8123/releases", o.Feed.ApiUrl);
        Assert.Equal(UpdateChannel.Beta, o.Channel);
        Assert.False(o.NoInstall);

        o = UpdateSelfTest.ParseArguments(["--feed", "http://localhost:9/r", "--no-install"]);
        Assert.Null(o.Channel); // the default for this build
        Assert.True(o.NoInstall);

        o = UpdateSelfTest.ParseArguments(["update-selftest", "--verbose", "--channel", "Stable", "--feed", "http://127.0.0.1/r"]);
        Assert.Equal(UpdateChannel.Stable, o.Channel);
    }

    [Theory]
    [InlineData("--feed is required", "update-selftest")]
    [InlineData("--feed is required", "update-selftest", "--channel", "beta")]
    [InlineData("--feed needs a value", "update-selftest", "--feed")]
    [InlineData("--feed needs a value", "update-selftest", "--feed", "--channel", "beta")]
    [InlineData("--feed is given twice", "update-selftest", "--feed", "http://127.0.0.1/a", "--feed", "http://127.0.0.1/b")]
    [InlineData("http://127.0.0.1 or http://localhost", "update-selftest", "--feed", "https://api.github.com/repos/qiyinxi/HomePodCast/releases")]
    [InlineData("http://127.0.0.1 or http://localhost", "update-selftest", "--feed", "http://192.168.50.10:8080/releases")]
    [InlineData("--channel is beta or stable", "update-selftest", "--feed", "http://127.0.0.1/r", "--channel", "nightly")]
    [InlineData("unknown argument \"--force\"", "update-selftest", "--feed", "http://127.0.0.1/r", "--force")]
    [InlineData("unknown argument \"update-selftest\"", "update-selftest", "update-selftest", "--feed", "http://127.0.0.1/r")]
    [InlineData("unknown argument \"http://127.0.0.1/r\"", "update-selftest", "http://127.0.0.1/r")]
    public void Refuses_what_it_does_not_understand(string message, params string[] args) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => UpdateSelfTest.ParseArguments(args)).Message);

    [Fact]
    public void Bad_arguments_exit_1_before_any_request() =>
        Assert.Equal(1, UpdateSelfTest.Run(["update-selftest", "--feed", "https://api.github.com/repos/qiyinxi/HomePodCast/releases"]));
}

public class UpdateFlowTests
{
    private static Release Rel(ReleaseAsset? msi) =>
        new(ReleaseVersion.Parse("9.0.0")!, "v9.0.0", "https://github.com/qiyinxi/HomePodCast/releases/tag/v9.0.0", false, msi);

    private static ReleaseAsset Msi(string? sha256) =>
        new("HomePodCast-9.0.0-x64.msi", "https://github.com/qiyinxi/HomePodCast/releases/download/v9.0.0/HomePodCast-9.0.0-x64.msi", 10, sha256);

    [Fact]
    public void The_update_button_installs_only_an_msi_install_with_a_checksum()
    {
        const string sha = "475c297fcc1a0c60ea97b9001db47ab659177926af5f0b191e548cfdcc2df16e";
        Assert.Equal(InstallRoute.Install, UpdateFlow.Route(Rel(Msi(sha)), msiInstall: true));
        Assert.Equal(InstallRoute.NoChecksum, UpdateFlow.Route(Rel(Msi(null)), msiInstall: true));
        Assert.Equal(InstallRoute.NoChecksum, UpdateFlow.Route(Rel(null), msiInstall: true));
        Assert.Equal(InstallRoute.ReleasePage, UpdateFlow.Route(Rel(Msi(sha)), msiInstall: false));
        Assert.Equal(InstallRoute.ReleasePage, UpdateFlow.Route(Rel(null), msiInstall: false));
    }

    [Fact]
    public void The_test_host_is_no_msi_install() => Assert.False(UpdateFlow.IsMsiInstall());
}

/// <summary>
/// update-selftest --no-install against a fake release list on 127.0.0.1 in this process: the check, the pick, the
/// download and its SHA-256 check, as the update button runs them. Never installs anything (--no-install, and the test
/// host is no MSI install); the served "MSI" is random bytes.
/// </summary>
public class UpdateSelfTestRunTests
{
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(300_000);
    private static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private const string Path99 = "/download/v99.0.0/HomePodCast-99.0.0-x64.msi";

    private static string Json(string baseUrl, string version, string? sha256, string? msiUrl = null) => $$"""
        [ { "tag_name": "v{{version}}", "draft": false, "prerelease": false,
            "html_url": "https://github.com/qiyinxi/HomePodCast/releases/tag/v{{version}}",
            "assets": [ { "name": "HomePodCast-{{version}}-x64.msi", "state": "uploaded", "size": {{Payload.Length}},
                          "digest": {{(sha256 == null ? "null" : $"\"sha256:{sha256}\"")}},
                          "browser_download_url": "{{msiUrl ?? $"{baseUrl}/download/v{version}/HomePodCast-{version}-x64.msi"}}" } ] },
          { "tag_name": "v100.0.0", "draft": true, "prerelease": false, "assets": [] } ]
        """;

    private static async Task<(int Code, string? Folder)> Run(FakeFeed feed)
    {
        string? folder = null;
        void Seen(string line)
        {
            if (line.Contains($"update: downloading {feed.Base}/") && line.LastIndexOf(" to ", StringComparison.Ordinal) is var at and > 0) folder = line[(at + 4)..];
        }
        Log.Written += Seen;
        try
        {
            var options = new UpdateSelfTest.Options(ReleaseFeed.Loopback(feed.Base + "/releases"), UpdateChannel.Beta, NoInstall: true);
            return (await UpdateSelfTest.RunAsync(options, CancellationToken.None), folder);
        }
        finally
        {
            Log.Written -= Seen;
        }
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_checked_and_removed_again()
    {
        await using var feed = new FakeFeed();
        feed.Routes["/releases"] = (200, Encoding.UTF8.GetBytes(Json(feed.Base, "99.0.0", PayloadSha)), null);
        feed.Routes[Path99] = (200, Payload, null);
        var (code, folder) = await Run(feed);
        Assert.Equal(UpdateSelfTest.HelperStarted, code);
        Assert.Equal(new[] { "/releases", Path99 }, feed.Requests);
        Assert.NotNull(folder);
        Assert.False(Directory.Exists(folder));
        Assert.All(feed.UserAgents, ua => Assert.Equal("HomePodCast/" + ReleaseVersion.CurrentText, ua));
    }

    [Fact]
    public async Task A_corrupted_download_is_refused_and_deleted()
    {
        await using var feed = new FakeFeed();
        feed.Routes["/releases"] = (200, Encoding.UTF8.GetBytes(Json(feed.Base, "99.0.0", PayloadSha)), null);
        feed.Routes[Path99] = (200, [.. Payload[..^1], (byte)(Payload[^1] ^ 1)], null);
        var (code, folder) = await Run(feed);
        Assert.Equal(UpdateSelfTest.VerificationFailed, code);
        Assert.NotNull(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Nothing_newer_means_no_download()
    {
        await using var feed = new FakeFeed();
        feed.Routes["/releases"] = (200, Encoding.UTF8.GetBytes(Json(feed.Base, "0.0.1", PayloadSha)), null);
        Assert.Equal(UpdateSelfTest.NoUpdate, (await Run(feed)).Code);
        Assert.Equal(new[] { "/releases" }, feed.Requests);
    }

    [Fact]
    public async Task An_msi_from_anywhere_but_the_feed_is_never_downloaded()
    {
        await using var feed = new FakeFeed();
        var elsewhere = "https://github.com/qiyinxi/HomePodCast/releases/download/v99.0.0/HomePodCast-99.0.0-x64.msi";
        feed.Routes["/releases"] = (200, Encoding.UTF8.GetBytes(Json(feed.Base, "99.0.0", PayloadSha, elsewhere)), null);
        Assert.Equal(UpdateSelfTest.NoUpdate, (await Run(feed)).Code); // no MSI from this feed: nothing for an MSI install
        Assert.Equal(new[] { "/releases" }, feed.Requests);
    }

    [Fact]
    public async Task Without_a_checksum_nothing_is_downloaded()
    {
        await using var feed = new FakeFeed();
        feed.Routes["/releases"] = (200, Encoding.UTF8.GetBytes(Json(feed.Base, "99.0.0", null)), null);
        Assert.Equal(UpdateSelfTest.NotInstalled, (await Run(feed)).Code);
        Assert.Equal(new[] { "/releases" }, feed.Requests);
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        await using var feed = new FakeFeed();
        feed.Routes["/releases"] = (200, Encoding.UTF8.GetBytes(Json(feed.Base, "99.0.0", PayloadSha)), null);
        feed.Routes[Path99] = (302, [], "http://127.0.0.1:1/HomePodCast-99.0.0-x64.msi"); // followed, it would be a refused connection
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => Run(feed));
        Assert.Equal("download: HTTP 302", ex.Message);
    }

    /// <summary>A minimal HTTP/1.1 server on 127.0.0.1 (one request per connection), like tools/ci/fake-release-feed.ps1.</summary>
    internal sealed class FakeFeed : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeFeed()
        {
            _listener.Start();
            Base = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(LoopAsync);
        }

        public string Base { get; }
        public ConcurrentDictionary<string, (int Status, byte[] Body, string? Location)> Routes { get; } = new();
        public List<string> Requests { get; } = [];
        public List<string> UserAgents { get; } = [];

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                        var path = (await reader.ReadLineAsync() ?? "").Split(' ') is [_, var p, ..] ? p : "";
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                            if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) lock (UserAgents) UserAgents.Add(line[11..].Trim());
                        lock (Requests) Requests.Add(path);
                        var (status, body, location) = Routes.TryGetValue(path, out var r) ? r : (404, "not found"u8.ToArray(), null);
                        var head = $"HTTP/1.1 {status} X\r\nContent-Length: {body.Length}\r\nConnection: close\r\n" +
                                   (location != null ? $"Location: {location}\r\n" : "") + "\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                        await stream.WriteAsync(body);
                    }
                    catch (Exception)
                    {
                        // the client went away
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
            _stop.Dispose();
        }
    }
}
