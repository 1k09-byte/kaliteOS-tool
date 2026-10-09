Set-Location 'E:\Users\Administrator\source\repos\kaliteconfig'
$out = dotnet build src/kaliteConfig/kaliteConfig.csproj -c Release -p:Platform=x64 2>&1
$exit = $LASTEXITCODE
Write-Output $out
Write-Output "BUILD_EXIT=$exit"
