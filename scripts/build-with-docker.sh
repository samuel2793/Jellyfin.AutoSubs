#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project AutoSubs.SelfTest/AutoSubs.SelfTest.csproj -c Release
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish Jellyfin.Plugin.AutoSubs/Jellyfin.Plugin.AutoSubs.csproj -c Release -o publish
printf '\nGenerated: publish/Jellyfin.Plugin.AutoSubs.dll\n'
