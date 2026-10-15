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

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
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

table="release-guard-deployments"
dynamo="http://localhost:18000"
host_repo="localhost:5050/release-guard/app"
cluster_repo="k3d-rg-registry:5000/release-guard/app"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
releasectl() { dotnet run --project "$repo_root/src/Release.Cli" --no-build -- "$@"; }

echo "release: building $commit from $source_dir"
docker build --quiet -f "$source_dir/src/Orders.Api/Dockerfile" -t "$host_repo:${commit:0:12}" "$source_dir" >/dev/null
docker push --quiet "$host_repo:${commit:0:12}" >/dev/null
digest="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$host_repo:${commit:0:12}" \
  | grep "^$host_repo@" | head -1 | sed 's/.*@//')"
echo "release: pushed $cluster_repo@$digest"

# Scan the pushed digest itself, so scanned bytes and promoted bytes are the same.
trivy image --quiet --image-src remote --insecure --scanners vuln --severity HIGH,CRITICAL \
  --ignore-unfixed --format json --output "$work/trivy.json" "$host_repo@$digest"
echo "release: scanned, $(jq '[.Results[]?.Vulnerabilities[]?] | length' "$work/trivy.json") fixable high or critical findings"

if [[ -n "$override_reason" ]]; then
  releasectl override --table "$table" --dynamo-endpoint "$dynamo" \
    --digest "$digest" --reason "$override_reason" --hours 1
fi

set +e
releasectl promote \
  --table "$table" --dynamo-endpoint "$dynamo" \
  --deploy-repo localhost:5050/release-guard/deploy --provider generic --insecure-registry \
  --template "$repo_root/deploy/release" \
  --commit "$commit" --image-repo "$cluster_repo" --image-digest "$digest" \
  --scan-report "$work/trivy.json" --scanned-digest "$digest" \
  ${promote_flags[@]+"${promote_flags[@]}"} | tee "$work/promote.json"
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
