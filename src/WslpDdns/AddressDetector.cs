using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WslpDdns;

/// <summary>No usable IPv4 address yet; usually the network is still coming up, so worth retrying.</summary>
sealed class NoAddressException(string message) : Exception(message);

static class AddressDetector
{
    // TEST-NET-1 (RFC 5737) is never routed, so "connecting" to it only asks the kernel
    // which source address the default route would use.
    static readonly IPAddress DefaultRouteProbe = IPAddress.Parse("192.0.2.1");

    /// <summary>
    /// Finds the IPv4 address to publish: the configured interface's address, otherwise the
    /// source address the kernel picks to reach the Technitium server (falling back to the default route).
    /// </summary>
    public static async Task<IPAddress> DetectAsync(Config config, CancellationToken ct)
    {
        if (config.Interface is { } name)
            return FromInterface(name);

        foreach (var server in await ResolveServerAsync(config.ServerUrl, ct))
        {
            if (!IPAddress.IsLoopback(server) && SourceAddressFor(server, config.ServerUrl.Port) is { } local)
                return local;
        }

        return SourceAddressFor(DefaultRouteProbe, 53)
            ?? throw new NoAddressException("no routable IPv4 address found (is the network up?); set DDNS_INTERFACE to pick one");
    }

    static async Task<IPAddress[]> ResolveServerAsync(Uri serverUrl, CancellationToken ct) =>
        serverUrl.HostNameType switch
        {
            UriHostNameType.IPv4 => [IPAddress.Parse(serverUrl.Host)],
            UriHostNameType.IPv6 => [],
            _ => await Dns.GetHostAddressesAsync(serverUrl.DnsSafeHost, AddressFamily.InterNetwork, ct),
        };

    static IPAddress? SourceAddressFor(IPAddress remote, int port)
    {
        try
        {
            // Connecting a UDP socket sends nothing; it just binds the socket to the route's source address.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(remote, port);
            var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
            return IsUsable(local) ? local : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    static IPAddress FromInterface(string name)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name)
            ?? throw new ConfigException($"DDNS_INTERFACE: no network interface named '{name}'");

        return nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).FirstOrDefault(IsUsable)
            ?? throw new NoAddressException($"interface {name} has no usable IPv4 address yet");
    }

    static bool IsUsable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && !(bytes[0] == 169 && bytes[1] == 254);
    }
}
