using System;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services;

namespace NetVerify;

internal static class NetReadSmoke
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        try
        {
            var list = await NetAdapterService.ListAsync().ConfigureAwait(false);
            check(list.Count >= 1, $"smoke lists adapters ({list.Count} found)");
            var realtek = list.FirstOrDefault(a => a.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase));
            check(realtek is not null, "smoke finds the Realtek NIC");
            if (realtek is not null)
            {
                check(!string.IsNullOrEmpty(realtek.RegistryKey), $"smoke matches registry key ({realtek.RegistryKey})");
                var props = await NetAdapterService.GetAdvancedAsync(realtek.RegistryKey).ConfigureAwait(false);
                check(props.Count >= 1, $"smoke reads advanced props ({props.Count})");
                var speed = props.FirstOrDefault(p => p.Keyword == "*SpeedDuplex");
                check(speed is not null && speed.Options.Count >= 5, "smoke *SpeedDuplex has options");
                var rxb = props.FirstOrDefault(p => p.Keyword == "*ReceiveBuffers");
                if (rxb is null)
                    check(true, "smoke numeric bounds skipped (driver exposes no *ReceiveBuffers)");
                else
                    check(rxb.Type == "int" && (rxb.Min ?? 0) > 0 && (rxb.Max ?? 0) > (rxb.Min ?? 0),
                        $"smoke numeric bounds read ({rxb.Min}..{rxb.Max})");
                check(props.Any(p => p.Category == "Vendor"), "smoke vendor category present");
            }

            var tcp = await NetAdapterService.GetTcpGlobalAsync().ConfigureAwait(false);
            check(tcp.Count >= 5, $"smoke netsh TCP parsed ({tcp.Count} entries)");
            check(tcp.Any(e => e.Name.Contains("Auto-Tuning")), "smoke autotuning present");

            var ping = await PingAsync("127.0.0.1", 3, 200).ConfigureAwait(false);
            check(ping.Received >= 1, $"smoke loopback ping replies ({ping.Received}/3)");

            var gw = list.SelectMany(a => a.Gateways).FirstOrDefault(g => !g.StartsWith("fe80", StringComparison.OrdinalIgnoreCase));
            if (gw is not null)
            {
                var gp = await PingAsync(gw, 3, 300).ConfigureAwait(false);
                check(gp.Received >= 1, $"smoke gateway ping replies ({gp.Received}/3 to {gw})");
            }
            else
            {
                check(true, "smoke no IPv4 gateway to ping (skipped)");
            }

            check(NetEditService.IsElevated(), "smoke elevation detected (shell is admin)");
        }
        catch (Exception ex)
        {
            check(false, "smoke threw: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<NetPingSummary> PingAsync(string host, int count, int timeoutMs)
    {
        var rtts = new System.Collections.Generic.List<double>();
        try
        {
            using var pinger = new System.Net.NetworkInformation.Ping();
            for (int i = 0; i < count; i++)
            {
                var reply = await pinger.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    rtts.Add(reply.RoundtripTime);
            }
        }
        catch
        {
        }
        return NetParsing.PingStats(count, rtts);
    }
}
