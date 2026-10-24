using System;
using System.Collections.Generic;

namespace kaliteConfig.Input;

public sealed class MouseCatalog
{
    public IReadOnlyList<string> GenericMakers { get; } = new List<string>
    {
        string.Empty,
        "Generic",
        "USB",
        "HID",
        "Microsoft",
        "(Standard system devices)",
        "Standard system devices",
        "PixArt",
        "Holtek",
        "MOSART",
        "MosArt",
        "Sunplus",
        "Sinowealth",
        "Primax",
        "ELAN",
    };

    public IReadOnlyList<string> GenericProducts { get; } = new List<string>
    {
        string.Empty,
        "USB Mouse",
        "HID-compliant mouse",
        "USB Optical Mouse",
        "USB Human Interface Device",
        "2.4G Mouse",
        "2.4GHz Mouse",
        "Wireless Mouse",
        "Composite Device",
    };

    public IReadOnlyList<string> VirtualSignals { get; } = new List<string>
    {
        "ROOT\\",
        "RDP_MOU",
        "RDP_KBD",
        "VMBUS",
        "VMBUS\\",
        "VMWARE",
        "VMW",
        "QEMU",
        "VBOX",
        "VBOXUSB",
        "XEN",
        "CITRIX",
        "TEAMVIEWER",
        "PARSEC",
        "SUNSHINE",
        "MOONLIGHT",
        "VIRTUAL",
        "VJOY",
        "VIGEM",
        "REWASD",
    };

    public IReadOnlyList<MouseVendorSoftware> VendorPrograms { get; } = new List<MouseVendorSoftware>
    {
        new("Logitech G Hub", new[] { "lghub.exe", "lghub_agent.exe" }, new[] { "LGHUB" }, new[] { "Logi" }),
        new("Razer Synapse", new[] { "Razer Synapse.exe", "RazerCentral.exe" }, new[] { "Razer" }, new[] { "Razer" }),
        new("SteelSeries GG", new[] { "SteelSeriesGG.exe", "SteelSeriesEngine.exe" }, new[] { "SteelSeries" }, new[] { "SteelSeries" }),
        new("Corsair iCUE", new[] { "iCUE.exe" }, new[] { "Corsair" }, new[] { "Corsair" }),
        new("ROCCAT Swarm", new[] { "RoccatSwarm.exe" }, new[] { "ROCCAT" }, new[] { "ROCCAT" }),
        new("HyperX NGENUITY", new[] { "NGenuity.exe", "HyperX NGENUITY.exe" }, new[] { "HyperX" }, new[] { "HyperX", "Kingston" }),
        new("Glorious Core", new[] { "GloriousCore.exe", "Glorious Model O.exe" }, new[] { "Glorious" }, new[] { "Glorious" }),
        new("ASUS Armoury Crate", new[] { "ArmouryCrate.exe" }, new[] { "ASUS" }, new[] { "ASUS", "ROG" }),
        new("MSI Center", new[] { "MSI Center.exe", "MSICenter.exe" }, new[] { "MSI" }, new[] { "MSI" }),
    };

    public IReadOnlyList<string> KnownBrands { get; } = new List<string>
    {
        "Endgame Gear",
        "Endgamegear",
        "Endgame",
        "XM2",
    };

    public bool MatchesKnownBrand(string manufacturer, string product)
    {
        string haystack = ((manufacturer ?? string.Empty) + " " + (product ?? string.Empty)).Trim();
        if (haystack.Length == 0) return false;
        foreach (string brand in KnownBrands)
            if (haystack.IndexOf(brand, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    public bool IsGenericMaker(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string text = value.Trim();
        foreach (string known in GenericMakers)
            if (text.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool IsGenericProduct(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string text = value.Trim();
        foreach (string known in GenericProducts)
            if (text.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool LooksVirtual(string instanceId, string parentId, string devicePath)
    {
        string haystack = ((instanceId ?? string.Empty) + "|" + (parentId ?? string.Empty) + "|" + (devicePath ?? string.Empty)).ToUpperInvariant();
        foreach (string signal in VirtualSignals)
            if (haystack.Contains(signal.ToUpperInvariant())) return true;
        return false;
    }
}

public sealed class MouseVendorSoftware
{
    public MouseVendorSoftware(string name, string[] processes, string[] uninstallHints, string[] makerHints)
    {
        Name = name;
        Processes = processes;
        UninstallHints = uninstallHints;
        MakerHints = makerHints;
    }

    public string Name { get; }
    public string[] Processes { get; }
    public string[] UninstallHints { get; }
    public string[] MakerHints { get; }
}
