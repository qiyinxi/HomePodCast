using System.Net;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>
/// <c>remote --host IP</c>: opens the remote-control channel to a paired Apple TV and prints what it says about itself,
/// its audio outputs and what plays, for <c>--seconds</c> (experimental). Without an output option it only reads: no
/// audio session, nothing changed on the Apple TV. <c>--set-outputs</c>, <c>--add-outputs</c> and
/// <c>--remove-outputs</c> (device UIDs from the listing, comma-separated) change the Apple TV's current audio outputs,
/// as its Control Centre does: only with the owner's OK.
/// </summary>
internal static class RemoteCli
{
    public const string Usage =
        "  remote --host IP [--port N] [--seconds N] [--volume-of UID] [--verbose]\n" +
        "         [--set-outputs UID,... | --add-outputs UID,... | --remove-outputs UID,...] [--after N]"; // experimental since 1.4

    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The output change the arguments ask for; null when they only read.</summary>
    internal static (MediaRemote.OutputChange Change, string[] Uids)? OutputChangeOf(string[] args)
    {
        (string Option, MediaRemote.OutputChange Change)[] options =
        [
            ("--set-outputs", MediaRemote.OutputChange.Set),
            ("--add-outputs", MediaRemote.OutputChange.Add),
            ("--remove-outputs", MediaRemote.OutputChange.Remove),
        ];
        var given = options.Where(o => args.Contains(o.Option)).ToList();
        if (given.Count == 0) return null;
        if (given.Count > 1) throw new ArgumentException("one of --set-outputs, --add-outputs, --remove-outputs");
        var uids = (Opt(args, given[0].Option) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (uids.Length == 0 || uids.Any(u => u.StartsWith("--"))) throw new ArgumentException($"{given[0].Option} needs device UIDs");
        return (given[0].Change, uids);
    }

    public static async Task<int> Run(string[] args)
    {
        var host = IPAddress.Parse(Opt(args, "--host") ?? throw new ArgumentException("--host required"));
        int port = int.Parse(Opt(args, "--port") ?? "7000");
        int seconds = int.Parse(Opt(args, "--seconds") ?? "10");
        int after = int.Parse(Opt(args, "--after") ?? "2");
        var change = OutputChangeOf(args);

        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var session = await RemoteControlSession.ConnectAsync(host, port, PairingStore.Default, connectTimeout.Token);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Lost += reason => lost.TrySetResult(reason);
        session.Received += message =>
        {
            foreach (var line in MediaRemote.Describe(message)) Log.Info("<< " + line);
            if (Log.Verbose) Log.Debug("<< raw " + message.Whole.Describe());
        };

        // DeviceInfo first: the Apple TV answers nothing before it.
        var info = await session.RequestAsync(id => MediaRemote.DeviceInfoMessage(id, MediaRemote.NewId(), Environment.MachineName), AnswerTimeout);
        var device = MediaRemote.Device.From(info.Inner);
        Log.Info($"Apple TV {Log.Name(device.Name)}: output UID {device.OutputUid}, {device.Grouped.Count} grouped device(s)");
        session.Send(MediaRemote.SetConnectionStateMessage());
        await session.RequestAsync(id => MediaRemote.ClientUpdatesConfigMessage(id, nowPlaying: true), AnswerTimeout);

        if (Opt(args, "--volume-of") is { } volumeOf)
        {
            try
            {
                await session.RequestAsync(id => MediaRemote.GetVolumeMessage(id, volumeOf), AnswerTimeout);
            }
            catch (TimeoutException)
            {
                Log.Warn($"no answer to GetVolume for {volumeOf}");
            }
        }

        for (int t = 1; t <= seconds && !lost.Task.IsCompleted; t++)
        {
            if (change is { } c && t == after + 1)
            {
                Log.Info($">> ModifyOutputContextRequest {c.Change}: {string.Join(", ", c.Uids)}");
                session.Send(MediaRemote.ModifyOutputContextMessage(c.Change, c.Uids));
            }
            await Task.WhenAny(Task.Delay(1000), lost.Task);
        }
        if (lost.Task.IsCompleted) Log.Warn($"stopped: {lost.Task.Result}");
        return 0;
    }

    private static string? Opt(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
