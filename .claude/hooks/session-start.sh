#!/bin/bash
# Claude Code on the web: make sure the .NET 8 SDK is present and NuGet packages
# are restored so unit tests, the headless harness and the plugin compile check run.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"

if ! command -v dotnet >/dev/null 2>&1; then
  # dotnet-install.sh downloads are blocked by the egress proxy; Ubuntu's package works.
  export DEBIAN_FRONTEND=noninteractive
  apt-get install -y -qq dotnet-sdk-8.0 >/dev/null 2>&1 \
    || { apt-get update -qq >/dev/null 2>&1 && apt-get install -y -qq dotnet-sdk-8.0 >/dev/null; }
fi

{
  echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
  echo 'export DOTNET_NOLOGO=1'
} >> "${CLAUDE_ENV_FILE:-/dev/null}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet restore SoundCalcs.Tests/SoundCalcs.Tests.csproj >/dev/null
dotnet restore SoundCalcs.Harness/SoundCalcs.Harness.csproj >/dev/null
dotnet restore SoundCalcs.CompileCheck/SoundCalcs.CompileCheck.csproj -p:EnableWindowsTargeting=true >/dev/null
