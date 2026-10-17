using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class PtpRealtimeLifecycleTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private sealed class Fixture : IDisposable
    {
        public UdpClient Audio { get; } = Socket();
        private readonly UdpClient _controlPeer = Socket(), _control = Socket(), _events = Socket(), _general = Socket();
        private readonly PtpMaster _clock;
        public PtpRealtimeSender Sender { get; }

        public Fixture()
        {
            _clock = new(IPAddress.Loopback, Endpoint(_events), Endpoint(_general), true, 0, 0);
            Sender = new(IPAddress.Loopback, IPAddress.Loopback, Endpoint(Audio).Port, Endpoint(_controlPeer).Port,
                _control, new byte[32], 123, 250, false, new AudioFifo(44100, 0, 999), null, _clock);
        }

        private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
        private static IPEndPoint Endpoint(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
        public void Dispose()
        {
            Sender.Dispose(); _clock.Dispose(); Audio.Dispose(); _controlPeer.Dispose(); _control.Dispose();
            _events.Dispose(); _general.Dispose();
        }
    }

    private sealed class Barrier : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        public void Hold()
        {
            Entered.TrySetResult();
            if (!_release.Wait(Guard)) throw new TimeoutException("The lifecycle test did not release its barrier");
        }
        public void Release() => _release.Set();
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    private static Task<T> Dedicated<T>(Func<T> work) => Task.Factory.StartNew(work, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static T Field<T>(PtpRealtimeSender sender, string name) =>
        (T)typeof(PtpRealtimeSender).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;

    private static bool TokenDisposed(PtpRealtimeSender sender)
    {
        try { _ = Field<CancellationTokenSource>(sender, "_stop").Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(5, guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_between_thread_publication_and_start_prevents_both_workers_starting(bool dispose)
    {
        using var fixture = new Fixture();
        using var barrier = new Barrier();
        var sender = fixture.Sender;
        var initialResends = Field<Task>(sender, "_resends");
        sender.BeforeSenderStartForTest = barrier.Hold;
        int reads = 0;
        var starting = Dedicated(() => Record.Exception(() => sender.Start(MediaClock.Now + MediaClock.FromMs(250),
            (_, audio) => { Interlocked.Increment(ref reads); audio.Clear(); return true; })));
        await barrier.Entered.Task.WaitAsync(Guard);
        try
        {
            var thread = Field<Thread>(sender, "_sender");
            Assert.True((thread.ThreadState & ThreadState.Unstarted) != 0);
            // This must finish while Start is still held. The old Dispose joined this unstarted thread and threw
            // ThreadStateException; old Stop returned, but Start then launched both workers on closed resources.
            var cleanup = Dedicated(() => Record.Exception(dispose ? sender.Dispose : sender.StopSending));
            Assert.Null(await cleanup.WaitAsync(Guard));
            Assert.Equal(0, sender.StartedAt);
            Assert.Same(initialResends, Field<Task>(sender, "_resends"));
        }
        finally { barrier.Release(); }
        var error = await starting.WaitAsync(Guard);
        if (dispose) Assert.IsType<ObjectDisposedException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, reads);
        Assert.Equal(0, sender.StartedAt);
        Assert.Equal(0, sender.PacketsSent);
        Assert.Equal(0, fixture.Audio.Available);
        sender.StopSending(); sender.Dispose(); sender.Dispose();
        Assert.True(TokenDisposed(sender));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_before_start_never_launches_a_worker(bool dispose)
    {
        using var fixture = new Fixture();
        var sender = fixture.Sender;
        var initialResends = Field<Task>(sender, "_resends");
        if (dispose) sender.Dispose(); else sender.StopSending();
        if (dispose) Assert.Throws<ObjectDisposedException>(() => sender.Start());
        else Assert.Throws<InvalidOperationException>(() => sender.Start());
        Assert.Null(Field<Thread?>(sender, "_sender"));
        Assert.Same(initialResends, Field<Task>(sender, "_resends"));
        Assert.Equal(0, sender.StartedAt);
        Assert.Equal(0, sender.PacketsSent);
        sender.StopSending(); sender.Dispose(); sender.Dispose();
        Assert.True(TokenDisposed(sender));
    }

    [Fact]
    public async Task Concurrent_dispose_retains_the_token_until_the_first_Stop_has_finished_cancelling()
    {
        using var fixture = new Fixture();
        using var barrier = new Barrier();
        var sender = fixture.Sender;
        sender.BeforeStopCancellationForTest = barrier.Hold;
        var stopping = Dedicated(() => Record.Exception(sender.StopSending));
        await barrier.Entered.Task.WaitAsync(Guard);
        try
        {
            // There are no workers to wait for. A second Stop/Dispose must return without freeing the token that
            // the held Stop is about to cancel. The former Interlocked guard let Dispose free it in this window.
            Assert.Null(await Dedicated(() => Record.Exception(sender.Dispose)).WaitAsync(Guard));
            Assert.False(TokenDisposed(sender));
        }
        finally { barrier.Release(); }
        Assert.Null(await stopping.WaitAsync(Guard));
        Assert.True(TokenDisposed(sender));
        sender.StopSending(); sender.Dispose();
    }

    [Fact]
    public async Task A_reader_can_dispose_its_own_sender_without_joining_itself_or_freeing_live_resources()
    {
        using var fixture = new Fixture();
        var sender = fixture.Sender;
        var returned = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<Exception>();
        sender.Failed += failures.Enqueue;
        sender.Start(MediaClock.Now + MediaClock.FromMs(250), (_, audio) =>
        {
            var error = Record.Exception(() =>
            {
                sender.Dispose(); sender.StopSending(); sender.Dispose();
                Assert.False(TokenDisposed(sender));
            });
            audio.Clear(); returned.TrySetResult(error); return true;
        });
        Assert.Null(await returned.Task.WaitAsync(Guard));
        await Until(() => TokenDisposed(sender));
        Assert.Empty(failures);
        Assert.Equal(0, sender.PacketsSent);
        Assert.Equal(0, fixture.Audio.Available);
    }

    [Fact]
    public async Task A_reader_held_past_Dispose_keeps_its_resources_then_releases_them_on_exit()
    {
        using var fixture = new Fixture();
        using var barrier = new Barrier();
        var sender = fixture.Sender;
        sender.Start(MediaClock.Now + MediaClock.FromMs(250), (_, audio) =>
        {
            barrier.Hold(); audio.Clear(); return true;
        });
        await barrier.Entered.Task.WaitAsync(Guard);
        try
        {
            Assert.Null(await Dedicated(() => Record.Exception(sender.Dispose)).WaitAsync(Guard));
            Assert.False(TokenDisposed(sender));
            sender.StopSending(); sender.Dispose();
        }
        finally { barrier.Release(); }
        // The old bounded Join protected a stuck reader, but leaked its token/cipher forever after it returned.
        await Until(() => TokenDisposed(sender));
        Assert.Equal(0, sender.PacketsSent);
        Assert.Equal(0, fixture.Audio.Available);
    }
}
