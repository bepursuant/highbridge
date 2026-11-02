#!/usr/bin/env bash
# Builds an unsigned highbridge MSI on Linux by running WiX under Wine (WiX itself only runs on
# Windows). For development and CI checks only; release MSIs come from scripts/build-msi.ps1 on
# Windows, which also signs and runs ICE validation (ICE validation does not work under Wine).
#
# Needs: dotnet 10 SDK, wine. Downloads a Windows x64 .NET runtime for WiX on first use.
# Wine's cabinet.dll does not compress, so this MSI is larger than one built on Windows.
#
#   scripts/build-msi.sh [version]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

version="${1:-$(dotnet msbuild src/Highbridge.App/Highbridge.App.csproj -getProperty:Version | tail -n 1)}"
runtime_version="${HIGHBRIDGE_WIN_RUNTIME_VERSION:-10.0.14}"
win_dotnet="${HIGHBRIDGE_WIN_DOTNET:-$HOME/.cache/highbridge/windotnet-$runtime_version}"
publish_dir="$root/artifacts/publish/win-x86"
msi="$root/artifacts/msi/highbridge-$version-x86.msi"

command -v wine >/dev/null || { echo "wine is required" >&2; exit 1; }

if [[ ! -f "$win_dotnet/dotnet.exe" ]]; then
  mkdir -p "$win_dotnet"
  curl -fsSL -o "$win_dotnet/runtime.zip" \
    "https://builds.dotnet.microsoft.com/dotnet/Runtime/$runtime_version/dotnet-runtime-$runtime_version-win-x64.zip"
  (cd "$win_dotnet" && unzip -q runtime.zip && rm runtime.zip)
fi

rm -rf "$publish_dir"
dotnet publish src/Highbridge.App -c Release -o "$publish_dir" -p:Version="$version"
dotnet tool restore

wix_version="$(python3 -c 'import json;print(json.load(open(".config/dotnet-tools.json"))["tools"]["wix"]["version"])')"
wix_dll="${NUGET_PACKAGES:-$HOME/.nuget/packages}/wix/$wix_version/tools/net6.0/any/wix.dll"

mkdir -p "$(dirname "$msi")"
wine "$win_dotnet/dotnet.exe" "$(winepath -w "$wix_dll")" build \
  "$(winepath -w installer/Highbridge.wxs)" "$(winepath -w installer/Dialogs.wxs)" \
  -arch x86 -d ProductVersion="$version" -d PublishDir="$(winepath -w "$publish_dir")" \
  -o "$(winepath -w "$msi")"

echo "Built $msi ($(( $(stat -c %s "$msi") / 1024 )) KB, unsigned, not ICE-validated)"
