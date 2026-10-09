param([string]$Title)
Add-Type 'using System;using System.Runtime.InteropServices;public class U{[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindowW(string c,string n);[DllImport("user32.dll")]public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);}'
$h=[U]::FindWindowW($null,$Title)
if($h -ne [IntPtr]::Zero){ [U]::PostMessageW($h,0x0010,[IntPtr]::Zero,[IntPtr]::Zero); "closed: $Title" } else { "not found: $Title" }
