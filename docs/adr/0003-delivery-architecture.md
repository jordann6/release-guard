# ADR-0003: Delivery architecture (ECR, Flux, EKS)

Status: Proposed
Date: 2026-10-15

## Context

The EKS cluster in the landing zone has a private API endpoint only. GitHub-hosted runners cannot reach it, and opening it to them would undo a deliberate control. Delivery has to be pull-based from inside the cluster.

## Decision

### Flow

1. `publish` pushes the application image to the **application repository** `release-guard/app`.
2. `promote` renders `deploy/` with the image pinned by digest and pushes the result as an OCI artifact to the **deployment repository** `release-guard/deploy`.
3. Flux `source-controller`, running in EKS with an IRSA role, pulls the artifact from ECR through the VPC interface endpoints.
4. Flux `kustomize-controller` applies it as a restricted service account.
5. The in-cluster verifier confirms the rollout and records the verdict (ADR-0006).

GitHub runners never hold Kubernetes credentials and never call the EKS API.

### Registries

Both repositories are created by the infrastructure layer (`aws-landing-zone/delivery`), never by a release:

| Repository | Content | Tag mutability | Writer |
|---|---|---|---|
| `release-guard/app` | Application image | IMMUTABLE | publish role |
| `release-guard/deploy` | Rendered manifests (OCI artifact) | IMMUTABLE | promote role |
| `flux/*` | Mirrored Flux controller images | IMMUTABLE | operator, once per environment |

All three use KMS encryption and scan on push. Flux controller images are copied by digest with `scripts/mirror-flux-images.sh`. The cluster then needs no route to ghcr.io and the firewall allowlist does not change.

### Immutable images, deployed by digest

- Application image tag: the full commit SHA. It is never moved or reused.
- Manifests reference `<app-repo-url>@sha256:<digest>`. A tag never appears in a pod spec.

### Release channel

The channel is kept separate from the immutable image tags:

- Each promotion allocates the next release sequence `N` from DynamoDB with a conditional increment (ADR-0006).
- The rendered artifact is pushed as `<deploy-repo>:1.0.N`. Because the repository is immutable, `1.0.N` can never be rewritten.
- The Flux `OCIRepository` uses `ref.semver: ">=1.0.0"` and follows the highest version.

**Rollback moves forward.** It renders the last verified release's content and publishes it as `1.0.(N+1)`. Desired state is now the healthy release, so Flux does not reapply the failed one. Nothing is retagged or deleted.

### Flux configuration

- `OCIRepository` `orders-release`: `provider: aws`, interval 1m, `ref.semver: ">=1.0.0"`.
- `Kustomization` `orders-migrate`: path `./migrate`, applies the migration Job. `wait: true`, `timeout: 5m`.
- `Kustomization` `orders-app`: path `./app`, `dependsOn: orders-migrate`, `wait: true`, `timeout: 5m`, `prune: true`.
- Both run as `serviceAccountName: orders-reconciler`, which is bound to a Role in the `orders` namespace only.
- kustomize-controller runs with `--no-cross-namespace-refs=true` and `--default-service-account=flux-restricted`, so any Kustomization that omits a service account gets no permissions.

### Rollout

- `Deployment` strategy `RollingUpdate`, `maxSurge: 1`, `maxUnavailable: 0`, `progressDeadlineSeconds: 240`, 2 replicas, `PodDisruptionBudget minAvailable: 1`.
- Readiness checks the database. Liveness checks only the process.
- Canary delivery and additional environments are deferred until the release and recovery paths are proven.

### Database migrations

- Migrations run as a Job named `orders-migrate-<release-seq>` in the `orders-migrate` Kustomization, as the `orders-migrator` database user (ADR-0004). The application user has DML rights only.
- **Expand and contract only.** A release may add tables, nullable columns, columns with defaults and indexes. Dropping, renaming or retyping a column, or adding `NOT NULL` without a default, is a contract step. It must ship in a later release than the change that stopped using the column.
- Enforcement in the PR pipeline (ADR-0001):
  - `migration-safety` generates the idempotent SQL script and rejects destructive statements unless the PR carries the `contract-migration` label.
  - `n-minus-1` applies the new migrations, then runs the previous release's integration tests against that schema. That proves the previous image can still run, which is what makes an application rollback safe without a database rollback.
- A rollback never reverts the schema.

## Consequences

- One ECR read path for both images and manifests, through private endpoints.
- A deployment is a new artifact version, so history in `release-guard/deploy` doubles as a release log.
- Releases must stay backward compatible with the previous schema. That costs some discipline in exchange for rollbacks with no database step.
