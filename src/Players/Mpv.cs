using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace HomePodCast.Players;

/// <summary>
/// mpv's JSON IPC (mpv manual, "JSON IPC"; input/ipc-win.c). mpv only listens when started with
/// --input-ipc-server or with input-ipc-server in mpv.conf; on Windows that is a named pipe (a bare name
/// gets the \\.\pipe\ prefix), one instance per client, restricted to the same user. Each request is one
/// line of UTF-8 JSON ending in \n; replies carry "error" and the request_id, events carry "event".
/// The property is audio-delay, in seconds; negative = audio earlier.
/// </summary>
internal static class MpvIpc
{
    /// <summary>The line the settings page suggests for mpv.conf.</summary>
    public const string ConfigLine = @"input-ipc-server=\\.\pipe\mpvsocket";

    private const string PipePrefix = @"\\.\pipe\", PipePrefixLong = @"\\?\pipe\";

    public static string SetDelayRequest(int ms, long requestId) =>
        $"{{\"command\":[\"set_property\",\"audio-delay\",{PlayerDelay.SecondsText(ms)}],\"request_id\":{requestId}}}\n";

    public static string GetDelayRequest(long requestId) =>
        $"{{\"command\":[\"get_property\",\"audio-delay\"],\"request_id\":{requestId}}}\n";

    /// <summary>
    /// One line from mpv: true when it is the reply to <paramref name="requestId"/> (events and replies to
    /// other requests are skipped). <paramref name="data"/> is the number in "data", if any.
    /// </summary>
    public static bool TryParseReply(string line, long requestId, out bool success, out double? data, out string? error)
    {
        success = false;
        data = null;
        error = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("event", out _)) return false;
            if (!root.TryGetProperty("error", out var err)) return false;
            if (root.TryGetProperty("request_id", out var id) && id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt64(out long got) && got != requestId)
                return false;
            error = err.GetString();
            success = error == "success";
            if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Number) data = d.GetDouble();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A pipe name as CreateFile wants it after \\.\pipe\ (mpv adds the prefix to a bare name).</summary>
    public static string? NormalizePipeName(string? value)
    {
        if (value == null) return null;
        var v = value.Trim().Trim('"', '\'');
        if (v.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)) v = v[PipePrefix.Length..];
        else if (v.StartsWith(PipePrefixLong, StringComparison.OrdinalIgnoreCase)) v = v[PipePrefixLong.Length..];
        return v.Length == 0 || v.Contains('\\') ? null : v;
    }

    /// <summary>The pipe from mpv's own arguments: --input-ipc-server=NAME (or "--input-ipc-server NAME").</summary>
    public static string? PipeFromArgs(IReadOnlyList<string> args)
    {
        string? found = null;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var bare = a.TrimStart('-');
            if (a.Length == bare.Length) continue; // not an option
            if (bare.StartsWith("input-ipc-server=", StringComparison.OrdinalIgnoreCase))
                found = bare["input-ipc-server=".Length..];
            else if (bare.Equals("input-ipc-server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                found = args[++i];
        }
        return NormalizePipeName(found); // the last one wins, as in mpv
    }

    /// <summary>Every input-ipc-server value in an mpv.conf (any profile; comments skipped).</summary>
    public static IEnumerable<string> PipesFromConfig(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("--")) line = line[2..];
            int eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("input-ipc-server", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(eq + 1)..].Trim();
            if (!value.StartsWith('"'))
            {
                int hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) value = value[..hash].Trim();
            }
            if (NormalizePipeName(value) is { } name) yield return name;
        }
    }

    /// <summary>Where mpv and mpv.net read mpv.conf for an mpv.exe in <paramref name="exeDir"/>.</summary>
    public static IEnumerable<string> ConfigFiles(string? exeDir)
    {
        if (Environment.GetEnvironmentVariable("MPV_HOME") is { Length: > 0 } home) yield return Path.Combine(home, "mpv.conf");
        if (exeDir != null)
        {
            yield return Path.Combine(exeDir, "portable_config", "mpv.conf");
            yield return Path.Combine(exeDir, "mpv.conf");
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "mpv", "mpv.conf");
        yield return Path.Combine(appData, "mpv.net", "mpv.conf");
    }
}

