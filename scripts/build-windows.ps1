$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root "dist/win-x64"
dotnet restore "$Root/JAASM.slnx"
dotnet publish "$Root/src/JAASM.App/JAASM.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $Out
Write-Host "JAASM Windows build: $Out"
