# ADR-0006: Deployment state, the deployment guard and recovery

Status: Proposed
Date: 2026-10-15

## Context

Pushing a manifest is not a deployment. A release counts as deployed only when the expected revision is running, ready and passing a functional check. The pipeline has to block the next release while the current one is unproven or broken, and it has to provide a recovery path that works while ordinary promotion is blocked.

## Decision

### Store

DynamoDB table `release-guard-deployments`: on-demand, KMS-encrypted, point-in-time recovery on. Partition key `pk` (string), sort key `sk` (string). Reached from EKS through a DynamoDB gateway endpoint.

| `pk` | `sk` | Writer | Content |
|---|---|---|---|
| `LOCK#prod` | `STATE` | promote role | `state`, `last_seq`, `inflight_seq`, `lease_owner`, `lease_until`, `last_healthy_seq`, `version` |
| `DEPLOY#prod#<seq>` | `RECORD` | promote role | `kind` (`normal`, `rollback`, `override`, `initialize`), `source_commit`, `image_digest`, `artifact_tag`, `artifact_digest`, `scan_evidence` (Trivy report SHA-256, result, scanned digest), `sbom_digest`, `pr_evidence` (PR, tree), `run_url`, `created_at` |
| `VERIFY#prod#<seq>` | `RESULT` | verifier | `result` (`pass`, `fail`, `timeout`), `observed_artifact_revision`, `observed_image_digest`, `rollout` summary, `smoke` results, `started_at`, `finished_at`, `reason` |
| `OVERRIDE#prod#<image-digest>` | `GRANT` | promote role in `production-override` | `reason`, `expires_at`, `approved_run_url`, `actor`, `consumed_by_seq` |

Composite partition keys let IAM `dynamodb:LeadingKeys` separate the writers (ADR-0004). The pipeline cannot write a verdict, and the verifier cannot start a deployment.

**Last verified healthy release** is the highest `seq` whose `VERIFY#` result is `pass`. `last_healthy_seq` on the lock caches it. When the two disagree, `releasectl` trusts the `VERIFY#` records.

### States (on `LOCK#prod`)

| State | Meaning |
|---|---|
| `UNINITIALIZED` | Lock item absent. No release has ever been verified. |
| `DEPLOYING` | A normal, override or initialize release is published and awaiting a verdict. |
| `HEALTHY` | The latest release has a `VERIFY#` pass. |
| `FAILED` | The latest release has a `VERIFY#` fail. |
| `VERIFY_TIMEOUT` | No verdict arrived before the deadline, or a run was interrupted. Treated like `FAILED`. |
| `ROLLING_BACK` | A rollback release is published and awaiting a verdict. |
| `ROLLBACK_FAILED` | The rollback itself failed verification. Only break-glass applies. |

### Transitions

| From | Event | Actor | To | Guard |
|---|---|---|---|---|
| `UNINITIALIZED` | `promote --initialize` | promote | `DEPLOYING` | Lock item does not exist (`attribute_not_exists(pk)`); evidence valid |
| `HEALTHY` | `promote` | promote | `DEPLOYING` | `version` matches; evidence valid; digest differs from the current healthy digest |
| `DEPLOYING` | verdict pass | verifier, then `releasectl wait` | `HEALTHY` | `inflight_seq` equals the verdict's seq |
| `DEPLOYING` | verdict fail | verifier, then `releasectl wait` | `FAILED` | same |
| `DEPLOYING` | deadline passed, no verdict | `releasectl wait` or any later `releasectl` call | `VERIFY_TIMEOUT` | `lease_until` plus the verifier deadline is in the past |
| `FAILED`, `VERIFY_TIMEOUT` | `rollback` | promote in `production` | `ROLLING_BACK` | A last verified healthy release exists |
| `FAILED`, `VERIFY_TIMEOUT` | `promote` with a valid override | promote in `production-override` | `DEPLOYING` | Unexpired, unconsumed `OVERRIDE#` for this exact image digest; scan evidence valid |
| `ROLLING_BACK` | verdict pass | verifier | `HEALTHY` | |
| `ROLLING_BACK` | verdict fail or deadline | verifier or `releasectl` | `ROLLBACK_FAILED` | |
| `ROLLBACK_FAILED` | `break-glass resolve` | operator | `HEALTHY` or `FAILED` | Recorded with reason; see runbook |

The verifier only writes `VERIFY#` items. `releasectl wait`, running as the promote role, reads the verdict and moves the lock. When no workflow is running, the next `releasectl` invocation reconciles the lock from the `VERIFY#` items before doing anything else.

Every lock change is a conditional write requiring the expected `version`, and it increments `version`. The release sequence lives on the same item (`last_seq`), so allocating a sequence number and taking the lock are one conditional write. Two runs racing for the lock cannot both win. The release workflows also use `concurrency: deploy-prod` with `cancel-in-progress: false`, which is a second layer, not the guarantee.

