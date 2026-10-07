using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HomePodCast.Net;

public sealed record AirPlayDevice(string Name, string DeviceId, IPAddress Address, int Port, string Model,
    IReadOnlyDictionary<string, string> Txt)
{
    public override string ToString() => $"{Name} ({Model}) {Address}:{Port} [{DeviceId}]";
}

/// <summary>Tiny mDNS browser for _airplay._tcp (no Bonjour dependency).</summary>
public static class Mdns
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const string Service = "_airplay._tcp.local";

    public static async Task<List<AirPlayDevice>> BrowseAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var records = new Records();
        var sockets = new List<UdpClient>();
        foreach (var ip in LocalIPv4Addresses())
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

        var query = BuildQuery(Service);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var receivers = sockets.Select(s => ReceiveLoop(s, records, cts.Token)).ToList();
        try
        {
            // Ask a couple of times; some responders skip the first unicast-response query.
            for (int i = 0; i < 3 && !cts.IsCancellationRequested; i++)
            {
                foreach (var s in sockets)
                    try { await s.SendAsync(query, Group, cts.Token); } catch (SocketException) { }
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (i + 1)), cts.Token).ContinueWith(_ => { });
            }
            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
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

    private static byte[] BuildQuery(string name)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id, flags, 1 question
        WriteName(ms, name);
        ms.Write(new byte[] { 0, 12, 0x80, 1 }); // PTR, class IN with unicast-response bit
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

    private sealed class Records
    {
        public readonly HashSet<string> Instances = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Host, int Port)> Srv = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<string, string>> Txt = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, IPAddress> A = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, IPAddress> SourceOf = new(StringComparer.OrdinalIgnoreCase);

        public List<AirPlayDevice> Devices()
        {
            var list = new List<AirPlayDevice>();
            foreach (var inst in Instances)
            {
                if (!Srv.TryGetValue(inst, out var srv)) continue;
                var txt = Txt.GetValueOrDefault(inst) ?? new Dictionary<string, string>();
                var addr = A.GetValueOrDefault(srv.Host) ?? SourceOf.GetValueOrDefault(inst);
                if (addr == null) continue;
                string name = inst.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase)
                    ? inst[..^(Service.Length + 1)] : inst;
                list.Add(new AirPlayDevice(name, txt.GetValueOrDefault("deviceid", ""), addr, srv.Port,
                    txt.GetValueOrDefault("model", ""), txt));
            }
            return list.OrderBy(d => d.Name).ToList();
        }
    }

    private static void Parse(byte[] msg, Records rec, IPAddress source)
    {
        if (msg.Length < 12) return;
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
            switch (type)
            {
                case 12 when name.Equals(Service, StringComparison.OrdinalIgnoreCase): // PTR
                {
                    int p = data;
                    var inst = ReadName(msg, ref p);
                    rec.Instances.Add(inst);
                    rec.SourceOf[inst] = source;
                    break;
                }
                case 33: // SRV
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(data + 4));
                    int p = data + 6;
                    rec.Srv[name] = (ReadName(msg, ref p), port);
                    break;
                }
                case 16: // TXT
                {
                    var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int p = data;
                    while (p < data + len)
                    {
                        int l = msg[p++];
                        var kv = Encoding.UTF8.GetString(msg, p, l);
                        p += l;
                        int eq = kv.IndexOf('=');
                        if (eq > 0) txt[kv[..eq]] = kv[(eq + 1)..];
                    }
                    rec.Txt[name] = txt;
                    break;
                }
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
