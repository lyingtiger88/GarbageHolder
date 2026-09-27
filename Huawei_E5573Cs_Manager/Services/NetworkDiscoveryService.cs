using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ModemManagerNative.Models;

namespace ModemManagerNative.Services;

public sealed class NetworkDiscoveryService
{
    public async Task<NetworkInfo> GetNetworkInfoAsync()
    {
        string? gateway = null;
        string? localIp = null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                 n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses
                .Select(x => x.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));

            var ip = props.UnicastAddresses
                .Select(x => x.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));

            if (gw is not null)
            {
                gateway = gw.ToString();
                localIp = ip?.ToString();
                break;
            }
        }

        var ssid = await GetCurrentSsidAsync();
        return new NetworkInfo(gateway, localIp, ssid);
    }

    public async Task<PingResult> PingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success
                ? new PingResult(true, reply.RoundtripTime, null)
                : new PingResult(false, null, reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return new PingResult(false, null, ex.Message);
        }
    }

    private static async Task<string?> GetCurrentSsidAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = trimmed.IndexOf(':');
                    if (idx >= 0) return trimmed[(idx + 1)..].Trim();
                }
            }
        }
        catch
        {
            // SSID is optional; network discovery should continue even if netsh is unavailable.
        }
        return null;
    }
}