### Promotion is blocked when

- Scan evidence is missing, failed, or names a different digest than the one being promoted.
- PR evidence is missing or its tree does not match (ADR-0002).
- The lock is in `DEPLOYING` or `ROLLING_BACK`, meaning a deployment is still running.
- The lock is in `FAILED`, `VERIFY_TIMEOUT` or `ROLLBACK_FAILED`, unless a valid override exists. Overrides do not apply to `ROLLBACK_FAILED`.
- The contract fails validation (ADR-0005).

### The verifier

`Release.Verifier` is a .NET worker in namespace `release-verifier`. It polls the lock every 10 seconds. When the state is `DEPLOYING` or `ROLLING_BACK` and the in-flight seq has no verdict, it:

1. Reads `DEPLOY#prod#<seq>` for the expected artifact digest and image digest.
2. Waits until Flux `Kustomization/orders-app` reports `Ready=True` with `status.lastAppliedRevision` ending in the expected artifact digest. If Ready is `False` for that revision, the result is fail.
3. Checks that the `orders-api` Deployment's pod template image is `<app-repo>@<expected digest>`, `observedGeneration` is current, and updated, ready and available replicas all equal desired.
4. Runs the smoke test against the in-cluster Service: create an order with an `Idempotency-Key`, repeat it and require the same order ID, read it back, apply one valid status transition with `If-Match`, and require a 409 on one invalid transition.
5. Writes `VERIFY#prod#<seq>` with `attribute_not_exists(pk)`, so a verdict is written once.

A hard deadline of 10 minutes from `DEPLOY#.created_at` produces a `timeout` verdict.

### Timeouts and interrupted runs

- Promote takes a lease: `lease_until = now + 15 minutes`. `releasectl wait` polls for up to 12 minutes.
- If the workflow is cancelled or the runner dies, the lock stays `DEPLOYING`. The verifier keeps running independently and still writes its verdict. The next `releasectl` call applies it. If no verdict exists after `lease_until`, it moves the lock to `VERIFY_TIMEOUT`.
- **Retries are idempotent by commit.** Rerunning promote for a commit whose `DEPLOY#` already exists for the in-flight seq does not publish again. It resumes waiting. A rerun after a verdict reports the verdict and exits.
- Promote takes the lock before it publishes. One `TransactWriteItems` call increments `last_seq`, moves the lock to `DEPLOYING` with `inflight_seq`, creates `DEPLOY#prod#<seq>` with `artifact_digest` empty, and for an override marks the grant consumed. Only then is the artifact pushed as `1.0.<seq>` and its digest written to the record. The verifier treats a record without an artifact digest as not yet published, and the 10-minute deadline still applies. A run that dies before the push therefore ends in `VERIFY_TIMEOUT`. That blocks promotion until a rollback, which republishes the healthy release under a higher version and is harmless when nothing changed.

### Recovery

`rollback.yml` (`workflow_dispatch`, environment `production`):

1. Requires the lock in `FAILED` or `VERIFY_TIMEOUT`.
2. Reads the last verified healthy release, re-renders its manifests with its image digest, and publishes them as the next seq with `kind: rollback`.
3. Moves the lock to `ROLLING_BACK` and waits for the verifier.

Because the healthy content is now the highest version, Flux converges to it and stays there. The failed release remains in the registry and the records.

If the rollback fails verification, the lock moves to `ROLLBACK_FAILED` and every automated path stops. The runbook covers diagnosis with `releasectl status` and Flux events. The operator can suspend Flux, pin a known digest with commands documented there, and record a `break-glass resolve` with a reason.

### Override

`override.yml` (`workflow_dispatch`, environment `production-override` with a required reviewer):

- Inputs: `image_digest`, `reason` (required, at least 20 characters), `expires_in_hours` (at most 24).
- After approval, it writes `OVERRIDE#prod#<digest>` with the approving run URL and expiry, then runs promote for that digest.
- An override lifts only the failed-rollout block. The digest must still have passing scan evidence, and the release is verified like any other.
- Each grant is consumed by exactly one deployment.
- A PR label `deploy-override-request` only posts a comment that pre-fills the inputs. It authorizes nothing.

### First deployment

- `promote --initialize` is valid only when the lock item does not exist. It creates the lock with `last_seq` 1 and records `kind: initialize`.
- Before it runs, the operator must have completed the one-time steps in the runbook: database users created, Flux installed with mirrored images, the verifier deployed.
- If the first release fails, there is no healthy release to roll back to. The lock stays `FAILED` until a fixed release is promoted with an override.

## Consequences

- "Deployed" always means a `VERIFY#` pass recorded by an identity the pipeline does not control.
- Two writers, one state document, conditional writes: the state machine is unit-tested in `Release.Core` and integration-tested against DynamoDB Local, including races and interrupted runs.
- The verifier is the single point that declares health. If it is down, deployments time out and block, which fails safe.
