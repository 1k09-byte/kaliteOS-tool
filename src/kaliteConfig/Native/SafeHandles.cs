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

namespace kaliteConfig.Native;

/// <summary>Guaranteed-cleanup wrapper for process handles. Never use raw <see cref="IntPtr"/>.</summary>
public sealed class SafeProcessHandle : SafeHandle
{
    public SafeProcessHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => NativeMethods.Handles.CloseHandle(handle);
}

/// <summary>Guaranteed-cleanup wrapper for thread handles. Never use raw <see cref="IntPtr"/>.</summary>
public sealed class SafeThreadHandle : SafeHandle
{
    public SafeThreadHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => NativeMethods.Handles.CloseHandle(handle);
}
