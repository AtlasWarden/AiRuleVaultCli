#!/usr/bin/env sh
set -eu

if [ "$#" -lt 5 ]; then
  printf '%s\n' 'usage: install.sh PACKAGE_ROOT VAULT_ROOT CONFIG_ROOT VAULT_ID PLAN_PATH [--apply]' >&2
  exit 2
fi

package_root=$1
vault_root=$2
config_root=$3
vault_id=$4
plan_path=$5
apply=
[ "$#" -ge 6 ] && apply=$6
manifest="$package_root/artifacts.json"
[ -f "$manifest" ] || { printf '%s\n' 'artifacts.json is required; package execution is blocked.' >&2; exit 4; }

printf '%s\n' 'POSIX wrapper requires a verified local RID artifact entry and is intentionally unsupported on this Windows-only development host.' >&2
printf '%s\n' "Requested explicit roots: vault=$vault_root config=$config_root id=$vault_id plan=$plan_path apply=$apply" >&2
exit 7
