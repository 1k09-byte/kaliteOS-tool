$repo = "c:\Users\Administrator\source\repos\kaliteconfig\src\kaliteConfig"
$files = Get-ChildItem -Path $repo -Recurse -Include *.cs, *.xaml

foreach ($file in $files) {
    if ($file.FullName -match "obj|bin|AssemblyInfo") { continue }
    $content = Get-Content -Path $file.FullName -Raw
    $original = $content
    
    # Remove the massive standard copyright header
    $content = $content -replace "(?sm)^// ==============================================================================.*?// without the express written permission of the copyright holder\.\r?\n// ==============================================================================\r?\n?", ""
    
    # Remove multi-line /* */ comments (which is usually dead code/notes)
    # Be careful not to remove things inside strings, but for a quick script we do a non-greedy replace
    $content = $content -replace "(?sm)/\*.*?\*/\r?\n?", ""
    
    # Remove large blocks of contiguous // comments (likely dead code block)
    # Match 3 or more consecutive lines of // comments that are not ///
    $content = $content -replace "(?m)(^\s*//((?!/).*)\r?\n){3,}", ""
    
    if ($content -ne $original) {
        Set-Content -Path $file.FullName -Value $content -NoNewline
    }
}
