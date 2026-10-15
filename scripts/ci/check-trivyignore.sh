#!/usr/bin/env bash
# Every .trivyignore entry must be preceded by "# expires: YYYY-MM-DD" and a reason,
# and the date must not have passed (ADR-0001). An accepted risk is temporary.
set -euo pipefail

file="${1:-.trivyignore}"
today="$(date -u +%F)"
status=0
expires=""
reason=""

if [[ ! -f "$file" ]]; then
  echo "check-trivyignore: no $file, nothing ignored"
  exit 0
fi

while IFS= read -r line || [[ -n "$line" ]]; do
  trimmed="${line#"${line%%[![:space:]]*}"}"
  if [[ -z "$trimmed" ]]; then
    expires=""; reason=""
    continue
  fi
  if [[ "$trimmed" =~ ^#[[:space:]]*expires:[[:space:]]*([0-9]{4}-[0-9]{2}-[0-9]{2})[[:space:]]*$ ]]; then
    expires="${BASH_REMATCH[1]}"
    continue
  fi
  if [[ "$trimmed" == \#* ]]; then
    reason="${trimmed#\#}"
    continue
  fi

  if [[ -z "$expires" ]]; then
    echo "::error::$file: '$trimmed' has no '# expires: YYYY-MM-DD' line"
    status=1
  elif [[ "$expires" < "$today" ]]; then
    echo "::error::$file: '$trimmed' expired on $expires"
    status=1
  elif [[ -z "${reason// /}" ]]; then
    echo "::error::$file: '$trimmed' has no reason comment"
    status=1
  else
    echo "check-trivyignore: $trimmed accepted until $expires:$reason"
  fi
  expires=""; reason=""
done < "$file"

exit "$status"
