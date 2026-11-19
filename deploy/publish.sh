#!/usr/bin/env bash
# Linux build (systemd). Usage: ./deploy/publish.sh [out] [rid]
set -euo pipefail
OUT=${1:-publish}; RID=${2:-linux-x64}
cd "$(dirname "$0")/.."
dotnet restore VertexAutoTradeBinance8.sln
dotnet publish "VertexAutoTradeBinance AI v5.0 Full Autonomous Mode/VertexAutoTradeBinance8.csproj" -c Release -r "$RID" --self-contained false -o "$OUT/bot"
dotnet publish VertexAutoTradeBinance8.Web/VertexAutoTradeBinance8.Web.csproj -c Release -r "$RID" --self-contained false -o "$OUT/web"
echo "OK → $OUT/bot, $OUT/web"
