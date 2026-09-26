// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native
{
    public static class NtDll
    {
        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(
            IntPtr ProcessHandle, 
            int ProcessInformationClass, 
            ref PS_PROTECTION ProcessInformation, 
            int ProcessInformationLength, 
            out int ReturnLength);
            
        [StructLayout(LayoutKind.Sequential)]
        public struct PS_PROTECTION
        {
            public byte Level;
            public byte Type;
            public byte Signer;
            public byte Audit;
        }
    }
}
