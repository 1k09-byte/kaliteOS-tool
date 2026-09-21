<#
.SYNOPSIS
  Answers two questions the thread tuner's "Apply ideal processor" depends on,
  using the real kernel32 exports:
    1. What does GetThreadIdealProcessorEx actually RETURN (the app declares it
       as "previous ideal processor number, (DWORD)-1 on failure")?
    2. Does SetThreadIdealProcessorEx report failure through its BOOL, and does
       a valid processor number round-trip?

  Prints the outcome of each call so the findings are evidence, not folklore.
#>
[CmdletBinding()]
param([int[]]$Targets = @(7, 0, 255))

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class IdealProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PN { public ushort Group; public byte Number; public byte Reserved; }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr SetThreadAffinityMask(IntPtr h, IntPtr mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadIdealProcessorEx(IntPtr h, ref PN ideal, IntPtr prev);

    // Variant A: what the app currently declares (DWORD return).
    [DllImport("kernel32.dll", EntryPoint = "GetThreadIdealProcessorEx", SetLastError = true)]
    public static extern uint GetA(IntPtr h, out PN cur);

    // Variant B: BOOL return.
    [DllImport("kernel32.dll", EntryPoint = "GetThreadIdealProcessorEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetB(IntPtr h, out PN cur);

    public static string ReadBoth(IntPtr h, out uint a, out bool b, out PN pnA, out PN pnB)
    {
        pnA = new PN(); pnB = new PN();
        a = GetA(h, out pnA);
        int errA = Marshal.GetLastWin32Error();
        b = GetB(h, out pnB);
        int errB = Marshal.GetLastWin32Error();
        return string.Format("A(uint) ret={0} err={1} G{2}:{3} | B(bool) ret={4} err={5} G{6}:{7}",
            a, errA, pnA.Group, pnA.Number, b, errB, pnB.Group, pnB.Number);
    }

    public static string Set(IntPtr h, ushort group, byte number, out bool ok, out int err)
    {
        var ideal = new PN { Group = group, Number = number, Reserved = 0 };
        var previous = new PN();
        IntPtr prevPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PN>());
        try
        {
            ok = SetThreadIdealProcessorEx(h, ref ideal, prevPtr);
            err = Marshal.GetLastWin32Error();
            previous = Marshal.PtrToStructure<PN>(prevPtr);
            return string.Format("--> group {0} CPU {1}: ok={2} err={3} windows-reported-previous=G{4}:{5}",
                group, number, ok, err, previous.Group, previous.Number);
        }
        finally { Marshal.FreeHGlobal(prevPtr); }
    }
}
'@

$h = [IdealProbe]::GetCurrentThread()
Write-Host "=== GetThreadIdealProcessorEx semantics ===" -ForegroundColor Cyan
$pnA = New-Object IdealProbe+PN
$pnB = New-Object IdealProbe+PN
$report = [IdealProbe]::ReadBoth($h, [ref]([uint32]0), [ref]([bool]$false), [ref]$pnA, [ref]$pnB)
Write-Host "  before: $report"

foreach ($t in $Targets) {
    Write-Host ""
    Write-Host "=== SetThreadIdealProcessorEx -> CPU $t ===" -ForegroundColor Cyan
    $ok = $false; $err = 0
    Write-Host ("  " + [IdealProbe]::Set($h, 0, [byte]$t, [ref]$ok, [ref]$err))
    $pnA2 = New-Object IdealProbe+PN
    $pnB2 = New-Object IdealProbe+PN
    $after = [IdealProbe]::ReadBoth($h, [ref]([uint32]0), [ref]([bool]$false), [ref]$pnA2, [ref]$pnB2)
    Write-Host "  after : $after"
}

# Realistic failure the tuner hits: the thread's affinity mask is narrower than
# the box the user ticked. Does an ideal processor outside the mask fail, or is
# it accepted and simply never honoured?
Write-Host ""
Write-Host "=== ideal processor outside the thread's affinity mask ===" -ForegroundColor Cyan
$last = [Environment]::ProcessorCount - 1
$saved = [IdealProbe]::SetThreadAffinityMask($h, [IntPtr]0x0F)   # CPUs 0-3 only
Write-Host "  affinity narrowed to CPUs 0-3 (previous mask 0x$('{0:X}' -f $saved.ToInt64()))"
$ok = $false; $err = 0
Write-Host ("  inside  (CPU 2): " + [IdealProbe]::Set($h, 0, 2, [ref]$ok, [ref]$err))
$ok = $false; $err = 0
Write-Host ("  outside (CPU $last): " + [IdealProbe]::Set($h, 0, $last, [ref]$ok, [ref]$err))
[void][IdealProbe]::SetThreadAffinityMask($h, $saved)
Write-Host "  affinity restored to 0x$('{0:X}' -f $saved.ToInt64())"
$ok = $false; $err = 0
Write-Host ("  same CPU now that affinity is wide again (CPU $last): " + [IdealProbe]::Set($h, 0, $last, [ref]$ok, [ref]$err))
$ok = $false; $err = 0
Write-Host ("  a CPU that was never excluded (CPU 8): " + [IdealProbe]::Set($h, 0, 8, [ref]$ok, [ref]$err))
