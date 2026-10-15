#!/usr/bin/env bash
# Brings up the local delivery environment (Phase 4): a k3d cluster with Flux, a
# plain-HTTP registry standing in for both ECR repositories, PostgreSQL, DynamoDB
# Local and the release verifier. Idempotent. Database passwords are generated into
# cluster Secrets and never written to disk.
#
# Host endpoints:  registry localhost:5050, DynamoDB http://localhost:18000
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cluster="release-guard"
registry="rg-registry"
table="release-guard-deployments"
kubectl_ctx="k3d-$cluster"

for tool in k3d kubectl flux docker dotnet openssl; do
  command -v "$tool" >/dev/null || { echo "up: $tool is required" >&2; exit 1; }
done

if ! k3d registry list -o json | grep -q "\"k3d-$registry\""; then
  echo "up: creating registry k3d-$registry (host port 5050)"
  k3d registry create "$registry" --port 5050
fi

if ! k3d cluster list -o json | grep -q "\"name\":\"$cluster\""; then
  echo "up: creating cluster $cluster"
  k3d cluster create "$cluster" \
    --servers 1 --agents 0 \
    --registry-use "k3d-$registry:5050" \
    --registry-config "$repo_root/scripts/local/registries.yaml" \
    --port "18000:30800@server:0" \
    --k3s-arg "--disable=traefik@server:0" \
    --k3s-arg "--disable=metrics-server@server:0" \
    --kubeconfig-switch-context=false \
    --wait
fi
k() { kubectl --context "$kubectl_ctx" "$@"; }

echo "up: installing Flux (source and kustomize controllers only)"
flux install --context "$kubectl_ctx" --components=source-controller,kustomize-controller >/dev/null
# Multi-tenancy lockdown (ADR-0004): no cross-namespace source references, and a
# Kustomization that names no service account reconciles with no permissions.
args="$(k -n flux-system get deploy kustomize-controller -o jsonpath='{.spec.template.spec.containers[0].args}')"
if [[ "$args" != *"--no-cross-namespace-refs=true"* ]]; then
  k -n flux-system patch deploy kustomize-controller --type json -p '[
    {"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--no-cross-namespace-refs=true"},
    {"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--default-service-account=flux-restricted"}]' >/dev/null
fi
k -n flux-system rollout status deploy/kustomize-controller --timeout=180s >/dev/null
k -n flux-system rollout status deploy/source-controller --timeout=180s >/dev/null

for ns in data orders release-verifier; do
  k create namespace "$ns" --dry-run=client -o yaml | k apply -f - >/dev/null
done

if ! k -n data get secret postgres-bootstrap >/dev/null 2>&1; then
  echo "up: generating local database passwords"
  admin_pw="$(openssl rand -hex 16)"; migrator_pw="$(openssl rand -hex 16)"; app_pw="$(openssl rand -hex 16)"
  k -n data create secret generic postgres-bootstrap \
    --from-literal=POSTGRES_PASSWORD="$admin_pw" \
    --from-literal=ORDERS_MIGRATOR_PASSWORD="$migrator_pw" \
    --from-literal=ORDERS_APP_PASSWORD="$app_pw" >/dev/null
  host="postgres.data.svc.cluster.local"
  k -n orders create secret generic orders-db \
    --from-literal=app="Host=$host;Database=orders;Username=orders_app;Password=$app_pw" \
    --from-literal=migrator="Host=$host;Database=orders;Username=orders_migrator;Password=$migrator_pw" >/dev/null
fi
k -n data create configmap postgres-init --from-file="$repo_root/deploy/local/init/10-bootstrap.sh" \
  --dry-run=client -o yaml | k apply -f - >/dev/null
k -n data create configmap postgres-bootstrap-sql --from-file="$repo_root/scripts/db/bootstrap.sql" \
  --dry-run=client -o yaml | k apply -f - >/dev/null

echo "up: building the verifier image"
docker build --quiet -f "$repo_root/src/Release.Verifier/Dockerfile" -t localhost:5050/release-guard/verifier:local "$repo_root" >/dev/null
docker push --quiet localhost:5050/release-guard/verifier:local >/dev/null

echo "up: applying the platform layer"
k apply -k "$repo_root/deploy/platform/local" >/dev/null
k -n data rollout status deploy/postgres --timeout=180s >/dev/null
k -n data rollout status deploy/dynamodb --timeout=180s >/dev/null

echo "up: creating the deployment table"
for _ in $(seq 1 30); do
  if dotnet run --project "$repo_root/src/Release.Cli" --no-build -- init-table \
      --table "$table" --dynamo-endpoint http://localhost:18000 >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
k -n release-verifier rollout restart deploy/verifier >/dev/null
k -n release-verifier rollout status deploy/verifier --timeout=180s >/dev/null

echo "up: ready. kubectl context $kubectl_ctx; table $table at http://localhost:18000"
