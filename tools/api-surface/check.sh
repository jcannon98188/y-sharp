#!/usr/bin/env bash
# API-surface diff for src/YSharp (ADR 0021, TASK-4.2).
# Builds the library and the dump tool, dumps the public surface, and diffs it against tools/api-surface/baseline.txt.
# Exit 0: surface unchanged. Exit 1: surface changed (the diff is printed). Pass --update to rewrite the baseline on purpose.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"

dotnet build "$root/src/YSharp/YSharp.csproj" -c Debug -nologo -v q >/dev/null
dotnet build "$here/ApiSurface.csproj" -c Release -nologo -v q >/dev/null
dll="$root/src/YSharp/bin/Debug/net10.0/YSharp.dll"
tool="$here/bin/Release/net10.0/ApiSurface.dll"
current="$(mktemp)"
trap 'rm -f "$current"' EXIT
dotnet "$tool" "$dll" > "$current"

if [[ "${1:-}" == "--update" ]]; then
  cp "$current" "$here/baseline.txt"
  echo "baseline.txt updated ($(wc -l < "$here/baseline.txt") lines)"
  exit 0
fi

if diff -u "$here/baseline.txt" "$current"; then
  echo "public surface unchanged ($(wc -l < "$current") lines)"
else
  echo "public surface CHANGED: see diff above" >&2
  exit 1
fi
