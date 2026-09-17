using System;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Abstraction over the TDR watchdog so SafetyRevertService can be unit
    /// tested with a fake that fires DriverResetDetected deterministically.
    /// </summary>
    public interface ITdrWatchdog : IDisposable
    {
        /// <summary>Begin watching for driver resets (post-write window).</summary>
        void Arm();

        /// <summary>Stop watching and clear the latched detection.</summary>
        void Disarm();

        /// <summary>Raised on a background thread when a reset is detected while armed.</summary>
        event Action? DriverResetDetected;
    }
}
