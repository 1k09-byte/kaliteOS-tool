using System;

namespace kaliteConfig.Models
{
    public sealed record AffinityChange(
        string DeviceId,
        string DeviceName,
        string PropertyName,
        object? OldValue,
        object? NewValue,
        DateTime Timestamp);
}
