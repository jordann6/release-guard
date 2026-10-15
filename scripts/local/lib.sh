#!/usr/bin/env bash
# Shared helpers for the local release scripts. Source, do not execute.
# shellcheck disable=SC2034  # variables are used by the sourcing scripts

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
table="release-guard-deployments"
dynamo="http://localhost:18000"
host_repo="localhost:5050/release-guard/app"
cluster_repo="k3d-rg-registry:5000/release-guard/app"
deploy_repo="localhost:5050/release-guard/deploy"
kube_context="k3d-release-guard"

releasectl() {
  dotnet run --project "$repo_root/src/Release.Cli" --no-build -- "$@"
}

state_args() {
  printf '%s\n' --table "$table" --dynamo-endpoint "$dynamo"
}

# build_image SOURCE_DIR TAG REPORT
# Builds and pushes the image, scans the pushed digest, and prints the digest.
build_image() {
  local source_dir="$1" tag="$2" report="$3" digest
  docker build --quiet -f "$source_dir/src/Orders.Api/Dockerfile" -t "$host_repo:$tag" "$source_dir" >/dev/null
  docker push --quiet "$host_repo:$tag" >/dev/null
  digest="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$host_repo:$tag" \
    | grep "^$host_repo@" | head -1 | sed 's/.*@//')"
  # Scan the pushed digest itself, so scanned bytes and promoted bytes are the same.
  trivy image --quiet --image-src remote --insecure --scanners vuln --severity HIGH,CRITICAL \
    --ignore-unfixed --format json --output "$report" "$host_repo@$digest"
  echo "$digest"
}

# promote COMMIT DIGEST REPORT [extra releasectl flags...]
promote() {
  local commit="$1" digest="$2" report="$3"
  shift 3
  releasectl promote --table "$table" --dynamo-endpoint "$dynamo" \
    --deploy-repo "$deploy_repo" --provider generic --insecure-registry \
    --template "$repo_root/deploy/release" \
    --commit "$commit" --image-repo "$cluster_repo" --image-digest "$digest" \
    --scan-report "$report" --scanned-digest "$digest" "$@"
}
