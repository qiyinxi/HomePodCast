using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace HomePodCast.Net;

/// <summary>Answers the speaker's NTP-style timing requests (0xD2) with our media clock (0xD3).</summary>
public sealed class TimingServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Thread _thread;
    private volatile bool _closed;

    public int Port { get; }
    public int RequestsAnswered { get; private set; }

    public TimingServer(IPAddress localIp)
    {
        _udp = new UdpClient(new IPEndPoint(localIp, 0));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _thread = new Thread(Run) { IsBackground = true, Name = "AirPlay timing", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var reply = new byte[32];
        while (!_closed)
        {
            try
            {
                IPEndPoint? from = null;
                var req = _udp.Receive(ref from);
                ulong now = MediaClock.NtpNow();
                if (req.Length < 32 || (req[1] & 0x7F) != 0x52) continue;

                reply[0] = req[0];
                reply[1] = 0xD3;
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 7);
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 0);
                req.AsSpan(24, 8).CopyTo(reply.AsSpan(8));             // reference = their send time
                BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), now); // receive time
                BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), MediaClock.NtpNow()); // send time
                _udp.Send(reply, reply.Length, from);
                RequestsAnswered++;
            }
            catch (SocketException) when (!_closed) { }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
        }
    }

    public void Dispose()
    {
        _closed = true;
        _udp.Dispose();
    }
}
