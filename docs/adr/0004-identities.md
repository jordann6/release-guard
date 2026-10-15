# ADR-0004: Identities and least privilege

Status: Proposed
Date: 2026-10-15

## Context

Each stage of the release does one thing, so each gets its own identity. No static AWS access keys exist anywhere. GitHub Actions federates through OIDC. EKS workloads use IRSA, which the landing-zone cluster already provides through its OIDC provider. EKS Pod Identity was considered and not chosen, because IRSA is already in place and proven there.

## Decision

### AWS and Kubernetes identities

| Identity | Federation and trust | Allowed | Not allowed |
|---|---|---|---|
| `release-guard-publish` | GitHub OIDC, `aud = sts.amazonaws.com`, `sub = repo:jordann6/release-guard:environment:publish` | Read the contract parameter; `ecr:GetAuthorizationToken`; push and describe on `release-guard/app`; `ecr:ListTagsForResource` on `release-guard/deploy` (contract generation check) | Delete, untag, repository policy, lifecycle policy; any other repository; DynamoDB |
| `release-guard-promote` | GitHub OIDC, `sub` in `repo:jordann6/release-guard:environment:production` or `…:environment:production-override` | Read the contract; describe and pull on `release-guard/app`; push on `release-guard/deploy`; DynamoDB `GetItem`, `Query`, conditional `PutItem`/`UpdateItem` on the deployment table where `dynamodb:LeadingKeys` is `LOCK#prod` or starts with `DEPLOY#prod`/`OVERRIDE#prod` | Writing `VERIFY#` items; deleting items; anything in `release-guard/app` beyond reads |
| `flux-source` | IRSA, `sub = system:serviceaccount:flux-system:source-controller` | `ecr:GetAuthorizationToken`; pull on `release-guard/deploy` and `flux/*` | Push; any other repository |
| `release-verifier` | IRSA, `sub = system:serviceaccount:release-verifier:verifier` | DynamoDB `GetItem` on the table; `PutItem` only where the key starts with `VERIFY#prod` | Writing `LOCK#`, `DEPLOY#`, `OVERRIDE#` |
| `orders-api` | IRSA, `sub = system:serviceaccount:orders:orders-api` | `rds-db:connect` on `dbuser:<db-resource-id>/orders_app` | Anything else in AWS |
| `orders-migrator` | IRSA, `sub = system:serviceaccount:orders:orders-migrator` | `rds-db:connect` on `dbuser:<db-resource-id>/orders_migrator` | Anything else in AWS |

Every GitHub-federated role carries a permissions boundary that also denies any action outside the deployment region. The OIDC provider is read as a data source. It belongs to the landing-zone bootstrap and is not created here.

The landing zone's `prod-external-secrets` role is not reused for the application. The application reads no secrets at all: it authenticates to PostgreSQL with short-lived IAM tokens generated from its IRSA credentials and refreshed by Npgsql's periodic password provider.

`LeadingKeys` matches the partition key, so the deployment table uses composite partition keys (`DEPLOY#prod#<seq>`, `VERIFY#prod#<seq>`). That way, item prefixes and IAM boundaries line up exactly (ADR-0006).

### Kubernetes RBAC

| Service account | Namespace | Bound to |
|---|---|---|
| `orders-reconciler` | `orders` | Role: manage Deployments, Services, ConfigMaps, ServiceAccounts, Jobs and PodDisruptionBudgets; read ReplicaSets and Pods for health checks. `orders` only |
| `flux-restricted` | `orders` | Nothing. It is the default for Kustomizations without an explicit account |
| `verifier` | `release-verifier` | Role in `orders`: `get` on the `orders-api` Deployment and the `orders-app` and `orders-migrate` Kustomizations, by resource name. Nothing else |
| `orders-api`, `orders-migrator` | `orders` | No Kubernetes API access; `automountServiceAccountToken: false` except for the projected IRSA token |

### Database users

Created once during first-deployment initialization (`scripts/db-bootstrap.sql`, run by the operator) and granted `rds_iam`:

- `orders_migrator`: owns the `orders` schema. DDL rights.
- `orders_app`: `SELECT`, `INSERT`, `UPDATE` on the `orders` schema tables. No `DELETE`, no DDL.

### GitHub environments

Applied by `scripts/apply-environments.sh` through `gh api`, then read back:

| Environment | Deployment branches | Reviewers | Used by |
|---|---|---|---|
| `publish` | `main` only | none | `release.yml` publish, `verify-main.yml` |
| `production` | `main` only | none | `release.yml` promote, `rollback.yml` |
| `production-override` | `main` only | required: repository owner | `override.yml` |

Because trust is scoped to the environment subject, a workflow run on any other branch cannot obtain a token those roles accept.

## Consequences

- The promote job can publish and record a deployment but cannot record its own success. Only the verifier inside the cluster can write a verdict.
- A one-person repository means the owner approves their own overrides. The approval is still recorded with run URL and expiry, which is the audit property this project demonstrates.
- Adding an application AWS dependency later means adding a statement to `orders-api`, reviewed through the landing-zone PR.
