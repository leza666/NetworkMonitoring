using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetworkMonitoring.Core;

/// <summary>IPv4 默认网关检测：优先 GetBestInterface（与系统路由一致），降级遍历网卡</summary>
public static class GatewayDetector
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    public static string? GetDefaultGateway()
    {
        if (GetBestInterface(0, out var bestIdx) == 0)
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var propsByIndex = all
                .Select(ni => (Ni: ni, P: ni.GetIPProperties()))
                .Where(x => x.P is not null)
                .ToList();
            var best = propsByIndex
                .FirstOrDefault(x => x.P!.GetIPv4Properties()?.Index == (int)bestIdx);
            if (best.Ni is not null)
            {
                var gw = FirstIpv4Gateway(best.P!);
                if (gw != null) return gw;
            }
        }

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            if (props is null) continue;
            var gw = FirstIpv4Gateway(props);
            if (gw != null) return gw;
        }
        return null;
    }

    private static string? FirstIpv4Gateway(IPInterfaceProperties props) =>
        props.GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?.ToString();
}