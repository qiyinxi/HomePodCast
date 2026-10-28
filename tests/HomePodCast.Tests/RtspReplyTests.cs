using System.Net;
using System.Net.Sockets;
using System.Text;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>Which reply <see cref="RtspConnection"/> hands to which request, against a scripted speaker on loopback.</summary>
public class RtspReplyTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>The plaintext RTSP side of a speaker: reads requests and writes what the test tells it to.</summary>
    private sealed class ScriptedSpeaker(TcpClient tcp) : IDisposable
    {
        private readonly NetworkStream _stream = tcp.GetStream();
        private readonly List<byte> _rx = [];
        private readonly byte[] _buf = new byte[4096];

        public async Task<HttpMessage> NextRequest(CancellationToken ct)
        {
            HttpMessage? request;
            while ((request = HttpMessage.TryParse(_rx)) == null)
            {
                int n = await _stream.ReadAsync(_buf, ct);
                if (n == 0) throw new IOException("the sender closed the connection");
                _rx.AddRange(_buf.AsSpan(0, n).ToArray());
            }
            return request;
        }

        public Task Reply(string status, string cseq, CancellationToken ct) =>
            _stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 {status}\r\nCSeq: {cseq}\r\nContent-Length: 0\r\n\r\n"), ct).AsTask();

        public void Dispose() => tcp.Dispose();
    }

    [Fact]
    public async Task A_late_reply_to_an_earlier_request_is_not_taken_for_the_next_ones()
    {
        // A request that gave up waiting (5 s receive timeout, connection still up) gets its reply later, in front of the
        // next request's. Taken for that one's, every later request got the reply to the one before it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(Guard);
        var accepted = listener.AcceptTcpClientAsync(guard.Token).AsTask();
        using var rtsp = await RtspConnection.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, guard.Token);
        using var speaker = new ScriptedSpeaker(await accepted);

        var serving = Task.Run(async () =>
        {
            var info = await speaker.NextRequest(guard.Token);
            await speaker.Reply("200 OK", info.Header("CSeq"), guard.Token);
            var feedback = await speaker.NextRequest(guard.Token);
            await speaker.Reply("500 Late", info.Header("CSeq"), guard.Token); // the earlier request's, once more
            await speaker.Reply("200 OK", feedback.Header("CSeq"), guard.Token);
            var volume = await speaker.NextRequest(guard.Token);
            await speaker.Reply("451 Parameter Not Understood", volume.Header("CSeq"), guard.Token);
            return (info.Header("CSeq"), feedback.Header("CSeq"), volume.Header("CSeq"));
        });

        var first = rtsp.Rtsp("GET", "/info");
        var second = rtsp.Rtsp("POST", "/feedback");
        var third = rtsp.Rtsp("SET_PARAMETER", contentType: "text/parameters", body: "volume: -20.000000"u8.ToArray());
        var (info, feedback, volume) = await serving;

        Assert.Equal((200, info), (first.StatusCode, first.Header("CSeq")));
        Assert.Equal((200, feedback), (second.StatusCode, second.Header("CSeq")));
        Assert.Equal((451, volume), (third.StatusCode, third.Header("CSeq")));
    }
}
