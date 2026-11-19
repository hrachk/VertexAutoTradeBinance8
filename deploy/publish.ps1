# Сборка production-версии (Windows x64, framework-dependent .NET 8).
# Запуск: powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
param([string]$Out = "publish", [string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet restore VertexAutoTradeBinance8.sln
dotnet build VertexAutoTradeBinance8.sln -c Release --no-restore

dotnet publish "VertexAutoTradeBinance AI v5.0 Full Autonomous Mode/VertexAutoTradeBinance8.csproj" -c Release -r $Runtime --self-contained false -o "$Out/bot"
dotnet publish "VertexAutoTradeBinance8.Web/VertexAutoTradeBinance8.Web.csproj" -c Release -r $Runtime --self-contained false -o "$Out/web"

Copy-Item deploy/appsettings.Local.example.json "$Out/bot/appsettings.Local.example.json" -Force
Copy-Item deploy/appsettings.Local.example.json "$Out/web/appsettings.Local.example.json" -Force
Write-Host "OK → $Out\bot (worker) и $Out\web (терминал)" -ForegroundColor Green
Write-Host "Дальше: положите appsettings.Local.json с НОВЫМИ ключами и запустите deploy\install-services.ps1 (от администратора)."
