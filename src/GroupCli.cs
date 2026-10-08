using System.Diagnostics;
using System.Globalization;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>
/// <c>group --hosts IP1,IP2 …</c>: stream to several speakers on one timeline (experimental stereo pair /
/// multi-room) without the GUI, printing per-speaker counters once a second — for checking real devices.
/// With --split the first host gets the left channel (the second one with --swap).
/// </summary>
internal static class GroupCli
{
    public const string Usage =
        "  group --hosts IP1,IP2 [--latency MS] [--seconds N] [--volume PCT] [--offsets A,B] [--split] [--swap] [--tone] [--verbose]";

    public static async Task<int> Run(string[] args)
    {
        var hosts = (Opt(args, "--hosts") ?? throw new ArgumentException("--hosts IP1,IP2 required"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IPAddress.Parse).ToList();
        if (hosts.Count < 2) throw new ArgumentException("--hosts needs at least two addresses");
        int latency = int.Parse(Opt(args, "--latency") ?? "250");
        int seconds = int.Parse(Opt(args, "--seconds") ?? "15");
        double? volume = Opt(args, "--volume") is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;
        var offsets = (Opt(args, "--offsets") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        var plan = new GroupPlan(GroupKind.MultiRoom, "cli", null, [], args.Contains("--split"), args.Contains("--swap"),
            new Dictionary<string, int>());
        bool tone = args.Contains("--tone");

        var fifo = new AudioFifo(RtpSender.SampleRate, targetMs: 20, capMs: 60);
        using var capture = tone ? null : new LoopbackCapture(fifo, RtpSender.SampleRate);
        using var toneGen = tone ? new ToneGenerator(fifo, int.Parse(Opt(args, "--beeps") ?? "1")) : null;
        capture?.Start();
        toneGen?.Start();

        var options = new StreamOptions(latency, null);
        var setups = hosts.Select((host, i) => new MemberSetup(host.ToString(),
            async ct => (IGroupMember)await AirPlayClient.PrepareAsync(host, 7000, options, plan.ChannelsFor(i), ct),
            i < offsets.Count ? offsets[i] : 0)).ToList();

        var sw = Stopwatch.StartNew();
        using var group = await SpeakerGroup.ConnectAsync(setups, fifo, volume, CancellationToken.None);
        Log.Info($"group streaming after {sw.ElapsedMilliseconds} ms setup; " + string.Join(", ", group.Members.Select((m, i) =>
            $"{setups[i].Label}: {m!.Name} a2r={m.ArrivalToRenderMs}ms {group.Sender!.Streams[i].Channels}")));

        var s = group.Sender!;
        for (int t = 1; t <= seconds && !group.Lost.IsCompleted; t++)
        {
            await Task.WhenAny(Task.Delay(1000), group.Lost);
            var legs = string.Join(" | ", group.Members.Select((m, i) =>
                $"{setups[i].Label} ntp={(m as AirPlayClient)?.TimingRequests} rtx={s.Streams[i].RetransmitRequests}/{s.Streams[i].Retransmitted}"));
            Log.Info($"t={t,3}s fifo={fifo.Depth * 1000.0 / RtpSender.SampleRate,5:F1}ms under={fifo.Underruns} " +
                     $"sent={s.PacketsSent} late={s.LateWakeups} maxLate={s.MaxLateMs:F1}ms skip={s.SkippedPackets} | {legs}");
        }
        if (group.Lost.IsCompleted) Log.Warn($"stopped: {group.Lost.Result}");
        return 0;
    }

    private static string? Opt(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
