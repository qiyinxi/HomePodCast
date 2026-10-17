using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HomePodCast.Net;

/// <summary>Bounded IPv4 SSDP browse. Nothing is persisted between explicit browses.</summary>
internal static class DlnaDiscovery
{
    internal static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private const string SearchTarget = "urn:schemas-upnp-org:device:MediaRenderer:1";
    private const int MaxPacketBytes = 16 * 1024;
    private const int MaxAdvertisements = 128;
    internal enum BrowseOperation { AliveObserved, WithdrawalProcessed, DescriptionCompleted }
    internal sealed record BrowseProgress(Advertisement Advertisement, long Revision, BrowseOperation Operation,
        IReadOnlyList<DlnaDevice> Devices);

    /// <summary>An explicit interface and unicast endpoint let tests avoid the real LAN entirely.</summary>
    internal static async Task<IReadOnlyList<DlnaDevice>> BrowseAsync(TimeSpan duration, CancellationToken ct = default,
        IPAddress? localAddress = null, IPEndPoint? endpoint = null, Func<BrowseProgress, bool>? progress = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        ct.ThrowIfCancellationRequested();
        endpoint ??= MulticastEndpoint;
        if (endpoint.AddressFamily != AddressFamily.InterNetwork || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("SSDP requires an IPv4 endpoint.", nameof(endpoint));
        if (localAddress is not null && localAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("SSDP requires a local IPv4 address.", nameof(localAddress));
        var interfaces = localAddress is not null ? [localAddress] : LocalAddresses();
        var cache = new Cache();
        var sockets = new List<UdpClient>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(duration);
        using var fetchSlots = new SemaphoreSlim(4);
        var fetches = new ConcurrentBag<Task>();
        var pending = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var clients = new Dictionary<IPAddress, HttpClient>();
        int advertisements = 0;
        try
        {
            foreach (var address in interfaces)
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(address, 0));
                    sockets.Add(socket);
                    if (IsMulticast(endpoint.Address))
                    {
                        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    }
                    clients.Add(address, DlnaHttp.CreateClient(address));
                }
                catch (SocketException) { }
            }
            var searchSockets = sockets.ToArray();
            var receivers = searchSockets.Select(socket => ReceiveAsync(socket)).ToList();
            // NOTIFY uses port 1900 rather than the search's ephemeral response port. A best-effort listener
            // exists only during the browse; another UPnP program owning this port must not break discovery.
            if (IsMulticast(endpoint.Address) && sockets.Count > 0)
            {
                UdpClient? notify = null;
                try
                {
                    notify = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
                    notify.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    notify.Client.Bind(new IPEndPoint(interfaces.All(IPAddress.IsLoopback) ? IPAddress.Loopback : IPAddress.Any, endpoint.Port));
                    foreach (var address in clients.Keys) notify.JoinMulticastGroup(endpoint.Address, address);
                    sockets.Add(notify);
                    receivers.Add(ReceiveAsync(notify));
                }
                catch (SocketException) { notify?.Dispose(); }
            }
            byte[] search = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: {endpoint.Address}:{endpoint.Port}\r\n"
                + $"MAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {SearchTarget}\r\n\r\n");
            foreach (var socket in searchSockets)
            {
                try { await socket.SendAsync(search, endpoint, deadline.Token).ConfigureAwait(false); }
                catch (SocketException) { }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            }
            try { await Task.WhenAll(receivers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.WhenAll(fetches).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return cache.Devices(DateTimeOffset.UtcNow);
        }
        finally
        {
            deadline.Cancel();
            foreach (var socket in sockets) socket.Dispose();
            foreach (var client in clients.Values) client.Dispose();
        }

        async Task ReceiveAsync(UdpClient socket)
        {
            try
            {
                while (true)
                {
                    var packet = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                    if (!TryParseAdvertisement(packet.Buffer, packet.RemoteEndPoint.Address, out var advertisement)) continue;
                    if (Interlocked.Increment(ref advertisements) > MaxAdvertisements) continue;
                    if (advertisement.ByeBye || advertisement.Expires <= DateTimeOffset.UtcNow)
                    {
                        cache.Remove(advertisement.Udn, advertisement.Address);
                        if (progress != null)
                        if (progress != null)
                            Report(advertisement, cache.ObserveAlive(advertisement.Udn, advertisement.Address), BrowseOperation.WithdrawalProcessed);
                        continue;
                    }
                    long revision = cache.ObserveAlive(advertisement.Udn, advertisement.Address);
                    string key = $"{advertisement.Address}|{advertisement.Udn}|{advertisement.Location}|{revision}";
                    if (!pending.TryAdd(key, 0))
                    {
                        Report(advertisement, revision, BrowseOperation.AliveObserved);
                        continue;
                    }
                    var bind = ((IPEndPoint)socket.Client.LocalEndPoint!).Address;
                    var client = clients.GetValueOrDefault(bind) ?? clients.Values.FirstOrDefault();
                    if (client is not null) fetches.Add(FetchAsync(client, advertisement, key, revision));
                    Report(advertisement, revision, BrowseOperation.AliveObserved);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) { }
        }

        async Task FetchAsync(HttpClient client, Advertisement advertisement, string key, long revision)
        {
            try
            {
                await fetchSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, advertisement.Location);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return;
                    byte[] xml = await DlnaHttp.ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                    var device = DlnaDevice.ParseDescription(xml, advertisement.Address, advertisement.Location!);
                    if (!string.Equals(device.Udn, advertisement.Udn, StringComparison.OrdinalIgnoreCase)) return;
                    cache.Add(device, advertisement.Expires, revision);
                }
                finally { fetchSlots.Release(); }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or FormatException or OperationCanceledException) { }
            finally
            {
                pending.TryRemove(key, out _);
                Report(advertisement, revision, BrowseOperation.DescriptionCompleted);
            }
        }

