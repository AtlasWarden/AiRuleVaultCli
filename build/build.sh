#!/usr/bin/env sh
set -eu

configuration="${1:-Release}"
repo_root="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
dotnet build "$repo_root/RuleVaultCli.sln" --configuration "$configuration" --no-restore
