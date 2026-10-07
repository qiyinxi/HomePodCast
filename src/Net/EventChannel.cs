using System.Net;
using System.Net.Sockets;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// Reverse channel the speaker uses to push requests (e.g. POST /command updateInfo).
/// Every request must be acknowledged with 200 OK or the speaker may drop the session.
/// </summary>
public sealed class EventChannel : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly HapSession _session;
    private readonly Thread _thread;
    private volatile bool _closed;

    public event Action<string>? Closed;

    private EventChannel(TcpClient tcp, HapSession session)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _session = session;
        _thread = new Thread(Run) { IsBackground = true, Name = "AirPlay events" };
    }

    public static async Task<EventChannel> ConnectAsync(IPAddress host, int port, byte[] sharedSecret, CancellationToken ct)
    {
        // Note the swap: the receiver writes with the "Write" key, so we read with it.
        var outKey = HapKeys.Derive(sharedSecret, "Events-Salt", "Events-Read-Encryption-Key");
        var inKey = HapKeys.Derive(sharedSecret, "Events-Salt", "Events-Write-Encryption-Key");

        for (int attempt = 1; ; attempt++)
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(host, port, ct);
                var ch = new EventChannel(tcp, new HapSession(outKey, inKey));
                ch._thread.Start();
                return ch;
            }
            catch (SocketException) when (attempt < 5)
            {
                tcp.Dispose();
                await Task.Delay(500, ct); // the listener sometimes isn't up yet right after SETUP
            }
        }
    }

    private void Run()
    {
        var buf = new byte[8192];
        var plain = new List<byte>();
        string reason = "event channel closed";
        try
        {
            while (!_closed)
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n == 0) break;
                plain.AddRange(_session.Decrypt(buf.AsSpan(0, n)));
                while (HttpMessage.TryParse(plain) is { } req)
                {
                    var headers = new List<KeyValuePair<string, string>>
                    {
                        new("Content-Length", "0"),
                        new("Audio-Latency", "0"),
                    };
                    if (req.Headers.TryGetValue("Server", out var server)) headers.Add(new("Server", server));
                    if (req.Headers.TryGetValue("CSeq", out var cseq)) headers.Add(new("CSeq", cseq));
                    var proto = req.StartLine.Split(' ').LastOrDefault() ?? "RTSP/1.0";
                    var resp = HttpMessage.Format($"{proto} 200 OK", headers, null);
                    _stream.Write(_session.Encrypt(resp));
                    Log.Debug($"event: {req.StartLine} ({req.Body.Length} bytes)");
                }
            }
        }
        catch (Exception ex) when (!_closed)
        {
            reason = $"event channel error: {ex.Message}";
        }
        catch
        {
            return;
        }
        if (!_closed) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _closed = true;
        try { _tcp.Close(); } catch { }
        _session.Dispose();
    }
}
