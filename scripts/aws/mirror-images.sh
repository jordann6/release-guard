#!/usr/bin/env bash
# Copies every platform image the private cluster needs into ECR, by digest, so it
# needs no route to ghcr.io or Docker Hub (ADR-0003): the Flux controllers, psql for
# the database bootstrap, and the verifier built from this commit. Repositories are
# immutable; an image that is already there is left alone.
# Requires crane and docker buildx.
# shellcheck source=scripts/aws/env.sh
source "$(dirname "$0")/env.sh"

registry="$(contract_field app_repository_url)"
registry="${registry%%/*}"
aws ecr get-login-password | crane auth login "$registry" --username AWS --password-stdin >/dev/null
aws ecr get-login-password | docker login "$registry" --username AWS --password-stdin >/dev/null

copy() { # source@digest destination:tag
  if crane digest "$2" >/dev/null 2>&1; then
    echo "mirror-images: $2 already present"
  else
    crane copy "$1" "$2"
    echo "mirror-images: $1 -> $2"
  fi
}

for entry in "${flux_images[@]}"; do
  name="${entry%%@*}"
  rest="${entry#*@}"
  digest="${rest%:*}"
  tag="${rest##*:}"
  copy "ghcr.io/fluxcd/$name@$digest" "$registry/flux/$name:$tag"
done

copy "postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea" \
  "$registry/tools/postgres:16-alpine"

commit="$(git -C "$aws_repo_root" rev-parse HEAD)"
verifier="$registry/release-guard/verifier:$commit"
if crane digest "$verifier" >/dev/null 2>&1; then
  echo "mirror-images: $verifier already present"
else
  docker buildx build --platform linux/amd64 --provenance=false \
    -f "$aws_repo_root/src/Release.Verifier/Dockerfile" -t "$verifier" --push "$aws_repo_root"
fi
echo "mirror-images: verifier $(crane digest "$verifier") for commit $commit"
