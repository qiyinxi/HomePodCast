using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HomePodCast.GuiSmoke;

/// <summary>The real GUI check reads this fixture once; notes and dates need no further request.</summary>
internal sealed class ReleaseFeedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _requests;
    public string Url { get; }
    public int Requests => Volatile.Read(ref _requests);

    public ReleaseFeedServer(string current)
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/releases";
        var entries = new[] { "99.0.0-beta.2", "99.0.0-beta.1", current }.Select(version => new
        {
            tag_name = "v" + version, prerelease = true, published_at = "2026-10-20T08:00:00Z",
            html_url = "https://github.com/qiyinxi/HomePodCast/releases/tag/v" + version,
            body = "## New features\n\nRelease notes fixture " + version + ".\n\n- **Clear updates** with [guide](https://github.com/qiyinxi/HomePodCast/blob/main/README.md).\n- [External text](https://example.com) stays text.\n\n### Fixes\n\n" +
                string.Join("\n\n", Enumerable.Range(1, 10).Select(n => $"- Improvement {n}: this deliberately long announcement checks scrolling and paragraph wrapping without any extra network requests.")) +
                "\n\n## 简体中文\n\n### 新功能\n\n更新说明测试 " + version + "。\n\n- **清晰的更新说明**，含[指南](https://github.com/qiyinxi/HomePodCast/blob/main/README.md)。\n- [外部文字](https://example.com)只显示为文字。\n\n### 修复\n\n" +
                string.Join("\n\n", Enumerable.Range(1, 10).Select(n => $"- 改进 {n}：这份较长的更新说明用于检查窗口内滚动和段落换行，不会再发起联网请求。")),
        });
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries));
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? line;
                    do { line = await reader.ReadLineAsync(_stop.Token); } while (!string.IsNullOrEmpty(line));
                    Interlocked.Increment(ref _requests);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            }
        });
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
}
