# ADR-0001: PR pipeline and main release pipeline

Status: Proposed
Date: 2026-10-15

## Context

The repository needs two kinds of automation. One proves a change is safe to merge. The other turns a merged change into a published, deployed and verified release. The open question was whether the static checks that run on a pull request should run again after the merge to main.

The infrastructure repositories rerun their credential-free gates on both `pull_request` and `push` to main. That makes sense there, because a direct push or an admin merge could otherwise reach the credentialed apply without passing them. This repository closes that gap differently (ADR-0002): main cannot be pushed directly and the release pipeline refuses to publish without evidence that matches the merged tree.

## Decision

### PR pipeline: `.github/workflows/pr.yml`

- Trigger: `pull_request` targeting `main`.
- Workflow permissions: `contents: read`. No job requests `id-token: write`, and no job holds AWS credentials or reads repository secrets. CodeQL needs `security-events: write` on its own job.
- Jobs live in the reusable `checks.yml`, which `verify-main.yml` also calls. All are required status checks:

| Job | What it enforces |
|---|---|
| `format` | `dotnet format --verify-no-changes` |
| `build` | Release build with analyzers, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, NuGet `--locked-mode` |
| `unit-tests` | Domain and release-tooling unit tests |
| `integration-tests` | PostgreSQL 16 via Testcontainers; DynamoDB Local for release-store tests |
| `migration-safety` | Fails on destructive DDL unless the PR has the `contract-migration` label (ADR-0003) |
| `n-minus-1` | The previous release's integration tests pass against the new schema |
| `guardrails` | platform-guardrails `ci.yml`, pinned by commit: gitleaks over full history plus stack detection. Its container job is skipped because `image-scan` covers the image |
| `sast` | CodeQL C# analysis, then a gate step that parses the SARIF and fails on any security result at `error` or `warning` level |
| `image-scan` | Builds the image and runs Trivy with `severity HIGH,CRITICAL`, `exit-code 1`, `ignore-unfixed true`. Every `.trivyignore` entry needs an `# expires: YYYY-MM-DD` comment, and expired entries fail the job |
| `evidence` | Runs last, needs every job above, writes `pr-evidence.json` (ADR-0002) |

Uploading a report never counts as passing. Every scanner exits non-zero on a finding above its threshold.

### Main release pipeline: `.github/workflows/release.yml`

- Trigger: `push` to `main`.
- `concurrency: release-main`, `cancel-in-progress: false`.
- Jobs, in order:

| Job | Credentials | Purpose |
|---|---|---|
| `evidence-gate` | read-only `GITHUB_TOKEN` | Proves required checks passed on a tree identical to the merged commit (ADR-0002). Fails closed. |
| `build-scan` | none | Rebuilds the production image from the merged commit, runs Trivy (same thresholds), produces a CycloneDX SBOM, records the digest. Does not push. |
| `publish` | environment `publish`, OIDC role `release-guard-publish` | Validates the deployment contract, pushes the scanned image to the application ECR repository tagged with the commit SHA, attests provenance and SBOM. |
| `promote` | environment `production`, OIDC role `release-guard-promote` | Runs `releasectl promote`, then `releasectl wait` until the in-cluster verifier records a verdict (ADR-0006). |

The image that `publish` pushes is the exact image `build-scan` scanned. `build-scan` builds an OCI archive whose manifest digest is known at build time and scans that archive. `publish` pushes the archive with `skopeo copy --preserve-digests` and fails if ECR reports a different digest.

`publish`, `promote` and the evidence record run only when the repository variable `DELIVERY_ENABLED` is `true`, which the operator sets once the AWS delivery layer exists. Until then every merge still runs the evidence gate and builds and scans the production image.

### What does not run on main

Formatting, analyzers, unit tests, integration tests, the migration checks and CodeQL do not run again on main. The tree they ran against is proven identical to the merged tree, and they are deterministic on an identical tree. Rerunning them would cost CI minutes and add no security property.

The image scan does run again, because the main pipeline builds a new artifact. A rebuild can resolve a different base layer or package, so the PR scan never saw the bytes being published.

## Consequences

- A PR can never cause an AWS call, so a malicious PR cannot reach credentials.
- A merge that skipped or bypassed the checks produces no release (ADR-0002).
- If PR evidence is lost or does not match, `verify-main.yml` regenerates it by running the full suite on main (ADR-0002). That is the only way back.
- Nightly rescans of published images for newly disclosed CVEs are an optional follow-up, not part of this decision.
