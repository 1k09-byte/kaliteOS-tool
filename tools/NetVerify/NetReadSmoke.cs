using System;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services;

namespace NetVerify;

/// <summary>Live read smoke tests: real registry, real NICs, real netsh, real ping.
/// Guarded so a failure reports instead of hanging.</summary>
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
                check(props.Count >= 20, $"smoke reads advanced props ({props.Count})");
                var speed = props.FirstOrDefault(p => p.Keyword == "*SpeedDuplex");
                check(speed is not null && speed.Options.Count >= 5, "smoke *SpeedDuplex has options");
                var rxb = props.FirstOrDefault(p => p.Keyword == "*ReceiveBuffers");
                check(rxb is not null && rxb.Type == "int" && rxb.Min == 32 && rxb.Max == 4096, "smoke numeric bounds read");
                check(props.Any(p => p.Category == "Vendor"), "smoke vendor category present");
            }

            var tcp = await NetAdapterService.GetTcpGlobalAsync().ConfigureAwait(false);
            check(tcp.Count >= 5, $"smoke netsh TCP parsed ({tcp.Count} entries)");
            check(tcp.Any(e => e.Name.Contains("Auto-Tuning")), "smoke autotuning present");

            var ping = await NetLatency.PingAsync("127.0.0.1", 3, 200).ConfigureAwait(false);
            check(ping.Summary.Received >= 1, $"smoke loopback ping replies ({ping.Summary.Received}/3)");

            var gw = list.SelectMany(a => a.Gateways).FirstOrDefault(g => !g.StartsWith("fe80", StringComparison.OrdinalIgnoreCase));
            if (gw is not null)
            {
                var gp = await NetLatency.PingAsync(gw, 3, 300).ConfigureAwait(false);
                check(gp.Summary.Received >= 1, $"smoke gateway ping replies ({gp.Summary.Received}/3 to {gw})");
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
}
