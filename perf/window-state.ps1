param([switch]$Restore)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Show {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
}
"@
$p = Get-Process kaliteConfig -ErrorAction Stop | Select-Object -First 1
$h = $p.MainWindowHandle
if ($Restore) {
  [Win32Show]::ShowWindow($h, 9) | Out-Null
  Start-Sleep -Seconds 3
  Write-Output ("restored={0}" -f (-not [Win32Show]::IsIconic($h)))
} else {
  [Win32Show]::ShowWindow($h, 6) | Out-Null
  Start-Sleep -Seconds 5
  Write-Output ("minimized={0}" -f ([Win32Show]::IsIconic($h)))
}
