using System.Collections.Generic;
using System.Threading.Tasks;

namespace kaliteConfig.Input;

public interface IMouseProtocol
{
    string Id { get; }
    bool EnabledByDefault { get; }
    bool Matches(MouseDevice device);
    Task<IReadOnlyList<MouseCapability>> ProbeAsync(MouseDevice device);
    Task<string?> ReadAsync(MouseDevice device, string setting);
    Task<bool> WriteAsync(MouseDevice device, string setting, string value);
}
