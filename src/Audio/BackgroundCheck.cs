using System.Diagnostics;
using System.Runtime.Versioning;

namespace HomePodCast.Audio;

/// <summary>
/// A check that runs once per process on its own MTA thread (Media Foundation wants one) and keeps its answer once it has
/// one. A caller waits at most the budget, counted from the probe's start; a probe that hasn't answered by then counts as
/// failed for that caller only. So a probe that is just slow (a cold Media Foundation while Windows signs in) answers
/// a later caller, and one that hangs for good costs only the first caller its wait. A timeout cached as a failure would
/// keep 5.1 off until the app restarts, on a PC whose encoder works.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BackgroundCheck(string name, Func<(bool Ok, string Detail)> probe, TimeSpan budget)
{
    private readonly object _gate = new();
    private Thread? _thread;
    private long _startedAt;
    private bool? _ok;
    private bool _lateLogged;

    /// <summary>True once the probe said yes; false when it said no, threw, or hasn't answered within the budget yet.</summary>
    public bool Passed()
    {
        Thread thread;
        long started;
        lock (_gate)
        {
            if (_ok is { } known) return known;
            if (_thread == null)
            {
                _startedAt = Stopwatch.GetTimestamp();
                _thread = new Thread(Run) { IsBackground = true, Name = name + " check" };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
            thread = _thread;
            started = _startedAt;
        }
        var left = budget - Stopwatch.GetElapsedTime(started);
        if (left > TimeSpan.Zero) thread.Join(left);
        bool firstLate;
        lock (_gate)
        {
            if (_ok is { } answer) return answer;
            firstLate = !_lateLogged;
            _lateLogged = true;
        }
        if (firstLate) Log.Warn($"{name}: no answer within {budget.TotalSeconds:0.#} s; refused for now, a later attempt takes the answer");
        return false;
    }

    private void Run()
    {
        bool ok;
        string detail;
        try { (ok, detail) = probe(); }
        catch (Exception ex) { ok = false; detail = $"{ex.GetType().Name}: {ex.Message}"; }
        lock (_gate) _ok = ok;
        if (ok) Log.Info($"{name}: {detail}");
        else Log.Warn($"{name} unavailable: {detail}");
    }
}
