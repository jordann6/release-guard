# Local release scenarios (Phase 4)

Every release and recovery path from ADR-0006, run end to end on a local cluster by
`scripts/local/scenarios.sh --fresh`. Each step builds a real image, scans the pushed
digest, promotes it through `releasectl`, and waits for the in-cluster verifier. The
outcome is checked against the guard's records and, where relevant, against what the
cluster runs.

Environment: k3d (one node), Flux 2.9.6 (source and kustomize controllers, locked down
as in AWS), a plain-HTTP registry standing in for both ECR repositories, PostgreSQL 16
and DynamoDB Local. Recorded 2026-10-15 on a fresh cluster.

| # | Scenario | Expected | Observed | Seconds |
|---|---|---|---|---|
| 1 | First deployment (--initialize) | Pass, Healthy | Pass, Healthy (pass) | 81 |
| 2 | Second release | Pass, Healthy | Pass, Healthy (pass) | 90 |
| 3 | Vulnerable image is refused before anything is published | Blocked, no release allocated | Blocked, no release allocated (pass) | 22 |
| 4 | Two concurrent promotions: exactly one starts | Blocked,Started then Healthy | Blocked,Started then Healthy (pass) | 102 |
| 5 | Failed rollout is detected by the verifier | Fail, Failed | Fail, Failed (pass) | 161 |
| 6 | Next promotion is blocked | Blocked | Blocked (pass) | 12 |
| 7 | Expired override is refused | Blocked | Blocked (pass) | 4 |
| 8 | Rollback restores the last verified release as a higher release | Healthy, restored image running, Flux on 1.0.5 | Healthy, restored image running, Flux on 1.0.5 (pass) | 99 |
| 9 | Approved override promotes a fix after another failure | Pass, Override, Healthy | Pass, Override, Healthy (pass) | 230 |
| 10 | Interrupted promotion resumes on retry without publishing twice | Deploying, then Resumed, Pass | Deploying, then Resumed, Pass (pass) | 90 |
| 11 | Abandoned promotion times out at the lease and blocks; rollback recovers | VerifyTimeout, Blocked, Healthy | VerifyTimeout, Blocked, Healthy (pass) | 113 |

How the failures are produced:

- **Vulnerable image (3):** the runtime base image pinned back to .NET 10.0.0. Scan evidence fails, so the guard refuses before allocating a release number.
- **Failed rollout (5, 9):** a build that requires a setting the environment does not have. Pods crash on start. Flux's health check fails after 90 seconds (the local timeout), and the verifier records Fail with Flux's reason.
- **Interrupted promotion (10):** the first attempt takes the lock and records the release, then cannot reach the registry. The retry for the same commit and image resumes that release instead of starting a new one.
- **Abandoned promotion (11):** the same interruption with a 20-second lease and no retry. When the lease runs out, the next read settles the environment to `VerifyTimeout`, and promotion is blocked until a rollback.

The rollback in step 8 republished release 2's artifact as `1.0.5`. Flux stayed on it after a further reconcile, and the running image was release 2's digest.
