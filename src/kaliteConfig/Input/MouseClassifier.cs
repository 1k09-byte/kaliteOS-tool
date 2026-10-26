using System;

namespace kaliteConfig.Input;

public interface IMouseClassifier
{
    void Classify(MouseDevice device, MouseCatalog catalog, bool vendorSoftwarePresent, string vendorSoftwareName, bool hasVendorInterface);
}

public sealed class MouseClassifier : IMouseClassifier
{
    private static readonly string[] NotMouseKinds = new[]
    {
        "touchpad",
        "touch screen",
        "touchscreen",
        "digitizer",
        "pen",
        "stylus",
        "wacom",
    };

    public static bool LooksLikeNonMouse(string manufacturer, string product)
    {
        string haystack = ((manufacturer ?? string.Empty) + " " + (product ?? string.Empty)).ToLowerInvariant();
        foreach (string kind in NotMouseKinds)
            if (haystack.Contains(kind)) return true;
        return false;
    }

    public void Classify(MouseDevice device, MouseCatalog catalog, bool vendorSoftwarePresent, string vendorSoftwareName, bool hasVendorInterface)
    {
        if (device.IsVirtual)
        {
            device.Class = MouseClass.Ignored;
            device.WindowsOnly = false;
            device.Branded = false;
            device.ClassReason = "Virtual or remote device, not a physical mouse.";
            return;
        }
        device.VendorSoftwarePresent = vendorSoftwarePresent;
        device.VendorSoftwareName = vendorSoftwareName ?? string.Empty;
        if (vendorSoftwarePresent || hasVendorInterface)
        {
            device.Class = MouseClass.Supported;
            device.WindowsOnly = false;
            device.Branded = true;
            device.ClassReason = vendorSoftwarePresent
                ? "Vendor software found: " + vendorSoftwareName + "."
                : "Vendor-defined interface handled by this app.";
            return;
        }
        string maker = IsUnknownMaker(device.Manufacturer) ? string.Empty : device.Manufacturer;
        bool makerBranded = maker.Length > 0 && !catalog.IsGenericMaker(maker);
        bool tokenBranded = catalog.MatchesKnownBrand(maker, device.Product);
        bool idBranded = !makerBranded && !tokenBranded && catalog.MatchesVerifiedId(device.VendorId, device.ProductId);
        if (makerBranded || tokenBranded || idBranded)
        {
            device.Class = MouseClass.Supported;
            device.WindowsOnly = false;
            device.Branded = true;
            device.ClassReason = makerBranded
                ? "Brand identified from the device manufacturer string."
                : tokenBranded
                ? "Brand identified from the device product string."
                : "Brand identified from the device USB identity.";
            return;
        }
        bool makerGeneric = maker.Length > 0 && catalog.IsGenericMaker(maker);
        bool productGeneric = device.Product.Length == 0 || catalog.IsGenericProduct(device.Product);
        if (makerGeneric && productGeneric)
        {
            device.Class = MouseClass.Ignored;
            device.WindowsOnly = false;
            device.Branded = false;
            device.ClassReason = "Generic device: brand strings say generic and no vendor software found.";
            return;
        }
        device.Class = MouseClass.Supported;
        device.WindowsOnly = true;
        device.Branded = false;
        device.ClassReason = "Brand couldn't be identified. Windows settings only.";
    }

    private static bool IsUnknownMaker(string maker)
    {
        return maker.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || maker.Equals("(Standard mouse types)", StringComparison.OrdinalIgnoreCase);
    }
}
