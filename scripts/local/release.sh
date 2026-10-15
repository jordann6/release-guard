#!/usr/bin/env bash
# One release against the local environment, the same sequence as release.yml:
# build, push, scan the pushed digest, promote under the guard, wait for the verdict.
#
# Usage: scripts/local/release.sh [--source DIR] [--commit SHA] [--initialize]
#                                 [--override "REASON"] [--no-wait]
#   --source    build the image from DIR (default: this repository)
#   --commit    source commit to record (default: git HEAD of this repository)
#   --override  grant an override for the built digest, then promote with it. Locally this
#               stands in for override.yml, where the grant needs an environment approval.
# Exit codes follow releasectl: 3 blocked, 4 verification failed or timed out.
set -euo pipefail
# shellcheck source=scripts/local/lib.sh
source "$(dirname "$0")/lib.sh"

source_dir="$repo_root"
commit="$(git -C "$repo_root" rev-parse HEAD)"
promote_flags=()
override_reason=""
wait_for_verdict=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --source) source_dir="$(cd "$2" && pwd)"; shift 2 ;;
    --commit) commit="$2"; shift 2 ;;
    --initialize) promote_flags+=(--initialize); shift ;;
    --override) override_reason="$2"; promote_flags+=(--override); shift 2 ;;
    --no-wait) wait_for_verdict=0; shift ;;
    *) echo "release: unknown argument $1" >&2; exit 2 ;;
  esac
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "release: building $commit from $source_dir"
digest="$(build_image "$source_dir" "${commit:0:12}" "$work/trivy.json")"
echo "release: pushed $cluster_repo@$digest, $(jq '[.Results[]?.Vulnerabilities[]?] | length' "$work/trivy.json") fixable high or critical findings"

if [[ -n "$override_reason" ]]; then
  releasectl override --table "$table" --dynamo-endpoint "$dynamo" --digest "$digest" --reason "$override_reason" --hours 1
fi

set +e
promote "$commit" "$digest" "$work/trivy.json" ${promote_flags[@]+"${promote_flags[@]}"} | tee "$work/promote.json"
status=${PIPESTATUS[0]}
set -e
if [[ $status -ne 0 ]]; then
  exit "$status"
fi

seq="$(jq -r '.seq // empty' "$work/promote.json")"
outcome="$(jq -r '.outcome' "$work/promote.json")"
if [[ "$outcome" == "NoChange" || -z "$seq" || $wait_for_verdict -eq 0 ]]; then
  exit 0
fi

echo "release: waiting for the verifier's verdict on release $seq"
releasectl wait --table "$table" --dynamo-endpoint "$dynamo" --seq "$seq" --timeout 00:12:00 --interval 00:00:05
