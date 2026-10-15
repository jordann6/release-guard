#!/usr/bin/env bash
# One-time platform bootstrap for the AWS environment (ADR-0006, first deployment).
# Run with scripts/aws/eks-tunnel.sh open in another terminal, after the delivery
# root is applied, the contract published and scripts/aws/mirror-images.sh run.
#
#   1. Flux from the mirrored images, locked down like the local environment.
#   2. The database: the orders database and its two IAM-authenticated roles.
#   3. The platform layer: namespaces, Flux tenant, delivery objects, verifier.
#
# Idempotent. Releases are never part of this; they arrive through release.yml.
# shellcheck source=scripts/aws/env.sh
source "$(dirname "$0")/env.sh"

kc version --request-timeout=10s >/dev/null || { echo "bootstrap: cannot reach the cluster; is eks-tunnel.sh running?" >&2; exit 1; }

c="$(contract)"
field() { jq -er --arg f "$1" '.[$f]' <<<"$c"; }
registry="$(field app_repository_url)"
registry="${registry%%/*}"
commit="$(git -C "$aws_repo_root" rev-parse HEAD)"

echo "bootstrap: installing Flux $flux_version from $registry/flux"
flux install --kubeconfig "$kubeconfig" --version "$flux_version" \
  --components=source-controller,kustomize-controller --registry "$registry/flux" >/dev/null
args="$(kc -n flux-system get deploy kustomize-controller -o jsonpath='{.spec.template.spec.containers[0].args}')"
if [[ "$args" != *"--no-cross-namespace-refs=true"* ]]; then
  kc -n flux-system patch deploy kustomize-controller --type json -p '[
    {"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--no-cross-namespace-refs=true"},
    {"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--default-service-account=flux-restricted"}]' >/dev/null
fi
# source-controller pulls release artifacts from ECR with its own IRSA role.
kc -n flux-system annotate serviceaccount source-controller --overwrite \
  "eks.amazonaws.com/role-arn=$(field flux_source_role_arn)" >/dev/null
kc -n flux-system rollout restart deploy/source-controller >/dev/null
kc -n flux-system rollout status deploy/source-controller --timeout=300s >/dev/null
kc -n flux-system rollout status deploy/kustomize-controller --timeout=300s >/dev/null

for ns in orders release-verifier; do
  kc create namespace "$ns" --dry-run=client -o yaml | kc apply -f - >/dev/null
done

db_host="$(workload_output db_endpoint)"
echo "bootstrap: database roles on $db_host"
# IAM authentication: the connection strings carry no password at all.
kc -n orders create secret generic orders-db \
  --from-literal=app="Host=$db_host;Database=orders;Username=orders_app;SSL Mode=Require" \
  --from-literal=migrator="Host=$db_host;Database=orders;Username=orders_migrator;SSL Mode=Require" \
  --dry-run=client -o yaml | kc apply -f - >/dev/null

kc create namespace db-bootstrap --dry-run=client -o yaml | kc apply -f - >/dev/null
# The master password goes from Secrets Manager to a short-lived Secret through stdin,
# never through command-line arguments. The namespace and Secret are deleted below.
aws secretsmanager get-secret-value --secret-id "$(workload_output db_secret_arn)" \
  --query SecretString --output text | jq -r .password | tr -d '\n' \
  | kc -n db-bootstrap create secret generic rds-master --from-file=PGPASSWORD=/dev/stdin \
      --dry-run=client -o yaml | kc apply -f - >/dev/null
kc -n db-bootstrap create configmap sql \
  --from-file="$aws_repo_root/scripts/db/bootstrap.sql" --from-file="$aws_repo_root/scripts/db/rds-iam.sql" \
  --dry-run=client -o yaml | kc apply -f - >/dev/null
kc -n db-bootstrap delete job db-bootstrap --ignore-not-found >/dev/null
kc apply -f - >/dev/null <<YAML
apiVersion: batch/v1
kind: Job
metadata:
  name: db-bootstrap
  namespace: db-bootstrap
spec:
  backoffLimit: 2
  activeDeadlineSeconds: 300
  template:
    spec:
      restartPolicy: Never
      automountServiceAccountToken: false
      securityContext:
        runAsNonRoot: true
        runAsUser: 70
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: psql
          image: $registry/tools/postgres:16-alpine
          env:
            - { name: PGHOST, value: "$db_host" }
            - { name: PGUSER, value: appadmin }
            - { name: PGSSLMODE, value: require }
            - name: PGPASSWORD
              valueFrom: { secretKeyRef: { name: rds-master, key: PGPASSWORD } }
          command: ["sh", "-ec"]
          args:
            - |
              psql -d app -tAc "SELECT 1 FROM pg_database WHERE datname = 'orders'" | grep -q 1 \
                || psql -d app -v ON_ERROR_STOP=1 -c "CREATE DATABASE orders"
              psql -d orders -v ON_ERROR_STOP=1 -f /sql/bootstrap.sql -f /sql/rds-iam.sql
              echo "orders database and roles ready"
          securityContext:
            allowPrivilegeEscalation: false
            capabilities: { drop: [ALL] }
          volumeMounts:
            - { name: sql, mountPath: /sql }
      volumes:
        - name: sql
          configMap: { name: sql }
YAML
kc -n db-bootstrap wait --for=condition=complete job/db-bootstrap --timeout=300s >/dev/null
kc -n db-bootstrap logs job/db-bootstrap | tail -1
kc delete namespace db-bootstrap --wait=false >/dev/null

echo "bootstrap: applying the platform layer"
render="$(mktemp -d)"
trap 'rm -rf "$render"' EXIT
cp -R "$aws_repo_root/deploy/platform/." "$render/"
VERIFIER_REPOSITORY_URL="$registry/release-guard/verifier" \
VERIFIER_DIGEST="$(crane digest "$registry/release-guard/verifier:$commit")" \
APP_ROLE_ARN="$(field app_role_arn)" \
MIGRATOR_ROLE_ARN="$(field migrator_role_arn)" \
VERIFIER_ROLE_ARN="$(field verifier_role_arn)" \
DEPLOY_REPOSITORY_URL="$(field deploy_repository_url)" \
DEPLOYMENT_TABLE="$(field deployment_table)" \
REGION="$(field region)" \
python3 -I - "$render/aws/kustomization.yaml" <<'PY'
import os, re, sys
path = sys.argv[1]
text = open(path).read()
names = ["VERIFIER_REPOSITORY_URL", "VERIFIER_DIGEST", "APP_ROLE_ARN", "MIGRATOR_ROLE_ARN",
         "VERIFIER_ROLE_ARN", "DEPLOY_REPOSITORY_URL", "DEPLOYMENT_TABLE", "REGION"]
for name in names:
    value = os.environ[name]
    if not value:
        sys.exit(f"bootstrap: {name} is empty")
    text = text.replace("${" + name + "}", value)
left = re.findall(r"\$\{[A-Z_]+\}", text)
if left:
    sys.exit(f"bootstrap: unresolved placeholders {left}")
open(path, "w").write(text)
PY
kc apply -k "$render/aws" >/dev/null
kc -n release-verifier rollout status deploy/verifier --timeout=300s >/dev/null

echo "bootstrap: platform ready. Flux is waiting for release 1.0.1; promote with --initialize."
