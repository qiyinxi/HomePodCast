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

    /// <summary>
    /// Reason given when the speaker itself ends the session (e.g. another sender took over). Compared as is;
    /// translate (L.T) only for display.
    /// </summary>
    public const string ClosedBySpeaker = "音箱结束了会话";

    public event Action<string>? Closed;

    private EventChannel(TcpClient tcp, HapSession session)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _session = session;
        _thread = new Thread(Run) { IsBackground = true, Name = "AirPlay events" };
    }

    /// <summary>Connect attempts in all; a refused one is tried again after 500 ms.</summary>
    private const int Attempts = 5;

    /// <summary>
    /// Attempts that got no answer at all (a filtered port), of <see cref="Attempts"/>: the RTSP session holds the speaker
    /// while they wait, and a port that dropped one connect usually drops the next.
    /// </summary>
    private const int UnansweredAttempts = 2;

    /// <summary>One connect attempt's limit, as RtspConnection's: without one, a filtered port waited the OS's own (about 21 s).</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    public static Task<EventChannel> ConnectAsync(IPAddress host, int port, byte[] sharedSecret, CancellationToken ct) =>
        ConnectAsync(sharedSecret, (tcp, token) => tcp.ConnectAsync(host, port, token), AttemptTimeout, ct);

    /// <param name="connect">Connects one attempt's client to the event port (tests: one that is refused or never answers).</param>
    internal static async Task<EventChannel> ConnectAsync(byte[] sharedSecret,
        Func<TcpClient, CancellationToken, ValueTask> connect, TimeSpan attemptTimeout, CancellationToken ct)
    {
        // Note the swap: the receiver writes with the "Write" key, so we read with it.
        var outKey = HapKeys.Derive(sharedSecret, "Events-Salt", "Events-Read-Encryption-Key");
        var inKey = HapKeys.Derive(sharedSecret, "Events-Salt", "Events-Write-Encryption-Key");

        for (int attempt = 1, unanswered = 0; ; attempt++)
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(attemptTimeout);
                    await connect(tcp, timeout.Token);
                }
                var ch = new EventChannel(tcp, new HapSession(outKey, inKey));
                ch._thread.Start();
                return ch;
            }
            catch (Exception ex)
            {
                // Every way out but success closes this attempt's socket: a stop while connecting, the last attempt, a
                // failure past the connect. Left to the finalizer, each kept a socket handle open until a collection.
                tcp.Dispose();
                bool timedOut = ex is OperationCanceledException && !ct.IsCancellationRequested;
                if (ex is not SocketException && !timedOut) throw;
                // What the OS's own connect timeout threw, rather than this attempt's cancellation.
                if (timedOut && (++unanswered >= UnansweredAttempts || attempt >= Attempts))
                    throw new SocketException((int)SocketError.TimedOut);
                if (attempt >= Attempts) throw;
                await Task.Delay(500, ct); // the listener sometimes isn't up yet right after SETUP
            }
        }
    }

    private void Run()
    {
        var buf = new byte[8192];
        var plain = new List<byte>();
        string reason = ClosedBySpeaker;
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
            reason = L.F("事件通道异常：{0}", ex.Message);
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
