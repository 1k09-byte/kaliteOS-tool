using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native;

public sealed class SafeProcessHandle : SafeHandle
{
    public SafeProcessHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => NativeMethods.Handles.CloseHandle(handle);
}

public sealed class SafeThreadHandle : SafeHandle
{
    public SafeThreadHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => NativeMethods.Handles.CloseHandle(handle);
}
