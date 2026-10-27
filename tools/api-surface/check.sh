#!/usr/bin/env bash
# API-surface diff for src/YSharp (ADR 0021, TASK-4.2) and the server tier, src/YSharp.Server and src/YSharp.Server.Testing (TASK-10).
# Builds the libraries and the dump tool, dumps each public surface, and diffs it against its baseline in tools/api-surface:
# baseline.txt for YSharp, baseline.server.txt for the server tier.
# Exit 0: both surfaces unchanged. Exit 1: a surface changed (the diff is printed). Pass --update to rewrite the baselines on purpose.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"

dotnet build "$root/src/YSharp.Server.Testing/YSharp.Server.Testing.csproj" -c Debug -nologo -v q >/dev/null
dotnet build "$here/ApiSurface.csproj" -c Release -nologo -v q >/dev/null
bin="$root/src/YSharp.Server.Testing/bin/Debug/net10.0"
tool="$here/bin/Release/net10.0/ApiSurface.dll"
core="$(mktemp)"
server="$(mktemp)"
trap 'rm -f "$core" "$server"' EXIT
dotnet "$tool" "$bin/YSharp.dll" > "$core"
dotnet "$tool" "$bin/YSharp.Server.dll" "$bin/YSharp.Server.Testing.dll" > "$server"

if [[ "${1:-}" == "--update" ]]; then
  cp "$core" "$here/baseline.txt"
  cp "$server" "$here/baseline.server.txt"
  echo "baselines updated (baseline.txt $(wc -l < "$here/baseline.txt") lines, baseline.server.txt $(wc -l < "$here/baseline.server.txt") lines)"
  exit 0
fi

status=0
for pair in "baseline.txt:$core" "baseline.server.txt:$server"; do
  baseline="${pair%%:*}"
  current="${pair#*:}"
  if diff -u --strip-trailing-cr "$here/$baseline" "$current"; then
    echo "$baseline: public surface unchanged ($(wc -l < "$current") lines)"
  else
    echo "$baseline: public surface CHANGED: see diff above" >&2
    status=1
  fi
done
exit $status
