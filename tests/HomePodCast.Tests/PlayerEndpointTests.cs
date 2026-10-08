using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomePodCast.Players;

namespace HomePodCast.Tests;

/// <summary>
/// The endpoints against in-process stand-ins that speak the players' protocols: a named pipe like mpv's
/// JSON IPC, and an HTTP server like VLC's web interface. Also checks the process/pipe/port lookups that
/// make sure nothing is sent to anyone else.
/// </summary>
public class PlayerEndpointTests
{
    /// <summary>mpv's IPC as far as audio-delay goes: one pipe instance per client, line-based JSON, events in between.</summary>
    private sealed class FakeMpv : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeMpv(double delaySeconds = 0)
        {
            Delay = delaySeconds;
            Name = "hpc-test-mpv-" + Guid.NewGuid().ToString("N");
            _loop = Task.Run(LoopAsync);
        }

        public string Name { get; }
        public double Delay { get; private set; }
        public ConcurrentQueue<string> Received { get; } = new();
        public int Connections;

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    await server.DisposeAsync();
                    return;
                }
                Interlocked.Increment(ref Connections);
                _ = Task.Run(() => ServeAsync(server));
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            await using var _ = pipe;
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    Received.Enqueue(line);
                    using var doc = JsonDocument.Parse(line);
                    var cmd = doc.RootElement.GetProperty("command");
                    long id = doc.RootElement.GetProperty("request_id").GetInt64();
                    string reply = cmd[0].GetString() switch
                    {
                        "get_property" when cmd[1].GetString() == "audio-delay" =>
                            $"{{\"data\":{Delay.ToString("F6", CultureInfo.InvariantCulture)},\"request_id\":{id},\"error\":\"success\"}}",
                        "set_property" when cmd[1].GetString() == "audio-delay" => Set(cmd[2].GetDouble(), id),
                        _ => $"{{\"request_id\":{id},\"error\":\"invalid parameter\"}}",
                    };
                    // mpv interleaves events with replies
                    var bytes = Encoding.UTF8.GetBytes("{\"event\":\"audio-reconfig\"}\n" + reply + "\n");
                    await pipe.WriteAsync(bytes, _stop.Token);
                    await pipe.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }

        private string Set(double value, long id)
        {
            Delay = value;
            return $"{{\"request_id\":{id},\"error\":\"success\"}}";
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Mpv_endpoint_sets_and_reads_audio_delay_over_the_pipe()
    {
        await using var mpv = new FakeMpv(delaySeconds: 0.05);
        var endpoint = new MpvEndpoint(mpv.Name, Environment.ProcessId, "mpv");

        Assert.Equal(new PlayerReading(50), await endpoint.ReadAsync(CancellationToken.None));
        await endpoint.WriteAsync(-236, CancellationToken.None);
        Assert.Equal(-0.236, mpv.Delay, 9);
        Assert.Equal(new PlayerReading(-236), await endpoint.ReadAsync(CancellationToken.None));

        var lines = mpv.Received.ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Matches("""^\{"command":\["get_property","audio-delay"\],"request_id":\d+\}$""", lines[0]);
        Assert.Matches("""^\{"command":\["set_property","audio-delay",-0\.236\],"request_id":\d+\}$""", lines[1]);
    }

    [Fact]
    public async Task Mpv_endpoint_sends_nothing_when_the_pipe_belongs_to_another_process()
    {
        await using var mpv = new FakeMpv();
        var endpoint = new MpvEndpoint(mpv.Name, processId: Environment.ProcessId + 1, "mpv");
        await Assert.ThrowsAsync<IOException>(() => endpoint.WriteAsync(-236, CancellationToken.None));
        await Task.Delay(100);
        Assert.Empty(mpv.Received);
        Assert.Equal(0, mpv.Delay);
    }

    [Fact]
    public async Task Mpv_state_machine_end_to_end_over_a_real_pipe()
    {
        await using var mpv = new FakeMpv(delaySeconds: 0.1);
        var scanner = new FixedScanner(new MpvEndpoint(mpv.Name, Environment.ProcessId, "mpv"));
        var sync = new PlayerSync(scanner);
        await sync.StepAsync(new PlayerSyncInput(true, StreamState.Streaming, 236), CancellationToken.None);
        Assert.Equal(-0.236, mpv.Delay, 9);
        Assert.Equal(PlayerState.Applied, sync.Statuses.Single().State);
        await sync.StepAsync(new PlayerSyncInput(true, StreamState.Idle, 0), CancellationToken.None);
        Assert.Equal(0.1, mpv.Delay, 9);
    }

    private sealed class FixedScanner(params IPlayerEndpoint[] endpoints) : IPlayerScanner
    {
        public Task<PlayerScan> ScanAsync(CancellationToken ct) => Task.FromResult(new PlayerScan(endpoints, []));
    }

    /// <summary>VLC's web interface as far as status.xml goes: Basic auth with an empty user, audiodelay command.</summary>
    private sealed class FakeVlc : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _password;

        public FakeVlc(string password, double delaySeconds = 0, bool playing = true)
        {
            _password = password;
            Delay = delaySeconds;
            Playing = playing;
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public double Delay { get; private set; }
        public bool Playing { get; set; }
        public ConcurrentQueue<(string RequestLine, string? Authorization)> Requests { get; } = new();

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            string? auth = null;
            while (await reader.ReadLineAsync() is { Length: > 0 } header)
                if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = header[14..].Trim();
            Requests.Enqueue((requestLine, auth));

            string status, body;
            if (_password.Length == 0)
            {
                (status, body) = ("403 Forbidden", "<html>Password for Web interface has not been set.</html>");
            }
            else if (auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _password)))
            {
                (status, body) = ("401 Unauthorized", "<html>401</html>");
            }
            else
            {
                var m = Regex.Match(requestLine, @"command=audiodelay&val=([-0-9.]+)");
                if (m.Success && Playing) Delay = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                status = "200 OK";
                body = Playing
                    ? $"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\" ?><root><audiodelay>{Delay.ToString(CultureInfo.InvariantCulture)}</audiodelay><currentplid>3</currentplid><state>playing</state></root>"
                    : "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\" ?><root><currentplid>-1</currentplid><state>stopped</state></root>";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/xml\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static IPEndPoint Local(int port) => new(IPAddress.Loopback, port);

    [Fact]
    public async Task Vlc_endpoint_sets_and_reads_audio_delay_with_the_password()
    {
        using var vlc = new FakeVlc("s3cret", delaySeconds: 0.02);
        var endpoint = new VlcEndpoint(Environment.ProcessId, Local(vlc.Port), "s3cret"); // real port-owner check

        Assert.Equal(new PlayerReading(20, "3"), await endpoint.ReadAsync(CancellationToken.None));
        await endpoint.WriteAsync(-236, CancellationToken.None);
        Assert.Equal(-0.236, vlc.Delay, 9);
        Assert.Equal(new PlayerReading(-236, "3"), await endpoint.ReadAsync(CancellationToken.None));

        var requests = vlc.Requests.ToArray();
        Assert.Equal("GET /requests/status.xml HTTP/1.1", requests[0].RequestLine);
        Assert.Equal("GET /requests/status.xml?command=audiodelay&val=-0.236 HTTP/1.1", requests[1].RequestLine);
        Assert.All(requests, r => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":s3cret")), r.Authorization));
    }

    [Fact]
    public async Task Vlc_without_a_password_or_with_the_wrong_one_is_reported()
    {
        using var noPassword = new FakeVlc("");
        var e1 = new VlcEndpoint(Environment.ProcessId, Local(noPassword.Port), "anything");
        Assert.Equal(PlayerProblem.NoPassword, (await Assert.ThrowsAsync<PlayerAccessException>(() => e1.ReadAsync(CancellationToken.None))).Problem);

        using var vlc = new FakeVlc("right");
        var e2 = new VlcEndpoint(Environment.ProcessId, Local(vlc.Port), "wrong");
        Assert.Equal(PlayerProblem.LoginFailed, (await Assert.ThrowsAsync<PlayerAccessException>(() => e2.ReadAsync(CancellationToken.None))).Problem);
        Assert.Equal(0, vlc.Delay);
    }

    [Fact]
    public async Task Vlc_endpoint_sends_nothing_to_a_port_another_process_owns()
    {
        using var vlc = new FakeVlc("s3cret");
        var endpoint = new VlcEndpoint(Environment.ProcessId + 1, Local(vlc.Port), "s3cret");
        await Assert.ThrowsAsync<IOException>(() => endpoint.WriteAsync(-236, CancellationToken.None));
        await Task.Delay(100);
        Assert.Empty(vlc.Requests);
    }

    [Fact]
    public async Task Stopped_vlc_waits_through_the_state_machine()
    {
        using var vlc = new FakeVlc("pw", delaySeconds: 0, playing: false);
        var sync = new PlayerSync(new FixedScanner(new VlcEndpoint(Environment.ProcessId, Local(vlc.Port), "pw")));
        await sync.StepAsync(new PlayerSyncInput(true, StreamState.Streaming, 236), CancellationToken.None);
        Assert.Equal(PlayerState.Waiting, sync.Statuses.Single().State);
        Assert.DoesNotContain(vlc.Requests, r => r.RequestLine.Contains("command="));

        vlc.Playing = true;
        await sync.StepAsync(new PlayerSyncInput(true, StreamState.Streaming, 236), CancellationToken.None);
        Assert.Equal(-0.236, vlc.Delay, 9);
        await sync.StepAsync(new PlayerSyncInput(false, StreamState.Streaming, 236), CancellationToken.None);
        Assert.Equal(0, vlc.Delay);
    }

    [Fact]
    public void Process_pipe_and_port_lookups_see_this_process()
    {
        int me = Environment.ProcessId;
        Assert.True(PlayerWin32.IsRunning(me));
        Assert.False(PlayerWin32.IsRunning(0));
        Assert.EndsWith(".exe", PlayerWin32.ImagePath(me), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(PlayerWin32.CommandLine(me)));
        Assert.Equal(new[] { "mpv.exe", "--input-ipc-server=a b", "c" }, PlayerWin32.SplitArgs("mpv.exe \"--input-ipc-server=a b\" c"));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Equal(me, PlayerWin32.ListenerOwner(port));
        Assert.Contains(PlayerWin32.TcpListeners(), l => l.Pid == me && l.EndPoint.Port == port && l.EndPoint.Address.Equals(IPAddress.Loopback));

        var name = "hpc-test-pipe-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.Contains(name, PlayerWin32.PipeNames());
        var accept = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(1000);
        Assert.Equal(me, PlayerWin32.PipeServerProcessId(client.SafePipeHandle));
    }

    [Fact]
    public async Task Scanner_runs_on_this_machine()
    {
        // Whatever players happen to run here: the scan must not throw, and only reports supported ones.
        var scan = await new PlayerScanner().ScanAsync(CancellationToken.None);
        Assert.All(scan.Endpoints, e => Assert.Contains(e.Kind, new[] { PlayerKind.Mpv, PlayerKind.Vlc }));
    }
}
