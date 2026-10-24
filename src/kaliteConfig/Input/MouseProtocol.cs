using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace kaliteConfig.Input;

public sealed class MouseDeviceSettings
{
    public int DpiX { get; set; }
    public int DpiY { get; set; }
    public int RateHz { get; set; }
    public int LodIndex { get; set; }
    public int LodCount { get; set; } = 3;
    public bool MotionSync { get; set; }
    public bool AngleSnapping { get; set; }
    public bool RippleControl { get; set; }
    public bool SlamclickFilter { get; set; }
    public bool JitterFilter { get; set; }
    public bool IsV2 { get; set; }
    public bool SupportsRate { get; set; } = true;
    public bool SupportsDpi { get; set; } = true;
    public bool SupportsLod { get; set; } = true;
    public string ModelName { get; set; } = string.Empty;
}

public interface IMouseProtocol
{
    string Id { get; }
    bool EnabledByDefault { get; }
    string LastError { get; }
    IReadOnlyList<int> SupportedRates { get; }
    bool Matches(MouseDevice device);
    MouseDeviceSettings? TryRead(MouseDevice device);
    bool TryWrite(MouseDevice device, string setting, string value);
}

public interface IMouseProtocolRegistry
{
    IReadOnlyList<IMouseProtocol> Modules { get; }
    IMouseProtocol? Resolve(MouseDevice device);
}

public sealed class MouseProtocolRegistry : IMouseProtocolRegistry
{
    public MouseProtocolRegistry(IEnumerable<IMouseProtocol> modules)
    {
        Modules = new List<IMouseProtocol>(modules);
    }

    public IReadOnlyList<IMouseProtocol> Modules { get; }

    public IMouseProtocol? Resolve(MouseDevice device)
    {
        foreach (var module in Modules)
        {
            try
            {
                if (module.Matches(device)) return module;
            }
            catch { }
        }
        return null;
    }
}
