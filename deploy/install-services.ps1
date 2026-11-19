# Установка бота и Web-терминала как Windows-служб (запуск от администратора).
param([string]$Publish = (Join-Path (Split-Path -Parent $PSScriptRoot) "publish"),
      [string]$DataDir = "C:\VertexShared")
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
[Environment]::SetEnvironmentVariable("VERTEX_DATA_DIR", $DataDir, "Machine")

$bot = Join-Path $Publish "bot\VertexAutoTradeBinance8.exe"
$web = Join-Path $Publish "web\VertexAutoTradeBinance8.Web.exe"

if (-not (Get-Service VertexAutoTradeBot -ErrorAction SilentlyContinue)) {
  sc.exe create VertexAutoTradeBot binPath= "`"$bot`"" start= delayed-auto DisplayName= "Vertex AutoTrade Bot" | Out-Null
  sc.exe failure VertexAutoTradeBot reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
}
if (-not (Get-Service VertexTerminalWeb -ErrorAction SilentlyContinue)) {
  sc.exe create VertexTerminalWeb binPath= "`"$web`" --contentRoot `"$(Split-Path $web)`"" start= delayed-auto DisplayName= "Vertex Terminal (Web)" | Out-Null
  sc.exe failure VertexTerminalWeb reset= 86400 actions= restart/10000/restart/30000 | Out-Null
}
Start-Service VertexAutoTradeBot
Start-Service VertexTerminalWeb
Write-Host "Службы запущены. Терминал: http://127.0.0.1:5253  Логи: $DataDir\logs" -ForegroundColor Green
