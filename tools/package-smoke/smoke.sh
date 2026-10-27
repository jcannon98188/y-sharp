#!/usr/bin/env bash
# Installs the packed YSharp packages into a fresh console project outside the repository and runs Program.cs (TASK-15).
# Usage: tools/package-smoke/smoke.sh <directory with the .nupkg files> <version>
set -euo pipefail
feed=$(cd "$1" && pwd)
# a Windows path for dotnet under Git Bash
command -v cygpath >/dev/null && feed=$(cygpath -w "$feed")
version=$2
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cd "$work"
# a private package cache, so a re-pack of the same version is never served from an older cached copy
export NUGET_PACKAGES="$work/packages"
# outside the repository, so its Directory.Build.props and central package versions do not apply
dotnet new console -n Smoke -o . --framework net10.0 >/dev/null
cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
for p in YSharp YSharp.Server YSharp.Server.Testing; do
  dotnet add package "$p" --version "$version" | tail -n 3
done
cp "$here/Program.cs" Program.cs
dotnet run -c Release