        void Report(Advertisement advertisement, long revision, BrowseOperation operation)
        {
            // Tests observe completed cache operations and finish the internal deadline explicitly. The
            // ordinary browse builds no progress records or snapshots, and caller cancellation still throws.
            if (progress == null) return;
            if (progress(new(advertisement, revision, operation, cache.Devices(DateTimeOffset.UtcNow)))) deadline.Cancel();
        }
    }

    private static IPAddress[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(i => i.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Distinct().Take(32).ToArray();

    private static bool IsMulticast(IPAddress address) => address.GetAddressBytes()[0] is >= 224 and <= 239;

    internal sealed record Advertisement(string Udn, IPAddress Address, Uri? Location, DateTimeOffset Expires, bool ByeBye);

    internal static bool TryParseAdvertisement(ReadOnlySpan<byte> packet, IPAddress address, out Advertisement advertisement)
    {
        advertisement = null!;
        if (packet.Length == 0 || packet.Length > MaxPacketBytes || address.AddressFamily != AddressFamily.InterNetwork
            || address.GetAddressBytes()[0] is 0 or >= 224) return false;
        foreach (byte value in packet) if (value > 127 || value == 0) return false;
        var lines = Encoding.ASCII.GetString(packet).Split("\r\n");
        bool notify = lines[0] == "NOTIFY * HTTP/1.1";
        if (!notify && lines[0] != "HTTP/1.1 200 OK") return false;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) return false;
        }
        if (!headers.TryGetValue(notify ? "NT" : "ST", out var target)
            || !DlnaDevice.HasVersion(target, DlnaDevice.MediaRenderer)) return false;
        if (!headers.TryGetValue("USN", out var usn)) return false;
        string udn = usn.Split("::", 2)[0];
        if (!udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) || udn.Length is <= 5 or > 256 || udn.Any(char.IsControl)) return false;
        if (notify && headers.GetValueOrDefault("NTS") == "ssdp:byebye")
        {
            advertisement = new(udn, address, null, DateTimeOffset.MinValue, true);
            return true;
        }
        if (notify && headers.GetValueOrDefault("NTS") != "ssdp:alive") return false;
        if (!headers.TryGetValue("LOCATION", out var location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri)) return false;
        try { DlnaHttp.RequirePeerHttp(uri, address); }
        catch (FormatException) { return false; }
        if (!headers.TryGetValue("CACHE-CONTROL", out var cacheControl)) return false;
        int? seconds = null;
        foreach (string part in cacheControl.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Equals("max-age", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair[1].Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out int value)) seconds = value;
        }
        if (seconds is null or < 0) return false;
        advertisement = new(udn, address, uri, DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds.Value, 86400)), false);
        return true;
    }

    internal sealed class Cache
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (DlnaDevice Device, DateTimeOffset Expires)> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string, IPAddress), long> _revisions = [];

        internal long ObserveAlive(string udn, IPAddress address)
        {
            lock (_gate)
            {
                var key = (udn.ToUpperInvariant(), address);
                return _revisions.GetValueOrDefault(key);
            }
        }

        internal void Add(DlnaDevice device, DateTimeOffset expires, long revision)
        {
            lock (_gate)
            {
                // A description completing after byebye must not resurrect the departed advertisement.
                if (_revisions.GetValueOrDefault((device.Udn.ToUpperInvariant(), device.Address)) != revision) return;
                _devices[device.Udn] = (device, expires);
            }
        }

        internal void Remove(string udn, IPAddress address)
        {
            lock (_gate)
            {
                var key = (udn.ToUpperInvariant(), address);
                _revisions[key] = _revisions.GetValueOrDefault(key) + 1;
                if (_devices.TryGetValue(udn, out var entry) && entry.Device.Address.Equals(address)) _devices.Remove(udn);
            }
        }

        internal IReadOnlyList<DlnaDevice> Devices(DateTimeOffset now)
        {
            lock (_gate) return _devices.Values.Where(e => e.Expires > now).Select(e => e.Device)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
