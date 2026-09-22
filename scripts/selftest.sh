#!/usr/bin/env bash
# Headless self-test of SoundCalcs — no Revit, no Windows, no user input.
#   1. unit tests                    (SoundCalcs.Tests)
#   2. plugin compile check          (all Revit/WPF code, net48 + net8.0-windows)
#   3. scenario harness + images     (SoundCalcs.Harness → $OUT/report.md, *.png)
# Usage: scripts/selftest.sh [output-dir]      Exit code is non-zero if any step fails.
set -uo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-harness-output}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK not found (Linux: apt-get install -y dotnet-sdk-8.0)" >&2
  exit 2
fi

status=0
step() {
  local name="$1"; shift
  echo "::: $name"
  if "$@"; then echo "::: $name — OK"; else echo "::: $name — FAILED"; status=1; fi
}

step "unit tests" dotnet test SoundCalcs.Tests/SoundCalcs.Tests.csproj --nologo -v q
step "plugin compile check" bash -c \
  'dotnet build SoundCalcs.CompileCheck/SoundCalcs.CompileCheck.csproj -p:EnableWindowsTargeting=true -nologo -v q 2>&1 | grep -E "error|Build succeeded|Error\(s\)" | sort -u; exit ${PIPESTATUS[0]}'
step "scenario harness" dotnet run -c Release --project SoundCalcs.Harness -- --out "$OUT"

echo
echo "Report: $OUT/report.md   Images: $OUT/<scenario>/*.png"
exit $status
