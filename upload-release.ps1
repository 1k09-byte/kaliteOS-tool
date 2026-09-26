$ErrorActionPreference = "Stop"
try {
    $cred = "url=https://github.com`n" | git credential fill
    $tokenLine = $cred | Where-Object { $_ -match "^password=" }
    if (-not $tokenLine) { throw "No token found!" }
    $token = $tokenLine -replace "^password=", ""

    $headers = @{
        "Authorization" = "token $token"
        "Accept"        = "application/vnd.github.v3+json"
        "User-Agent"    = "PowerShell"
    }

    Write-Host "Creating release..."
    $body = '{"tag_name":"v0.3.0.21","name":"কালiteConfig v0.3.0.21","body":"- Complete Built-In Snipping Tool Purge\n- System Tray Precision\n- Improved DPI UI Rendering\n- WinUI Native Details Integration\n- Snappy Dynamic Search","draft":false,"prerelease":false}'

    try {
        $response = Invoke-RestMethod -Uri "https://api.github.com/repos/1k09-byte/kaliteOS-tool/releases" -Method Post -Headers $headers -Body $body -ContentType "application/json"
        Write-Host "Release created: $($response.id)"
    }
    catch {
        Write-Host "Creation failed, error: $_"
        Write-Host "Fetching existing release..."
        $response = Invoke-RestMethod -Uri "https://api.github.com/repos/1k09-byte/kaliteOS-tool/releases/tags/v0.3.0.21" -Method Get -Headers $headers -ContentType "application/json"
        Write-Host "Release found: $($response.id)"
    }

    $uploadUrl = $response.upload_url -replace '\{.*?\}', '?name=kaliteConfig-Setup-0.3.0.21.exe'
    Write-Host "Uploading asset to $uploadUrl..."

    try {
        Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers @{
            "Authorization" = "token $token"
            "Accept"        = "application/vnd.github.v3+json"
            "User-Agent"    = "PowerShell"
            "Content-Type"  = "application/octet-stream"
        } -InFile "C:\Users\Administrator\source\repos\kaliteconfig\dist\kaliteConfig-Setup-0.3.0.21.exe"
        Write-Host "Upload complete!"
    }
    catch {
        Write-Host "Upload failed: $_"
    }
}
catch {
    Write-Host "Fatal Error: $_"
}
