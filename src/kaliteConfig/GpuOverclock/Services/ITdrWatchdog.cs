// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
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
