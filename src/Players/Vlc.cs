using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace HomePodCast.Players;

/// <summary>
/// VLC's Lua web interface (share/lua/intf/modules/httprequests.lua, VLC 3.0 and 4.0):
/// GET /requests/status.xml?command=audiodelay&amp;val=SECONDS sets the delay of what is playing, and
/// status.xml reports it as &lt;audiodelay&gt; in seconds (negative = audio earlier). The interface is off
/// by default and refuses every request without a password (HTTP Basic, empty user name). VLC 3 starts
/// each new item at its default delay, so status.xml's &lt;currentplid&gt; tells when to set it again.
/// </summary>
internal static class VlcHttp
{
    public const int DefaultPort = 8080;
    public const string StatusPath = "/requests/status.xml";

    public static string SetDelayPath(int ms) => $"{StatusPath}?command=audiodelay&val={PlayerDelay.SecondsText(ms)}";

    public static string BasicAuth(string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password));

    /// <summary>
    /// The delay and the playlist item from status.xml; no &lt;audiodelay&gt; (VLC 3 with nothing open) means
    /// "can't say".
    /// </summary>
    public static PlayerReading ParseStatus(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("empty status.xml");
        int? delay = null;
        if (root.Element("audiodelay")?.Value is { } text &&
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
            double.IsFinite(seconds))
            delay = PlayerDelay.FromSeconds(seconds);
        var item = root.Element("currentplid")?.Value.Trim();
        if (string.Equals(root.Element("state")?.Value.Trim(), "stopped", StringComparison.OrdinalIgnoreCase)) delay = null;
        return new PlayerReading(delay, string.IsNullOrEmpty(item) || item == "-1" ? null : item);
    }
}

/// <summary>The web-interface settings VLC is running with.</summary>
internal sealed record VlcSettings(string? Password, int Port)
{
    public static readonly VlcSettings Default = new(null, VlcHttp.DefaultPort);

    /// <summary>http-password / http-port from a vlcrc (lines starting with # are VLC's commented defaults).</summary>
    public static VlcSettings FromVlcrc(string text, VlcSettings? defaults = null)
    {
        var s = defaults ?? Default;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] is '#' or '[') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];
            if (key == "http-password") s = s with { Password = value };
            else if (key == "http-port" && int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and < 65536)
                s = s with { Port = port };
        }
        return s;
    }

    /// <summary>--http-password / --http-port on VLC's command line override the vlcrc.</summary>
    public VlcSettings WithArgs(IReadOnlyList<string> args)
    {
        var s = this;
        for (int i = 0; i < args.Count; i++)
        {
            if (TryOption(args, ref i, "http-password", out var value)) s = s with { Password = value };
            else if (TryOption(args, ref i, "http-port", out value) &&
                     int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and < 65536)
                s = s with { Port = port };
        }
        return s;
    }

    private static bool TryOption(IReadOnlyList<string> args, ref int i, string name, out string value)
    {
        var a = args[i];
        value = "";
        if (!a.StartsWith("--", StringComparison.Ordinal)) return false;
        var bare = a[2..];
        if (bare.StartsWith(name + "=", StringComparison.Ordinal))
        {
            value = bare[(name.Length + 1)..];
            return true;
        }
        if (bare == name && i + 1 < args.Count)
        {
            value = args[++i];
            return true;
        }
        return false;
    }

    /// <summary>vlcrc of a VLC in <paramref name="exeDir"/>: portable\vlcrc next to it, else %APPDATA%\vlc\vlcrc.</summary>
    public static IEnumerable<string> VlcrcFiles(string? exeDir)
    {
        if (exeDir != null) yield return Path.Combine(exeDir, "portable", "vlcrc");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");
    }
}

