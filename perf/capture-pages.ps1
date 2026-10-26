param([string[]]$Pages = @('Process Tuning', 'Process Affinity'), [string]$OutDir = 'Docs/design-backup', [string]$Prefix = 'before')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Focus-App([IntPtr]$hwnd) {
  [void][Win]::ShowWindow($hwnd, 9)
  $fgThread = [Win]::GetCurrentThreadId()
  $targetThread = (Get-Process -Id (Get-Process kaliteConfig | Select-Object -First 1).Id).Threads[0].Id
  [void][Win]::AttachThreadInput($fgThread, [uint32]$targetThread, $true)
  [void][Win]::SetForegroundWindow($hwnd)
  [void][Win]::BringWindowToTop($hwnd)
  [void][Win]::AttachThreadInput($fgThread, [uint32]$targetThread, $false)
  for ($i = 0; $i -lt 20; $i++) {
    if ([Win]::GetForegroundWindow() -eq $hwnd) { return $true }
    Start-Sleep -Milliseconds 250
    [void][Win]::SetForegroundWindow($hwnd)
  }
  return ([Win]::GetForegroundWindow() -eq $hwnd)
}

if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$proc = Get-Process kaliteConfig -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output 'WINDOW-NOT-FOUND'; exit 1 }
$focused = Focus-App $proc.MainWindowHandle
Write-Output "focused=$focused"

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)
foreach ($name in $Pages) {
  $rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
  $all = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $found = $false
  foreach ($el in $all) {
    if ($el.Current.Name -eq $name -and $el.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') {
      $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
      $found = $true
      break
    }
  }
  if (-not $found) { Write-Output "NAV-NOT-FOUND: $name"; continue }
  Start-Sleep -Seconds 4

  [void](Focus-App $proc.MainWindowHandle)
  $rect = New-Object Win+RECT
  [void][Win]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  if ($w -le 0 -or $h -le 0) { Write-Output "BAD-RECT: $name"; continue }
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $gfx = [System.Drawing.Graphics]::FromImage($bmp)
  if ([Win]::GetForegroundWindow() -eq $proc.MainWindowHandle) {
    $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  } else {

    $hdc = $gfx.GetHdc()
    [void][Win]::PrintWindow($proc.MainWindowHandle, $hdc, 2)
    $gfx.ReleaseHdc($hdc)
    Write-Output "WARN occluded, PrintWindow fallback used: $name"
  }
  $safe = ($name -replace '[^A-Za-z0-9]+', '-').ToLower()
  $path = Join-Path $OutDir "$Prefix-$safe.png"
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $gfx.Dispose(); $bmp.Dispose()
  Write-Output "SAVED $path ($w x $h)"
}
