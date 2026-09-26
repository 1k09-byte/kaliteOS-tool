$oldHeader = @"
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

$newHeader = @"
// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================

"@

$files = Get-ChildItem -Path "c:\Users\Administrator\source\repos\kaliteconfig\src\kaliteConfig" -Filter *.cs -Recurse

foreach ($file in $files) {
    if ($file.FullName -match "\\obj\\" -or $file.FullName -match "\\bin\\") { continue }
    
    $content = Get-Content $file.FullName -Raw
    if ($content.Contains($oldHeader)) {
        $newContent = $content.Replace($oldHeader, $newHeader)
        Set-Content -Path $file.FullName -Value $newContent -NoNewline
    }
}
Write-Host "Updated licensing header in $($files.Count) C# source files."
