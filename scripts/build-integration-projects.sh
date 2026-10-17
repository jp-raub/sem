#!/usr/bin/env bash
# Build each shared project once; test execution stays serial and isolated.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
shopt -s nullglob
projects=(src/Sem.Web/Sem.Web.csproj tests/*/*.csproj)
solution="$work/sem-integration.slnx"
dotnet new sln --name sem-integration --format slnx --output "$work"
dotnet sln "$solution" add "${projects[@]}"
# Cache only downloaded packages. Always resolve the current complete graph;
# neither cached build outputs nor a cache hit may skip restore/build/tests.
dotnet restore "$solution"
# Bound build parallelism to avoid memory pressure; do not parallelize fixtures.
dotnet build "$solution" -c Release --no-restore --maxcpucount:2
printf 'Built current graph: %s application and test projects.\n' "${#projects[@]}"
