#!/usr/bin/env bash
# Builds everything (Windows projects cross-compile) and runs all tests that work without
# Windows or a scanner: .NET unit/API tests plus JS client unit and dev-host end-to-end tests.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build highbridge.slnx -c Release
# Server before Core: vstest runs assemblies in parallel by default, and Core's barcode
# detector tests starve the API test's below-normal BarcodeWorker threads on slow CI runners.
dotnet test tests/Highbridge.Server.Tests/Highbridge.Server.Tests.csproj -c Release --no-build
dotnet test tests/Highbridge.Core.Tests/Highbridge.Core.Tests.csproj -c Release --no-build

HIGHBRIDGE_DEVHOST_DLL="$PWD/src/Highbridge.DevHost/bin/Release/net10.0/Highbridge.DevHost.dll" \
  node --test "clients/js/test/*.test.mjs"
