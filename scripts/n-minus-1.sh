#!/usr/bin/env bash
# N-1 compatibility check (ADR-0003). Migrates a fresh database with THIS source's
# migrations, then runs the PREVIOUS release's integration tests against it. Passing
# proves the previous image still works on the new schema, which is what makes an
# application rollback safe without a database rollback.
#
# Usage: scripts/n-minus-1.sh <previous-release>
#   <previous-release> is a git ref (tag or commit) or a directory holding that source.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <previous-release-ref-or-directory>" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
image="postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea"
database="nminus1"
password="$(openssl rand -hex 16)"
container=""
worktree=""

cleanup() {
  if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
  if [[ -n "$worktree" ]]; then git -C "$repo_root" worktree remove --force "$worktree" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

if [[ -d "$1" ]]; then
  previous="$(cd "$1" && pwd)"
else
  worktree="$(mktemp -d)/previous"
  git -C "$repo_root" worktree add --detach "$worktree" "$1" >/dev/null
  previous="$worktree"
fi

if [[ ! -d "$previous/tests/Orders.IntegrationTests" ]]; then
  echo "n-minus-1: $previous has no integration tests" >&2
  exit 2
fi

echo "n-minus-1: starting PostgreSQL"
container="$(docker run -d -e POSTGRES_PASSWORD="$password" -p 127.0.0.1::5432 "$image")"
port="$(docker port "$container" 5432/tcp | head -1 | sed 's/.*://')"
for _ in $(seq 1 60); do
  if docker exec "$container" pg_isready -U postgres >/dev/null 2>&1; then break; fi
  sleep 1
done
docker exec "$container" psql -U postgres -q -c "CREATE DATABASE $database" >/dev/null

admin="Host=127.0.0.1;Port=$port;Username=postgres;Password=$password"
target="$admin;Database=$database"

echo "n-minus-1: applying current migrations"
Database__ConnectionString="$target" \
  dotnet run --project "$repo_root/src/Orders.Api" -c Release -- migrate

echo "n-minus-1: current schema:"
docker exec "$container" psql -U postgres -d "$database" -At \
  -c "SELECT migration_id FROM orders.__ef_migrations_history ORDER BY 1" | sed 's/^/  /'

echo "n-minus-1: running previous release's integration tests against it"
ORDERS_TEST_ADMIN_CONNECTION="$admin" ORDERS_TEST_DATABASE="$database" \
  dotnet test --project "$previous/tests/Orders.IntegrationTests" -c Release

echo "n-minus-1: previous release is compatible with the current schema"
