using System.Diagnostics;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>
/// Keeps a speaker group (experimental stereo pair / multi-room) streaming: connect every member, stream
/// until any one of them is lost, tear all of them down, back off, reconnect all of them. Mirrors the
/// single-speaker loop in StreamController.RunAsync, including backing off for good when a speaker ends
/// a healthy session (another sender took over).
/// </summary>
internal sealed class GroupRunner(
    string name,
    Func<CancellationToken, Task<SpeakerGroup>> connect,
    Action<StreamState, string> status,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? takeoverGrace = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeSpan _takeoverGrace = takeoverGrace ?? TimeSpan.FromSeconds(15);
    private SpeakerGroup? _current;

    public SpeakerGroup? Current => Volatile.Read(ref _current);

    public event Action? FirewallBlocked;

    /// <summary>
    /// Raised on the loop once a newly connected group is <see cref="Current"/>: from then on volume changes reach it,
    /// so this is where one made while it was connecting is sent again.
    /// </summary>
    public event Action<SpeakerGroup>? Connected;

    /// <summary>Runs until cancelled; returns true if it gave up because another sender took a speaker over.</summary>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            bool streamed = false;
            string? lostReason = null, lostText = null;
            try
            {
                status(StreamState.Connecting, attempt == 0 ? L.T("正在连接…") : L.F("正在重连（第 {0} 次）…", attempt));
                var group = await connect(ct);
                Volatile.Write(ref _current, group);
                ct.ThrowIfCancellationRequested();
                Connected?.Invoke(group);
                attempt = 0;
                streamed = true;
                status(StreamState.Streaming, L.F("已连接 · {0}（实验性）", name));
                using (new System.Threading.Timer(_ => group.LogStats(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)))
                    lostText = await group.Lost.WaitAsync(ct);
                lostReason = group.LostReason;
                group.LogStats();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lostText = Describe(ex);
                Log.Warn($"group: {lostText}");
                if (Find<FirewallBlockedException>(ex) != null) FirewallBlocked?.Invoke();
            }
            finally
            {
                TearDown();
            }

            if (ct.IsCancellationRequested) break;

            if (streamed && started.Elapsed > _takeoverGrace && lostReason == EventChannel.ClosedBySpeaker)
            {
                // A speaker ended a healthy session: most likely someone AirPlayed to it. Don't fight back.
                status(StreamState.Idle, L.T(StreamController.TakenOverText));
                return true;
            }

            attempt++;
            var wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            status(StreamState.Retrying, L.F("{0}，{1:F0} 秒后重试", lostText, wait.TotalSeconds));
            try { await _delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    /// <summary>Ends the current group, if any (also used by StreamController.Stop).</summary>
    public void TearDown() => Interlocked.Exchange(ref _current, null)?.Dispose();

    internal static string Describe(Exception ex) => ex switch
    {
        GroupMemberException g => L.F("{0}：{1}", g.Member, Describe(g.InnerException!)),
        AirPlayException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex is T match) return match;
        return null;
    }
}
