# Dumps the live kaliteConfig window's UI Automation tree.
# Usage: powershell -ExecutionPolicy Bypass -File tools/uia-dump.ps1 [-MaxDepth 8] [-Filter ""]
# Rectangles tell us whether something is laid out but unpainted (real rect, blank pixels)
# versus collapsed/zero-size (no rect) -- the distinction a screenshot cannot make for us.
param(
    [int]$MaxDepth = 8,
    [string]$Filter = "",
    [string]$Invoke = "",       # navigate: pick the first element whose Name/Class matches and run its default pattern
    [switch]$Click,            # navigate with a real mouse click at the element's clickable point
    [switch]$Probe,            # hit-test the window: who is actually at each point on screen?
    [switch]$Raw,
    [int]$SettleMs = 700
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process kaliteConfig -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Error "kaliteConfig has no window"; exit 1 }

Write-Output "PID=$($proc.Id)  HWND=$($proc.MainWindowHandle)  Title='$($proc.MainWindowTitle)'"

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if (-not $root) { Write-Error "AutomationElement.FromHandle failed (elevation mismatch?)"; exit 2 }

$walker = if ($Raw) { [System.Windows.Automation.TreeWalker]::RawViewWalker }
          else { [System.Windows.Automation.TreeWalker]::ControlViewWalker }

# Elements that exist in the tree but were never laid out report an empty (all-NaN)
# BoundingRectangle. That is the difference between "unpainted" and "not there".
function Format-Rect($r) {
    if ([double]::IsNaN($r.X) -or [double]::IsNaN($r.Width)) { return "<NO LAYOUT>" }
    return ("{0,5:F0},{1,5:F0} {2,4:F0}x{3,4:F0}" -f $r.X, $r.Y, $r.Width, $r.Height)
}

function Show-Node($node, $depth) {
    if ($depth -gt $MaxDepth) { return }
    $pad = "  " * $depth
    $name = $node.Current.Name
    $type = $node.Current.ControlType.ProgrammaticName -replace "ControlType\.", ""
    try { $local = $node.Current.LocalizedControlType } catch { $local = "" }
    try { $cls = $node.Current.ClassName } catch { $cls = "" }
    try { $off = $node.Current.IsOffscreen } catch { $off = $false }
    $rect = Format-Rect $node.Current.BoundingRectangle
    $id = ""
    try { $id = $node.Current.AutomationId } catch {}
    $line = "$pad$type/$local [$rect] '$name'"
    if ($cls) { $line += " class=$cls" }
    if ($id) { $line += " #$id" }
    if ($off) { $line += " (offscreen)" }
    if (-not $Filter -or $name -match $Filter -or $type -match $Filter -or $cls -match $Filter) { Write-Output $line }
    $child = $walker.GetFirstChild($node)
    while ($child) {
        Show-Node $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

if ($Probe) {
    $r = $root.Current.BoundingRectangle
    for ($y = 40; $y -lt 80; $y += 8) {
        for ($x = 90; $x -lt 700; $x += 20) {
            $hit = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point $x, $y))
            if ($hit) {
                Write-Output ("{0,4},{1,3} -> {2} '{3}'" -f $x, $y, ($hit.Current.ControlType.ProgrammaticName -replace "ControlType\.", ""), $hit.Current.Name)
            }
        }
    }
    exit 0
}

if ($Invoke) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $target = $null
    foreach ($e in $all) {
        if ($e.Current.Name -eq $Invoke -or $e.Current.AutomationId -eq $Invoke) { $target = $e; break }
    }
    if (-not $target) {
        foreach ($e in $all) {
            if ($e.Current.Name -like "*$Invoke*") { $target = $e; break }
        }
    }
    if (-not $target) { Write-Error "no element matching '$Invoke'"; exit 3 }
    Write-Output "INVOKE '$($target.Current.Name)' ($($target.Current.ControlType.ProgrammaticName))"

    if ($Click) {
        # Real input, because programmatic selection of NavigationView top tabs does
        # not land on the item you asked for.
        $sig = '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);' +
               '[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, System.IntPtr e);' +
               '[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);' +
               '[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);'
        Add-Type -Namespace W -Name U -MemberDefinition $sig -ReferencedAssemblies System.Drawing
        $pt = $null
        try { $pt = $target.GetClickablePoint() } catch { }
        if (-not $pt) {
            # Fall back to the centre of the bounding rectangle (some NavigationView
            # tabs report no clickable point).
            $r = $target.Current.BoundingRectangle
            $pt = New-Object System.Windows.Point ($r.X + $r.Width / 2), ($r.Y + $r.Height / 2)
        }
        $save = New-Object System.Drawing.Point
        [void][W.U]::GetCursorPos([ref]$save)
        [void][W.U]::SetCursorPos([int]$pt.X, [int]$pt.Y)
        Start-Sleep -Milliseconds 150
        # XAML's pointer pipeline needs a move to place the pointer before the press: without it
        # the click can be routed to whatever element was under the cursor before.
        $absX = [int]([int]$pt.X * 65535 / [W.U]::GetSystemMetrics(0))
        $absY = [int]([int]$pt.Y * 65535 / [W.U]::GetSystemMetrics(1))
        [W.U]::mouse_event(([uint32]0x0001 -bor 0x8000), [uint32]$absX, [uint32]$absY, 0, [System.IntPtr]::Zero)  # MOVE|ABSOLUTE
        Start-Sleep -Milliseconds 120
        [W.U]::mouse_event(0x0002, 0, 0, 0, [System.IntPtr]::Zero)   # LEFTDOWN
        Start-Sleep -Milliseconds 60
        [W.U]::mouse_event(0x0004, 0, 0, 0, [System.IntPtr]::Zero)   # LEFTUP
        Start-Sleep -Milliseconds 150
        [void][W.U]::SetCursorPos($save.X, $save.Y)
        Write-Output "CLICKED at $([int]$pt.X),$([int]$pt.Y)"
        Start-Sleep -Milliseconds $SettleMs
        Show-Node $root 0
        exit 0
    }
    $done = $false
    foreach ($p in @([System.Windows.Automation.SelectionItemPattern]::Pattern,
                     [System.Windows.Automation.InvokePattern]::Pattern,
                     [System.Windows.Automation.ExpandCollapsePattern]::Pattern)) {
        $obj = $null
        if ($target.TryGetCurrentPattern($p, [ref]$obj)) {
            switch ($p.ProgrammaticName) {
                "SelectionItemPatternIdentifiers.Pattern" { $obj.Select() }
                "InvokePatternIdentifiers.Pattern"        { $obj.Invoke() }
                "ExpandCollapsePatternIdentifiers.Pattern"{ $obj.Expand() }
            }
            $done = $true
            break
        }
    }
    if (-not $done) { Write-Error "'$Invoke' exposes no invokable pattern"; exit 4 }
    Start-Sleep -Milliseconds $SettleMs
}

Show-Node $root 0
