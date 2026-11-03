# Local release scenarios

Every release and recovery path from ADR-0006, run end to end on a local cluster by
`scripts/local/scenarios.sh --fresh`. Each step builds a real image, scans the pushed
digest, promotes it through `releasectl`, and waits for the in-cluster verifier. The
outcome is checked against the guard's records and, where relevant, against what the
cluster runs.

Environment: k3d (one node), Flux 2.9.6 (source and kustomize controllers, locked down
as in AWS), Argo Rollouts 1.10.1, Prometheus 3.15.1 fed by an OpenTelemetry Collector,
a plain-HTTP registry standing in for both ECR repositories, PostgreSQL 16 and DynamoDB
Local. Every release after the first is a canary: half the replicas, about a minute of
analysis on error ratio and p95 latency, then the rest (ADR-0009). Recorded 2026-11-03
on a fresh cluster.

| # | Scenario | Expected | Observed | Seconds |
|---|---|---|---|---|
| 1 | First deployment (--initialize) | Pass, Healthy | Pass, Healthy (pass) | 69 |
| 2 | Second release | Pass, Healthy | Pass, Healthy (pass) | 173 |
| 3 | Vulnerable image is refused before anything is published | Blocked, no release allocated | Blocked, no release allocated (pass) | 27 |
| 4 | Two concurrent promotions: exactly one starts | Blocked,Started then Healthy | Blocked,Started then Healthy (pass) | 196 |
| 5 | Failed rollout is detected by the verifier | Fail, Failed | Fail, Failed (pass) | 319 |
| 6 | Next promotion is blocked | Blocked | Blocked (pass) | 8 |
| 7 | Expired override is refused | Blocked | Blocked (pass) | 4 |
| 8 | Rollback restores the last verified release as a higher release | Healthy, restored image running, Flux on 1.0.5 | Healthy, restored image running, Flux on 1.0.5 (pass) | 105 |
| 9 | Approved override promotes a fix after another failure | Pass, Override, Healthy | Pass, Override, Healthy (pass) | 468 |
| 10 | Interrupted promotion resumes on retry without publishing twice | Deploying, then Resumed, Pass | Deploying, then Resumed, Pass (pass) | 180 |
| 11 | Abandoned promotion times out at the lease and blocks; rollback recovers | VerifyTimeout, Blocked, Healthy | VerifyTimeout, Blocked, Healthy (pass) | 113 |
| 12 | Canary analysis stops a release that fails 30% of requests; stable keeps serving | Fail, Failed, aborted, stable only | Fail, Failed, aborted, stable only (pass) | 112 |
| 13 | Rollback after an aborted canary restores a healthy release | Healthy, Healthy | Healthy, Healthy (pass) | 49 |

How the failures are produced:

- **Vulnerable image (3):** the runtime base image pinned back to .NET 10.0.0. Scan evidence fails, so the guard refuses before allocating a release number.
- **Failed rollout (5, 9):** a build that requires a setting the environment does not have. The canary pod crashes on start, the Rollout hits its progress deadline and aborts, and the verifier records Fail with the reason.
- **Degraded release (12):** a build that starts and passes every probe but fails about 30% of order requests. Only the canary analysis can see it: the first measurement read an error ratio of 0.286 against a limit of 0.01, the Rollout aborted, and only the stable release's pods were serving afterwards. The verdict was recorded 35 seconds after Flux applied the release.
- **Interrupted promotion (10):** the first attempt takes the lock and records the release, then cannot reach the registry. The retry for the same commit and image resumes that release instead of starting a new one.
- **Abandoned promotion (11):** the same interruption with a 20-second lease and no retry. When the lease runs out, the next read settles the environment to `VerifyTimeout`, and promotion is blocked until a rollback.

The rollback in step 8 republished release 2's artifact as `1.0.5`. Flux stayed on it after a further reconcile, and the running image was release 2's digest.

Each passing canary in this run measured an error ratio of 0 and a p95 latency between 17 and 37 ms. Step 13 rolled back after the aborted canary and the Rollout returned to Healthy.
