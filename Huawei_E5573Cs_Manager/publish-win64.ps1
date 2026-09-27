$ErrorActionPreference = 'Stop'

Write-Host 'Publishing Huawei E55573Cs Manager for Windows x64...' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install .NET 8 or newer SDK from Microsoft and run this script again.'
}

& dotnet restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$out = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish'
Write-Host ''
Write-Host 'Publish completed successfully.' -ForegroundColor Green
Write-Host "Output: $out"
if (Test-Path $out) { Start-Process explorer.exe $out }
