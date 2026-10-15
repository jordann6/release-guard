# ADR-0007: Release evidence and supply-chain pinning

Status: Proposed
Date: 2026-10-15

## Context

Each release should be explainable after the fact: what source, what bytes, what was scanned, what ran, and whether it worked. The build inputs should be fixed so the same commit cannot silently pull different dependencies.

## Decision

### Evidence kept per release

| Evidence | Produced by | Stored in |
|---|---|---|
| Image digest | `build-scan` | `DEPLOY#` record, GitHub Release |
| CycloneDX SBOM | Syft in `build-scan` | Workflow artifact; SBOM attestation (`actions/attest-sbom`) bound to the image digest |
| Build provenance | `actions/attest-build-provenance` in `publish` | GitHub attestation store, bound to the image digest |
| Trivy report, JSON and SARIF | `build-scan` | Workflow artifact; SHA-256 and result in `DEPLOY#.scan_evidence` |
| PR evidence | `pr.yml` | Workflow artifact; PR number and tree in `DEPLOY#.pr_evidence` |
| Deployment record | `releasectl promote` | `DEPLOY#prod#<seq>` |
| Verification and smoke results | verifier | `VERIFY#prod#<seq>` |

After the verdict, `releasectl export-evidence` writes `release-evidence-<seq>.json`, which joins all of the above. The release workflow uploads it with 90-day retention and attaches it to a GitHub Release named `r<seq>`, whether the verdict passed or failed. The DynamoDB records are the system of record. The GitHub Release is a readable copy.

Attestations can be checked by anyone with `gh attestation verify oci://<app-repo-url>@<digest> --repo jordann6/release-guard`.

### Pinning

- **Base images** are pinned by digest in the Dockerfile, for example `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:<digest>`. The chiseled image has no shell or package manager and runs as a non-root user by default.
- **.NET SDK** version is pinned in `global.json` with `rollForward: latestPatch`.
- **NuGet** uses central package management (`Directory.Packages.props`) and lock files (`RestorePackagesWithLockFile`). CI restores with `--locked-mode`.
- **Third-party actions** are pinned to full commit SHAs with the version in a trailing comment.
- **Reusable workflows** from platform-guardrails are pinned to a full commit SHA, and their `guardrails_ref` input is set to the same SHA, so the scripts they check out match the reviewed workflow.
- **Tool binaries** downloaded in workflows (Trivy, Syft, gitleaks, Flux CLI) are pinned by version and verified against published SHA-256 checksums.
- **Dependabot** proposes updates for `nuget`, `docker` and `github-actions`. Each update is an ordinary PR through the full pipeline.

## Consequences

- Rebuilding an old commit uses the same inputs, apart from deliberately updated pins.
- Updates arrive as reviewed PRs rather than at build time.
- Signing with cosign and having Flux verify signatures is an optional enhancement. Provenance attestations already bind each image digest to the workflow run that built it.
