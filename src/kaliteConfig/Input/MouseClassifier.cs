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
            device.ClassReason = "Virtual or remote device, not a physical mouse.";
            return;
        }
        device.VendorSoftwarePresent = vendorSoftwarePresent;
        device.VendorSoftwareName = vendorSoftwareName ?? string.Empty;
        bool makerBranded = !catalog.IsGenericMaker(device.Manufacturer);
        bool tokenBranded = !makerBranded && catalog.MatchesKnownBrand(device.Manufacturer, device.Product);
        device.Branded = makerBranded || tokenBranded;
        if (vendorSoftwarePresent || hasVendorInterface)
        {
            device.Class = MouseClass.Supported;
            device.ClassReason = vendorSoftwarePresent
                ? "Vendor software found: " + vendorSoftwareName + "."
                : "Vendor-defined interface handled by this app.";
            return;
        }
        if (makerBranded || tokenBranded)
        {
            device.Class = MouseClass.Supported;
            device.ClassReason = makerBranded
                ? "Brand identified from the device manufacturer string."
                : "Brand identified from the device product string.";
            return;
        }
        device.Class = MouseClass.Ignored;
        device.ClassReason = "Generic mouse: no brand and no vendor software found.";
    }
}
