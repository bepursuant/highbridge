#!/usr/bin/env bash
# Builds everything (Windows projects cross-compile) and runs all tests that work without
# Windows or a scanner: .NET unit/API tests plus JS client unit and dev-host end-to-end tests.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build papertrail.slnx -c Release
# Server before Core: vstest runs assemblies in parallel by default, and Core's barcode
# detector tests starve the API test's below-normal BarcodeWorker threads on slow CI runners.
dotnet test tests/Papertrail.Server.Tests/Papertrail.Server.Tests.csproj -c Release --no-build
dotnet test tests/Papertrail.Core.Tests/Papertrail.Core.Tests.csproj -c Release --no-build

PAPERTRAIL_DEVHOST_DLL="$PWD/src/Papertrail.DevHost/bin/Release/net10.0/Papertrail.DevHost.dll" \
  node --test "clients/js/test/*.test.mjs"
