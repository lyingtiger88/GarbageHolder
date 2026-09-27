$ErrorActionPreference = 'Stop'

Write-Host 'Building Huawei E5573Cs Manager...' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install .NET 8 or newer SDK from Microsoft and run this script again.'
}

& dotnet restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

Write-Host 'Build completed successfully.' -ForegroundColor Green
