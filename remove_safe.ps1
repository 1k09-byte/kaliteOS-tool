$repo = "c:\Users\Administrator\source\repos\kaliteconfig\src\kaliteConfig"
$files = Get-ChildItem -Path $repo -Recurse -Include *.cs, *.xaml

$header1 = "// ==============================================================================`r`n// Copyright (c) 2026 kaliteConfig`r`n// All rights reserved.`r`n//`r`n// This software and associated documentation files are provided freely for end-users`r`n// to download and use. However, the source code remains strictly proprietary. `r`n// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, `r`n// sublicense, or sell copies of the source code in any form, in whole or in part,`r`n// without the express written permission of the copyright holder.`r`n// ==============================================================================`r`n"

$header2 = $header1.Replace("`r`n", "`n")

$count = 0
foreach ($file in $files) {
    if ($file.FullName -match "obj|bin|AssemblyInfo") { continue }
    $content = Get-Content -Path $file.FullName -Raw
    $original = $content
    
    if ($content.StartsWith($header1)) {
        $content = $content.Substring($header1.Length)
    } elseif ($content.StartsWith($header2)) {
        $content = $content.Substring($header2.Length)
    }
    
    if ($content -ne $original) {
        # trim any leading newlines left behind
        $content = $content.TrimStart("`r`n")
        Set-Content -Path $file.FullName -Value $content -NoNewline
        $count++
    }
}
Write-Output "Cleaned $count files safely."
