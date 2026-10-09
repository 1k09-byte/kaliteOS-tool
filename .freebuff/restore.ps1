param([int]$ProcId)
Add-Type 'using System;using System.Runtime.InteropServices;public class W2{[DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);}'
$p = Get-Process -Id $ProcId
"hwnd=$($p.MainWindowHandle) iconic=$([W2]::IsIconic($p.MainWindowHandle))"
[W2]::ShowWindow($p.MainWindowHandle, 3) | Out-Null   # SW_MAXIMIZE
[W2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 2
"restored+maximized, iconic now=$([W2]::IsIconic($p.MainWindowHandle))"
