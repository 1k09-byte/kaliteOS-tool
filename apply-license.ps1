$header = @"
// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================

"@

$files = Get-ChildItem -Path "c:\Users\Administrator\source\repos\kaliteconfig\src\kaliteConfig" -Filter *.cs -Recurse

foreach ($file in $files) {
    if ($file.FullName -match "\\obj\\" -or $file.FullName -match "\\bin\\") { continue }
    
    $content = Get-Content $file.FullName -Raw
    if (-not $content.StartsWith("// ===================")) {
        $newContent = $header + $content
        Set-Content -Path $file.FullName -Value $newContent -NoNewline
    }
}
Write-Host "Injected licensing header into $($files.Count) C# source files."