/// <summary>
/// One running VLC reached through its web interface on this PC. Before every request the port is checked
/// to still belong to that VLC process, so the password and the command never reach another program.
/// </summary>
internal sealed class VlcEndpoint : IPlayerEndpoint
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false, // localhost only, never through a proxy
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private readonly string _password;

    public VlcEndpoint(int processId, IPEndPoint address, string password)
    {
        ProcessId = processId;
        Address = address;
        _password = password;
    }

    public int ProcessId { get; }
    public IPEndPoint Address { get; }
    public string Key => $"vlc:{ProcessId}:{Address.Port}";
    public PlayerKind Kind => PlayerKind.Vlc;
    public string Name => "VLC";

    /// <summary>Checks that the port is still VLC's (tests replace it).</summary>
    internal Func<int, int, bool> OwnsPort { get; init; } = (pid, port) => PlayerWin32.ListenerOwner(port) == pid;

    public async Task<PlayerReading> ReadAsync(CancellationToken ct) =>
        VlcHttp.ParseStatus(await GetAsync(VlcHttp.StatusPath, ct).ConfigureAwait(false));

    public async Task WriteAsync(int delayMs, CancellationToken ct) =>
        await GetAsync(VlcHttp.SetDelayPath(delayMs), ct).ConfigureAwait(false);

    private async Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        if (!OwnsPort(ProcessId, Address.Port)) throw new IOException($"port {Address.Port} is no longer VLC's ({ProcessId})");
        var host = Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{Address.Address}]" : Address.Address.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{Address.Port}{pathAndQuery}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", VlcHttp.BasicAuth(_password));
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new PlayerAccessException(_password.Length == 0 ? PlayerProblem.NoPassword : PlayerProblem.LoginFailed, "VLC: 401");
            case HttpStatusCode.Forbidden: // VLC 3: "Password for Web interface has not been set."
                throw new PlayerAccessException(PlayerProblem.NoPassword, "VLC: 403");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Finds the web interface of running VLC processes.</summary>
internal static class VlcFinder
{
    public static (List<IPlayerEndpoint> Endpoints, List<PlayerNote> Notes) Find(IReadOnlyCollection<int> processes)
    {
        var endpoints = new List<IPlayerEndpoint>();
        var notes = new List<PlayerNote>();
        if (processes.Count == 0) return (endpoints, notes);

        var listeners = PlayerWin32.TcpListeners();
        foreach (var pid in processes)
        {
            var exeDir = Path.GetDirectoryName(PlayerWin32.ImagePath(pid) ?? "");
            var settings = VlcSettings.Default;
            foreach (var file in VlcSettings.VlcrcFiles(string.IsNullOrEmpty(exeDir) ? null : exeDir))
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    settings = VlcSettings.FromVlcrc(File.ReadAllText(file));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (PlayerWin32.CommandLine(pid) is { } cmd) settings = settings.WithArgs(PlayerWin32.SplitArgs(cmd));

            var mine = listeners.Where(l => l.Pid == pid && l.EndPoint.Port == settings.Port).Select(l => l.EndPoint).ToList();
            if (mine.Count == 0)
            {
                notes.Add(new PlayerNote(PlayerKind.Vlc, "VLC", PlayerProblem.NoInterface));
                continue;
            }
            if (string.IsNullOrEmpty(settings.Password))
            {
                notes.Add(new PlayerNote(PlayerKind.Vlc, "VLC", PlayerProblem.NoPassword));
                continue;
            }
            endpoints.Add(new VlcEndpoint(pid, Loopback(mine), settings.Password));
        }
        return (endpoints, notes);
    }

    /// <summary>The address to connect to: loopback when VLC listens on all addresses or on loopback.</summary>
    internal static IPEndPoint Loopback(IReadOnlyList<IPEndPoint> listening)
    {
        var v4 = listening.FirstOrDefault(e => e.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (v4 != null)
            return v4.Address.Equals(IPAddress.Any) || IPAddress.IsLoopback(v4.Address) ? new IPEndPoint(IPAddress.Loopback, v4.Port) : v4;
        var v6 = listening[0];
        return v6.Address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(v6.Address) ? new IPEndPoint(IPAddress.IPv6Loopback, v6.Port) : v6;
    }
}
