param([int]$ProcId)
Add-Type -AssemblyName System.Windows.Forms
Add-Type 'using System;using System.Runtime.InteropServices;public class F{[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);}'
$p = Get-Process -Id $ProcId
[F]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
"escape sent"
