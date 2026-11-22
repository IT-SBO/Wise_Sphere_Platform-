#!/usr/bin/env bash
# Run the .NET 10 SDK in a container (no local SDK needed):  scripts/dotnet.sh build | test | ...
# Passes through an HTTPS proxy + CA bundle when the host uses one (corporate proxies, CI sandboxes).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARGS=(--rm -v "$ROOT":/src -w /src -v ws-nuget:/root/.nuget/packages -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1)
if [ -n "${HTTPS_PROXY:-}" ]; then
  ARGS+=(--network host -e HTTPS_PROXY="$HTTPS_PROXY" -e NO_PROXY="${NO_PROXY:-}")
  if [ -n "${SSL_CERT_FILE:-}" ]; then ARGS+=(-v "$SSL_CERT_FILE":/etc/ssl/certs/proxy-ca.crt:ro -e SSL_CERT_FILE=/etc/ssl/certs/proxy-ca.crt); fi
fi
exec docker run "${ARGS[@]}" mcr.microsoft.com/dotnet/sdk:10.0.404 dotnet "$@"
