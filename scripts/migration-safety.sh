#!/usr/bin/env bash
# Rejects destructive DDL in new migrations unless the change is an approved contract
# step (ADR-0003). The previous release must keep working against the new schema, so
# a release may only add: tables, nullable columns, columns with defaults, indexes.
#
# Usage:
#   scripts/migration-safety.sh --from <migration>     # check migrations after <migration>
#   scripts/migration-safety.sh --base <git-ref>       # check migrations added since <git-ref>
#   scripts/migration-safety.sh --sql-file <file>      # check a SQL file (used by self-test)
#
# Set CONTRACT_MIGRATION_APPROVED=true (CI does this when the PR has the
# contract-migration label) to report findings without failing.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
project="$repo_root/src/Orders.Api"

from=""
sql_file=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from) from="$2"; shift 2 ;;
    --base)
      # Newest migration that already exists at the base ref.
      from="$(git -C "$repo_root" ls-tree --name-only "$2" -- "src/Orders.Api/Data/Migrations/" \
        | sed -n 's|.*/\([0-9]\{14\}_[A-Za-z0-9]*\)\.cs$|\1|p' | sort | tail -1)"
      if [[ -z "$from" ]]; then from="0"; fi
      shift 2 ;;
    --sql-file) sql_file="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$sql_file" ]]; then
  if [[ -z "$from" ]]; then
    echo "one of --from, --base or --sql-file is required" >&2
    exit 2
  fi
  sql_file="$(mktemp)"
  trap 'rm -f "$sql_file"' EXIT
  (cd "$repo_root" && dotnet restore "$project" >/dev/null && dotnet ef migrations script "$from" -p "$project" -o "$sql_file" >/dev/null)
fi

# One finding per matching line. EF emits one statement per line for these forms.
patterns=(
  'DROP[[:space:]]+(TABLE|SCHEMA|VIEW|TYPE)'
  'DROP[[:space:]]+COLUMN'
  'RENAME[[:space:]]+(TO|COLUMN)'
  'ALTER[[:space:]]+COLUMN[[:space:]]+[^[:space:]]+[[:space:]]+(SET[[:space:]]+DATA[[:space:]]+)?TYPE'
  'ALTER[[:space:]]+COLUMN[[:space:]]+[^[:space:]]+[[:space:]]+SET[[:space:]]+NOT[[:space:]]+NULL'
  'TRUNCATE'
  'DELETE[[:space:]]+FROM'
)

findings="$(grep -n -i -E "$(IFS='|'; echo "${patterns[*]}")" "$sql_file" || true)"

# ADD <column> ... NOT NULL without a DEFAULT breaks inserts from the previous release.
not_null_adds="$(grep -n -i -E 'ALTER[[:space:]]+TABLE.*[[:space:]]ADD[[:space:]].*NOT[[:space:]]+NULL' "$sql_file" \
  | grep -v -i 'DEFAULT' || true)"

all="$(printf '%s\n%s\n' "$findings" "$not_null_adds" | sed '/^$/d' | sort -u -t: -k1,1n)"

if [[ -z "$all" ]]; then
  echo "migration-safety: no destructive statements"
  exit 0
fi

echo "migration-safety: destructive statements found:"
while IFS= read -r line; do echo "  $line"; done <<< "$all"

if [[ "${CONTRACT_MIGRATION_APPROVED:-false}" == "true" ]]; then
  echo "migration-safety: allowed because the change is labelled contract-migration"
  exit 0
fi

echo "migration-safety: split this into expand and contract releases, or label the PR contract-migration" >&2
exit 1