/// <summary>
/// One running mpv reached through its pipe. Every request opens its own connection and first checks that
/// the pipe's server is still the mpv process found by the scan, so nothing is sent to anyone else.
/// </summary>
internal sealed class MpvEndpoint(string pipeName, int processId, string name) : IPlayerEndpoint
{
    private static long _nextId;
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1500);

    public string PipeName { get; } = pipeName;
    public int ProcessId { get; } = processId;
    public string Key => $"mpv:{ProcessId}:{PipeName}";
    public PlayerKind Kind => PlayerKind.Mpv;
    public string Name { get; } = name;

    public async Task<PlayerReading> ReadAsync(CancellationToken ct)
    {
        var data = await RequestAsync(id => MpvIpc.GetDelayRequest(id), ct).ConfigureAwait(false);
        return new PlayerReading(data is { } s ? PlayerDelay.FromSeconds(s) : null);
    }

    public Task WriteAsync(int delayMs, CancellationToken ct) => RequestAsync(id => MpvIpc.SetDelayRequest(delayMs, id), ct);

    private async Task<double?> RequestAsync(Func<long, string> request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout + ReplyTimeout);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, timeout.Token).ConfigureAwait(false);
        if (PlayerWin32.PipeServerProcessId(pipe.SafePipeHandle) != ProcessId)
            throw new IOException($"pipe {PipeName} is no longer served by process {ProcessId}");

        long id = Interlocked.Increment(ref _nextId);
        var bytes = Encoding.UTF8.GetBytes(request(id));
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (!MpvIpc.TryParseReply(line, id, out bool ok, out double? data, out string? error)) continue;
            if (!ok) throw new IOException($"mpv: {error}");
            return data;
        }
        throw new IOException("mpv closed the pipe");
    }
}

/// <summary>Finds the pipes of running mpv processes.</summary>
internal sealed class MpvFinder
{
    // pipe name -> pid of its server (verified once; re-checked by every request anyway)
    private readonly Dictionary<string, int> _servers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="processes">Running mpv / mpv.net processes: pid → display name.</param>
    public async Task<(List<IPlayerEndpoint> Endpoints, List<PlayerNote> Notes)> FindAsync(
        IReadOnlyDictionary<int, string> processes, CancellationToken ct)
    {
        var endpoints = new List<IPlayerEndpoint>();
        var notes = new List<PlayerNote>();
        if (processes.Count == 0)
        {
            _servers.Clear();
            return (endpoints, notes);
        }

        // Candidate names: each process's own --input-ipc-server, every input-ipc-server in an mpv.conf it
        // may read, and any existing pipe with "mpv" in its name. Only existing pipes are opened.
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exeDirs = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in processes.Keys)
        {
            if (PlayerWin32.CommandLine(pid) is { } cmd && MpvIpc.PipeFromArgs(PlayerWin32.SplitArgs(cmd)) is { } own)
                candidates.Add(own);
            exeDirs.Add(Path.GetDirectoryName(PlayerWin32.ImagePath(pid) ?? ""));
        }
        foreach (var file in exeDirs.SelectMany(d => MpvIpc.ConfigFiles(string.IsNullOrEmpty(d) ? null : d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(file)) candidates.UnionWith(MpvIpc.PipesFromConfig(File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var configured = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var existing = PlayerWin32.PipeNames();
        candidates.UnionWith(existing.Where(n => n.Contains("mpv", StringComparison.OrdinalIgnoreCase)));
        candidates.IntersectWith(existing);

        foreach (var stale in _servers.Keys.Where(k => !candidates.Contains(k)).ToList()) _servers.Remove(stale);
        foreach (var pipe in candidates)
        {
            if (_servers.TryGetValue(pipe, out int known) && (processes.ContainsKey(known) || PlayerWin32.IsRunning(known))) continue;
            _servers[pipe] = await ServerOfAsync(pipe, ct).ConfigureAwait(false);
        }

        var served = new HashSet<int>();
        foreach (var (pipe, pid) in _servers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!processes.TryGetValue(pid, out var name)) continue;
            // mpv.exe only serves JSON IPC pipes; mpv.net may have pipes of its own, so only take the one it was told to open.
            if (name != "mpv" && !configured.Contains(pipe)) continue;
            if (!served.Add(pid)) continue;
            endpoints.Add(new MpvEndpoint(pipe, pid, name));
        }
        foreach (var (pid, name) in processes)
            if (!served.Contains(pid)) notes.Add(new PlayerNote(PlayerKind.Mpv, name, PlayerProblem.NoInterface));
        return (endpoints, notes);
    }

    /// <summary>Connects (nothing is written) to learn which process serves the pipe; 0 if unknown.</summary>
    private static async Task<int> ServerOfAsync(string pipeName, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)MpvEndpoint.ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return PlayerWin32.PipeServerProcessId(pipe.SafePipeHandle);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return 0;
        }
    }
}
