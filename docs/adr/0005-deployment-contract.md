# ADR-0005: Infrastructure to application deployment contract

Status: Proposed
Date: 2026-10-15

## Context

The release workflows need resource identifiers that the infrastructure layer owns: repository URLs, role ARNs, cluster name, table name. Reading Terraform state from the application repository would hand it state access. Hardcoding names in two repositories would let them drift. The platform-guardrails apply workflow currently produces no outputs at all.

## Decision

### Publication

After a successful apply of `aws-landing-zone/delivery`, the infrastructure layer publishes one JSON document to SSM Parameter Store:

- Name: `/delivery/release-guard/prod/contract`
- Type: `String`, standard tier. It holds identifiers, not secrets.
- Writer: `platform-guardrails/scripts/publish-contract.sh`. It runs as a step in `tf-apply.yml` when the caller sets `contract_parameter`, and it can be run by the operator after a local apply. It reads `terraform output -json`, keeps only the outputs named in the allowlist file `delivery/contract-allowlist.txt`, and adds the fields Terraform does not know.
- Only the infrastructure apply principal holds `ssm:PutParameter` and `ssm:DeleteParameter` on `/delivery/*`. The publish and promote roles hold `ssm:GetParameter` on this one parameter.
- `tf-destroy.yml` deletes the parameter after a successful destroy when `contract_parameter` is set. The operator destroy runbook does the same.

### Content (version 1)

Schema: `docs/schemas/deployment-contract.v1.schema.json`.

| Field | Source |
|---|---|
| `contract_version` | publisher, `1` |
| `generation_id` | Terraform output, a `random_id` in the delivery root |
| `region` | Terraform output |
| `cluster_name`, `namespace` | Terraform output |
| `app_repository_url`, `deploy_repository_url` | Terraform output |
| `publish_role_arn`, `promote_role_arn` | Terraform output |
| `app_role_arn`, `migrator_role_arn`, `verifier_role_arn`, `flux_source_role_arn` | Terraform output |
| `deployment_table` | Terraform output |
| `source_commit` | publisher, commit of the infrastructure repository that was applied |
| `published_at` | publisher, UTC timestamp |

The allowlist is the security boundary. Adding an output to the delivery root does not publish it.

### Bootstrapping the role ARNs

The workflows need a role to read the contract, and the role ARNs are in the contract. The two OIDC role ARNs are therefore also stored as GitHub environment variables (`PUBLISH_ROLE_ARN` on `publish`, `PROMOTE_ROLE_ARN` on `production` and `production-override`). After assuming the role, `releasectl contract-check` fails if the ARN in the contract differs from the one that was assumed.

### Consumer validation

`releasectl contract-check` runs at the start of `publish`, `promote`, `rollback` and `override`, and fails closed when:

1. The parameter does not exist.
2. The document is not valid JSON or fails the v1 schema.
3. `contract_version` has a major version this build does not understand.
4. Any required field is missing or empty.
5. `generation_id` differs from the `DeliveryGeneration` tag on the live `release-guard/deploy` repository. That means the contract describes infrastructure that no longer exists, for example one left over from before a destroy and recreate.
6. The assumed role ARN differs from the contract's role for that stage.

## Consequences

- The application repository never reads Terraform state and never repeats a resource name.
- Recreating the environment produces a new generation, and every stale consumer fails loudly instead of pushing to a repository that no longer exists.
- Contract changes that remove or rename fields bump `contract_version` and need a matching `releasectl` release.
