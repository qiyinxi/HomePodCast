using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HomePodCast;

/// <summary>Windows' network location category: the firewall profile that applies to a network.</summary>
public enum NetworkCategory { Unknown, Public, Private, Domain }

/// <summary>The kind of adapter, which decides the Settings page where the network's profile type is changed.</summary>
public enum AdapterKind { Other, Ethernet, WiFi }

/// <summary>
/// The network the speaker is reached through, as Windows classifies it. HomePodCast's inbound firewall rule
/// (Firewall.RuleScript, installer\HomePodCast.wxs) only applies on Private networks, so on a Public one the speaker's
/// connections back to us (timing, events, resend requests) are blocked even though the rule exists. Seen 2026-10-10:
/// a PC moved from Wi-Fi to Ethernet, and Windows put the new network in Public.
/// Found by asking the stack which local address reaches the speaker, that address's adapter, and the Network List
/// Manager's connection on that adapter. Detection never throws: anything that fails gives <see cref="Unknown"/>.
/// </summary>
/// <param name="Interface">The adapter's name ("以太网", "WLAN"), or null if not found.</param>
/// <param name="Name">The network (profile) name Windows shows ("ASUS_D0"), or null if not known.</param>
public sealed record NetworkProfile(string? Interface, AdapterKind Kind, string? Name, NetworkCategory Category)
{
    public static readonly NetworkProfile Unknown = new(null, AdapterKind.Other, null, NetworkCategory.Unknown);

    /// <summary>The name to show the user: the network's, else the adapter's.</summary>
    public string DisplayName => Name ?? Interface ?? "?";

    /// <summary>The Windows Settings page that has this network's "Network profile type".</summary>
    public string SettingsUri => SettingsUriFor(Kind);

    internal static string SettingsUriFor(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => "ms-settings:network-ethernet",
        AdapterKind.WiFi => "ms-settings:network-wifi",
        _ => "ms-settings:network-status",
    };

    internal static AdapterKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
        _ => AdapterKind.Other,
    };

    /// <summary>For the log: <c>以太网 (Ethernet), profile "ASUS_D0" is Public</c>.</summary>
    public override string ToString()
    {
        string kind = Kind switch { AdapterKind.WiFi => "Wi-Fi", AdapterKind.Ethernet => "Ethernet", _ => "other" };
        string where = $"{Interface ?? "?"} ({kind})";
        if (Category == NetworkCategory.Unknown) return $"{where}, profile unknown";
        return Name == null ? $"{where}, profile is {Category}" : $"{where}, profile \"{Name}\" is {Category}";
    }

    /// <summary>NLM_NETWORK_CATEGORY: 0 public, 1 private, 2 domain authenticated.</summary>
    internal static NetworkCategory FromNlm(int category) => category switch
    {
        0 => NetworkCategory.Public,
        1 => NetworkCategory.Private,
        2 => NetworkCategory.Domain,
        _ => NetworkCategory.Unknown,
    };

    /// <summary>
    /// The firewall's active profiles (NET_FW_PROFILE2: 1 domain, 2 private, 4 public), when the Network List Manager
    /// can't be asked. Only a single active profile says anything about the speaker's network: with two (another
    /// adapter, such as Hyper-V's "Default Switch", sits in Public) there is no telling which one is ours.
    /// </summary>
    internal static NetworkCategory FromProfileTypes(int types) => types switch
    {
        1 => NetworkCategory.Domain,
        2 => NetworkCategory.Private,
        4 => NetworkCategory.Public,
        _ => NetworkCategory.Unknown,
    };

    /// <summary>
    /// The network that reaches <paramref name="speaker"/>, or the default route's when the speaker's address isn't
    /// known yet. Blocks for a few ms (COM); call it off the UI thread.
    /// </summary>
    public static NetworkProfile ForSpeaker(IPAddress? speaker)
    {
        try
        {
            // Connecting a UDP socket sends nothing: the stack only picks the route, and with it our local address.
            // No speaker yet: any off-link address (TEST-NET-1) goes the default route's way.
            var target = speaker ?? IPAddress.Parse("192.0.2.1");
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 7000));
            return ForLocalAddress(((IPEndPoint)socket.LocalEndPoint!).Address);
        }
        catch (Exception ex)
        {
            Warn($"network: no route to {speaker?.ToString() ?? "the default gateway"} ({ex.Message}); profile unknown");
            return Unknown;
        }
    }

    /// <summary>The network of the adapter that has <paramref name="local"/> (the address a connection to the speaker uses).</summary>
    public static NetworkProfile ForLocalAddress(IPAddress local)
    {
        try
        {
            if (IPAddress.IsLoopback(local)) return Unknown;
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));
            if (nic == null)
            {
                Warn($"network: no adapter has {local}; profile unknown");
                return Unknown;
            }
            var kind = KindOf(nic.NetworkInterfaceType);
            string? name = null;
            NetworkCategory category;
            try
            {
                (name, category) = FromNetworkList(Guid.Parse(nic.Id));
            }
            catch (Exception ex)
            {
                category = FromProfileTypes(Firewall.CurrentProfileTypes());
                Warn($"network: Network List Manager unavailable ({ex.Message}); firewall profile says {category}");
            }
            return new NetworkProfile(nic.Name, kind, name, category);
        }
        catch (Exception ex)
        {
            Warn($"network: profile of {local} unknown ({ex.Message})");
            return Unknown;
        }
    }

    /// <summary>Name and category of the network on this adapter; (null, Unknown) when the adapter has none (Hyper-V switches, VPNs).</summary>
    private static (string? Name, NetworkCategory Category) FromNetworkList(Guid adapter)
    {
        var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: true)!;
        var manager = (INetworkListManager)Activator.CreateInstance(type)!;
        var connections = manager.GetNetworkConnections();
        var one = new INetworkConnection[1];
        while (connections.Next(1, one, out uint fetched) == 0 && fetched == 1)
        {
            if (one[0].GetAdapterId() != adapter) continue;
            var network = one[0].GetNetwork();
            return (network.GetName(), FromNlm(network.GetCategory()));
        }
        return (null, NetworkCategory.Unknown);
    }

    private static string? _lastWarning;

    /// <summary>A failing lookup fails the same way on every retry: log it once, until something else happens.</summary>
    private static void Warn(string message)
    {
        if (Interlocked.Exchange(ref _lastWarning, message) != message) Log.Warn(message);
    }

    // netlistmgr.h. Dual interfaces: the IDispatch slots come first, then these, in this order. Declared only up to the
    // last method used. (Late-bound IDispatch can't return GetAdapterId's GUID, so not `dynamic` like Firewall.)

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object NewEnum();

        [PreserveSig]
        int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] connections, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkConnection
    {
        INetwork GetNetwork();
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        IEnumNetworkConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        int GetCategory();
    }
}
