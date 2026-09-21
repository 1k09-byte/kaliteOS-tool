using System;
using System.Linq;
using kaliteConfig.Services;

namespace NetVerify;

/// <summary>Every system-wide row must be present (backed by a verified read) and
/// changeable (backed by a verified setter). No placeholder rows.</summary>
internal static class SysRowTests
{
    public static void Run(Action<bool, string> check)
    {
        var defs = NetParsing.SysRowDefs();
        check(defs.Count == 14 + 2 + 7 + 4, $"sys 27 rows defined ({defs.Count})");
        foreach (var d in defs)
        {
            bool shapeOk = d.Kind == NetParsing.SysKind.Enum
                ? d.Options.Count >= 2
                : d.Max > d.Min;
            check(shapeOk, $"sys row well-formed: {d.Key}");
            check(!string.IsNullOrWhiteSpace(d.Setter) && !string.IsNullOrWhiteSpace(d.SetterArg),
                $"sys row has setter: {d.Key}");
        }

        // Windows answers in its own casing ("enabled"); the option list is capitalized
        // and a combo bound by SelectedValue shows nothing when the two differ.
        var rssState = defs.First(d => d.Key == "Receive-Side Scaling State");
        check(NetParsing.CanonicalOption(rssState, "enabled") == "Enabled", "sys canonical maps live casing");
        check(NetParsing.CanonicalOption(rssState, " DISABLED ") == "Disabled", "sys canonical trims");
        check(NetParsing.CanonicalOption(rssState, "bogus") == "bogus", "sys canonical keeps unknown values");
        var tsRow = defs.First(d => d.Key == "RFC 1323 Timestamps");
        check(NetParsing.CanonicalOption(tsRow, "allowed") == "Default", "sys canonical maps netsh's 'allowed' to Default");

        var sections = defs.Select(d => d.Section).Distinct().ToList();
        check(sections.Count == 4
            && sections.Contains("TCP Global Parameters")
            && sections.Contains("UDP Global Parameters")
            && sections.Contains("Global Offload Settings")
            && sections.Contains("Receive Side Scaling"), "sys four sections");

        // Each section explains itself in the SysW side panel, so no section may fall
        // back to the generic text.
        foreach (var s in sections)
            check(NetParsing.SysSectionHint(s) != "System-wide network settings.", $"sys section has a hint: {s}");

        // Profile int mapping measured live (1..6).
        check(NetParsing.RssProfileName(1) == "Closest", "sys profile 1=Closest");
        check(NetParsing.RssProfileName(4) == "NUMAStatic", "sys profile 4=NUMAStatic");
        check(NetParsing.RssProfileName(6) == "Balanced", "sys profile 6=Balanced");
        check(NetParsing.RssProfileName(9).StartsWith("Unknown"), "sys profile unknown guarded");

        // Offload int display.
        check(NetParsing.OffloadDisplay("TaskOffload", 1) == "Enabled", "sys offload 1=Enabled");
        check(NetParsing.OffloadDisplay("TaskOffload", 0) == "Disabled", "sys offload 0=Disabled");
        check(NetParsing.OffloadDisplay("NetworkDirectAcrossIPSubnets", 0) == "Blocked", "sys across 0=Blocked");
        check(NetParsing.OffloadDisplay("NetworkDirectAcrossIPSubnets", 1) == "Allowed", "sys across 1=Allowed");

        // Setter commands (strings only here; execution is elevated + read-back).
        var rto = defs.First(d => d.Key == "Initial RTO");
        var (tool, args) = NetParsing.SysSetterCommand(rto, "", "1000");
        check(tool == "netsh" && args == "interface tcp set global initialrto=1000", "sys netsh setter string");
        var uro = defs.First(d => d.Key == "Receive Offload State");
        var (tool2, args2) = NetParsing.SysSetterCommand(uro, "", "disabled");
        check(tool2 == "netsh" && args2 == "interface udp set global uro=disabled", "sys udp setter string");
        var cong = defs.First(d => d.Key == "Add-On Congestion Control Provider");
        var (tool3, args3) = NetParsing.SysSetterCommand(cong, "", "Cubic");
        check(tool3 == "netsh" && args3 == "interface tcp set supplemental template=internet congestionprovider=Cubic", "sys supplemental setter string");
        var off = defs.First(d => d.Key == "TaskOffload");
        var (tool4, args4) = NetParsing.SysSetterCommand(off, "", "Disabled");
        check(tool4 == "powershell.exe" && args4.Contains("Set-NetOffloadGlobalSetting -TaskOffload Disabled -Confirm:$false"), "sys offload setter string");
        var rss = defs.First(d => d.Key == "RSS Queues");
        var (tool5, args5) = NetParsing.SysSetterCommand(rss, "Ethernet 2", "4");
        check(tool5 == "powershell.exe" && args5.Contains("Set-NetAdapterRss -Name 'Ethernet 2' -NumberOfReceiveQueues 4 -Confirm:$false"), "sys rss setter string");

        // Supplemental parse on recorded output.
        var supp = NetParsing.ParseNetshKeyValues(NetFixtures.NetshSupplementalSample);
        check(supp["Congestion Control Provider"] == "cubic", "sys parse congestion provider");
        check(supp["Minimum RTO (msec)"] == "300", "sys parse min RTO");
    }
}

