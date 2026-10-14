using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HomePodCast.Net;

public sealed record AirPlayDevice(string Name, string DeviceId, IPAddress Address, int Port, string Model,
    IReadOnlyDictionary<string, string> Txt)
{
    public bool HasAirPlay { get; init; } = true;
    public int? RaopPort { get; init; }
    public IReadOnlyDictionary<string, string>? RaopTxt { get; init; }
    public ReceiverKind Kind => ReceiverKinds.Classify(Model, Txt, HasAirPlay, RaopPort != null);
    public override string ToString() => $"{Name} ({Model}, {Kind}) {Address}:{Port} [{DeviceId}]";
}

/// <summary>Tiny mDNS browser for AirPlay and RAOP (no Bonjour dependency).</summary>
public static class Mdns
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    internal const string Service = "_airplay._tcp.local";
    internal const string RaopService = "_raop._tcp.local";

    public static Task<List<AirPlayDevice>> BrowseAsync(TimeSpan timeout, CancellationToken ct = default) =>
        BrowseNetworkAsync(timeout, LocalIPv4Addresses().Distinct().ToArray(), Group, ct);

    // Loopback and a private multicast port let tests exercise sockets without querying real receivers.
    internal static async Task<List<AirPlayDevice>> BrowseNetworkAsync(TimeSpan timeout, IReadOnlyList<IPAddress> interfaces,
        IPEndPoint group, CancellationToken ct = default)
    {
        var records = new Records();
        var sockets = new List<UdpClient>();
        foreach (var ip in interfaces)
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(ip, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(udp);
            }
            catch (SocketException) { }
        }

        UdpClient? multicast = null;
        try
        {
            multicast = new UdpClient(AddressFamily.InterNetwork);
            multicast.ExclusiveAddressUse = false;
            multicast.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            multicast.Client.Bind(new IPEndPoint(IPAddress.Any, group.Port));
            multicast.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            foreach (var ip in interfaces)
                try { multicast.JoinMulticastGroup(group.Address, ip); } catch (SocketException) { }
        }
        catch (SocketException) { multicast?.Dispose(); multicast = null; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var receivers = sockets.Select(s => ReceiveLoop(s, records, cts.Token)).ToList();
        if (multicast != null) receivers.Add(ReceiveLoop(multicast, records, cts.Token));
        try
        {
            // Ask a couple of times; some responders skip the first unicast-response query.
            for (int i = 0; i < 3 && !cts.IsCancellationRequested; i++)
            {
                foreach (string service in new[] { Service, RaopService })
                {
                    foreach (var s in sockets)
                        try { await s.SendAsync(BuildQuery(service, unicast: true), group, cts.Token); } catch (SocketException) { }
                    if (multicast != null)
                        foreach (var ip in interfaces)
                            try
                            {
                                multicast.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                                await multicast.SendAsync(BuildQuery(service, unicast: false), group, cts.Token);
                            }
                            catch (SocketException) { }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (i + 1)), cts.Token);
            }
            await Task.WhenAll(receivers);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(receivers);
            foreach (var s in sockets) s.Dispose();
            multicast?.Dispose();
        }
        ct.ThrowIfCancellationRequested();
        return records.Devices();
    }

    private static async Task ReceiveLoop(UdpClient udp, Records records, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(ct);
                lock (records) Parse(res.Buffer, records, res.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { } // malformed packet
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);

    internal static byte[] BuildQuery(string name, bool unicast = true)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id, flags, 1 question
        WriteName(ms, name);
        ms.Write(new byte[] { 0, 12, unicast ? (byte)0x80 : (byte)0, 1 }); // PTR, QU or QM
        return ms.ToArray();
    }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            s.WriteByte((byte)b.Length);
            s.Write(b);
        }
        s.WriteByte(0);
    }

    internal sealed class Records
    {
        public readonly HashSet<string> Instances = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Host, int Port)> Srv = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<string, string>> Txt = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, IPAddress> A = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, IPAddress> SourceOf = new(StringComparer.OrdinalIgnoreCase);

        public List<AirPlayDevice> Devices()
        {
            var byId = new Dictionary<string, AirPlayDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in Instances.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!Srv.TryGetValue(inst, out var srv)) continue;
                var txt = Txt.GetValueOrDefault(inst) ?? new Dictionary<string, string>();
                var addr = A.GetValueOrDefault(srv.Host) ?? SourceOf.GetValueOrDefault(inst);
                if (addr == null) continue;
                bool raop = inst.EndsWith("." + RaopService, StringComparison.OrdinalIgnoreCase);
                string service = raop ? RaopService : Service;
                string name = inst.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? inst[..^(service.Length + 1)] : inst;
                int at = raop ? name.IndexOf('@') : -1;
                string id = txt.GetValueOrDefault("deviceid", "");
                if (at > 0)
                {
                    if (id.Length == 0) id = name[..at];
                    name = name[(at + 1)..];
                }
                if (id.Length == 0) id = $"service:{srv.Host}:{name}";
                string key = id.Replace(":", "").Replace("-", "").ToUpperInvariant();
                string model = txt.GetValueOrDefault("model", txt.GetValueOrDefault("am", ""));
                var device = new AirPlayDevice(name, id, addr, srv.Port, model, txt)
                {
                    HasAirPlay = !raop, RaopPort = raop ? srv.Port : null, RaopTxt = raop ? txt : null,
                };
                if (byId.TryGetValue(key, out var other))
                {
                    var air = device.HasAirPlay ? device : other;
                    var legacy = raop ? device : other;
                    var merged = new Dictionary<string, string>(legacy.Txt, StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in air.Txt) merged[k] = v;
                    device = air with
                    {
                        Txt = merged, Model = air.Model.Length > 0 ? air.Model : legacy.Model,
                        HasAirPlay = air.HasAirPlay, RaopPort = legacy.RaopPort, RaopTxt = legacy.RaopTxt,
                    };
                }
                byId[key] = device;
            }
            return byId.Values.OrderBy(d => ReceiverKinds.SortOrder(d.Kind)).ThenBy(d => d.Name).ToList();
        }
    }

    internal static void Parse(byte[] msg, Records rec, IPAddress source)
    {
        if (msg.Length < 12 || (msg[2] & 0x80) == 0) return; // responses only
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
        int rr = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6)) +
                 BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(8)) +
                 BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(10));
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(msg, ref pos); pos += 4; }
        for (int i = 0; i < rr && pos < msg.Length; i++)
        {
            string name = ReadName(msg, ref pos);
            int type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
            int len = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos + 8));
            int data = pos + 10;
            if (data + len > msg.Length) return;
            switch (type)
            {
                case 12 when name.Equals(Service, StringComparison.OrdinalIgnoreCase) || name.Equals(RaopService, StringComparison.OrdinalIgnoreCase): // PTR
                {
                    int p = data;
                    var inst = ReadName(msg, ref p);
                    rec.Instances.Add(inst);
                    rec.SourceOf[inst] = source;
                    break;
                }
                case 33 when len >= 6: // SRV
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(data + 4));
                    int p = data + 6;
                    rec.Srv[name] = (ReadName(msg, ref p), port);
                    if (name.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + RaopService, StringComparison.OrdinalIgnoreCase))
                    {
                        rec.Instances.Add(name);
                        rec.SourceOf[name] = source;
                    }
                    break;
                }
                case 16: // TXT
                    if (!rec.Txt.TryGetValue(name, out var existing)) rec.Txt[name] = existing = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, value) in TxtRecord.Parse(msg, data, len)) existing[key] = value;
                    break;
                case 1 when len == 4: // A
                    rec.A[name] = new IPAddress(msg.AsSpan(data, 4));
                    break;
            }
            pos = data + len;
        }
    }

    private static string ReadName(byte[] msg, ref int pos)
    {
        var labels = new List<string>();
        int p = pos;
        bool jumped = false;
        for (int guard = 0; guard < 128; guard++)
        {
            int len = msg[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int target = (len & 0x3F) << 8 | msg[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(msg, p + 1, len));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return string.Join('.', labels);
    }
}
