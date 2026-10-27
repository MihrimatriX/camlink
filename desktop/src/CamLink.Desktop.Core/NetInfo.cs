using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CamLink.Desktop.Core;

public sealed record LanAddress(string Address, string InterfaceName, IPAddress? Broadcast, bool IsWifi, int Score);

/// <summary>Bilgisayarın yerel ağ adreslerini bulur; telefonların bağlanacağı adresi seçer.</summary>
public static class NetInfo
{
    // Bağdaştırıcı taraması pahalı (Hyper-V/WSL'li bu makinede 51 bağdaştırıcı, çağrı başına ~25 ms CPU) ve durum (1 Hz)
    // ile duyuru (0,5 Hz) her seferinde tarıyordu. Adres değişince Windows haber verir; olay kaçarsa 30 sn sonra yeniden taranır.
    static volatile IReadOnlyList<LanAddress>? _cache;
    static long _cacheTicks;

    static NetInfo() => NetworkChange.NetworkAddressChanged += (_, _) => _cache = null;

    /// <summary>Puana göre sıralı IPv4 adresleri (en uygun olan ilk sırada). Ağ yoksa boş liste. Sonuç en çok 30 sn önbellekte.</summary>
    public static IReadOnlyList<LanAddress> LanAddresses()
    {
        var c = _cache;
        if (c != null && Environment.TickCount64 - Interlocked.Read(ref _cacheTicks) < 30_000) return c;
        c = Scan();
        Interlocked.Exchange(ref _cacheTicks, Environment.TickCount64);
        return _cache = c;
    }

    static IReadOnlyList<LanAddress> Scan()
    {
        var list = new List<LanAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var name = ni.Name + " " + ni.Description;
            // Sanal makine ve VPN bağdaştırıcıları telefonlardan erişilemez; sona atılır.
            var virtualish = name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || name.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                || name.Contains("docker", StringComparison.OrdinalIgnoreCase);
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                var ip = ua.Address;
                if (ip.ToString().StartsWith("169.254.")) continue;
                var score = Score(ip) + (virtualish ? -50 : 0) + (props.GatewayAddresses.Count > 0 ? 10 : 0);
                list.Add(new LanAddress(ip.ToString(), ni.Name, Broadcast(ip, ua.IPv4Mask), ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, score));
            }
        }
        return list.OrderByDescending(a => a.Score).ToList();
    }

    internal static int Score(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 192 && b[1] == 168) return 30;
        if (b[0] == 10) return 20;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return 15;
        return 0;
    }

    internal static IPAddress? Broadcast(IPAddress ip, IPAddress? mask)
    {
        if (mask == null) return null;
        var a = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (m.All(x => x == 0)) return null;
        var r = new byte[4];
        for (var i = 0; i < 4; i++) r[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(r);
    }

    /// <summary>127.0.0.0/8, ::1 ve ::ffff:127.x.x.x. Uzak adres bilinmiyorsa false.</summary>
    public static bool IsLoopback(IPAddress? remote) =>
        remote != null && (IPAddress.IsLoopback(remote) || (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4())));
}
