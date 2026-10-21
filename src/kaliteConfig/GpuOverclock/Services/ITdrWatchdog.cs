using System;

namespace kaliteConfig.GpuOverclock.Services
{
    public interface ITdrWatchdog : IDisposable
    {
        void Arm();

        void Disarm();

        event Action? DriverResetDetected;
    }
}
