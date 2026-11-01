# Build a single self-contained .exe (end users do NOT need .NET installed).
# One-time requirement: .NET SDK 8  ->  https://dotnet.microsoft.com/download/dotnet/8.0
#
# Run:  powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

$out = "bin\Release\net8.0-windows\win-x64\publish"
Write-Host ""
Write-Host "DONE. Distribute this folder: $out" -ForegroundColor Green
Write-Host "It must contain: QarttzVPN.exe, config.template.json, exclude.txt and core\sing-box.exe"
