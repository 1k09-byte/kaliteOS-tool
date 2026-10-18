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
    public static class HardwareQuiesceGate
    {
        private static readonly object _gate = new();
        private static int _holders;
        private static string? _reason;

        public static bool IsQuiesced
        {
            get { lock (_gate) return _holders > 0; }
        }

        public static string? Reason
        {
            get { lock (_gate) return _reason; }
        }

        public static IDisposable Hold(string reason)
        {
            lock (_gate)
            {
                _holders++;
                _reason = reason;
            }
            return new Lease();
        }

        private sealed class Lease : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _holders = Math.Max(0, _holders - 1);
                    if (_holders == 0) _reason = null;
                }
            }
        }
    }
}
