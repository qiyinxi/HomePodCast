using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>Connecting the event channel: retries, a limit per attempt, and no attempt's socket left open.</summary>
public class EventChannelTests
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>Every attempt's socket, as the connect was given it.</summary>
    private readonly ConcurrentQueue<Socket> _attempts = new();

    private Func<TcpClient, CancellationToken, ValueTask> Connect(Func<TcpClient, CancellationToken, Task> attempt) => (tcp, ct) =>
    {
        _attempts.Enqueue(tcp.Client);
        return new ValueTask(attempt(tcp, ct));
    };

    private static bool Closed(Socket socket) => socket.SafeHandle.IsClosed;

    [Fact]
    public async Task A_port_that_never_answers_is_given_up_on_after_two_short_attempts()
    {
        // No limit per attempt: a filtered port waited the OS's own connect timeout (about 21 s) five times, while the
        // RTSP session held the speaker. And the last attempt's socket was left to the finalizer.
        var took = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SocketException>(() => EventChannel.ConnectAsync(Secret,
            Connect((_, ct) => Task.Delay(Timeout.Infinite, ct)), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.Equal(SocketError.TimedOut, error.SocketErrorCode);
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"took {took.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(2, _attempts.Count);
        Assert.All(_attempts, socket => Assert.True(Closed(socket)));
    }

    [Fact]
    public async Task A_refused_port_is_tried_five_times_and_the_last_attempt_is_closed_too()
    {
        var error = await Assert.ThrowsAsync<SocketException>(() => EventChannel.ConnectAsync(Secret,
            Connect((_, _) => throw new SocketException((int)SocketError.ConnectionRefused)), TimeSpan.FromSeconds(5),
            CancellationToken.None));
        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        Assert.Equal(5, _attempts.Count);
        Assert.All(_attempts, socket => Assert.True(Closed(socket)));
    }

    [Fact]
    public async Task A_stop_while_connecting_closes_the_socket_and_is_not_retried()
    {
        using var stop = new CancellationTokenSource();
        var connecting = EventChannel.ConnectAsync(Secret, Connect((_, ct) =>
        {
            stop.Cancel(); // 断开 before the speaker answered
            return Task.Delay(Timeout.Infinite, ct);
        }), TimeSpan.FromSeconds(5), stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.True(Closed(Assert.Single(_attempts)));
    }

    [Fact]
    public async Task A_listener_that_is_not_up_yet_is_tried_again()
    {
        // Right after SETUP the speaker's event listener is sometimes not up yet: a refused connect is tried again.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port, tries = 0;
        using var channel = await EventChannel.ConnectAsync(Secret, Connect(async (tcp, ct) =>
        {
            if (++tries < 3) throw new SocketException((int)SocketError.ConnectionRefused);
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        }), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(3, _attempts.Count);
        Assert.Equal(new[] { true, true, false }, _attempts.Select(Closed)); // the third one is the channel's
    }
}
