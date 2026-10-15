# ADR-0002: Main protection and mapping check evidence to the merged tree

Status: Proposed
Date: 2026-10-15

## Context

ADR-0001 skips the test suite on main. That is only safe if the release pipeline can prove the merged source is the source that passed the checks. With squash merges, the commit on main is new: its SHA never existed on the PR, so check runs cannot be looked up by SHA alone.

## Decision

### Branch protection

A repository ruleset on `main`, applied by `scripts/apply-ruleset.sh` through `gh api` and read back after applying:

- Restrict deletions and block force pushes.
- Require a pull request before merging. Required approvals: 0, because the repository has one maintainer. This is a stated limitation, not an oversight.
- Require status checks: every job listed in ADR-0001's PR table, with "require branches to be up to date before merging" (strict) on.
- Allowed merge method: squash only. Require linear history.
- Bypass list: empty. Rulesets apply to repository admins unless they are listed as bypass actors, so the owner cannot push directly either.

Rulesets on a GitHub Free account require a public repository. The repository is public.

### Why the tree matches

With strict status checks, a PR can only merge when its branch contains the current tip of `main`. The PR checks run on `refs/pull/N/merge`, the merge of the PR head into that tip. A squash merge of an up-to-date branch writes a commit whose tree is exactly that merge tree. So the evidence key is the **tree SHA**, not the commit SHA.

### PR evidence

The PR pipeline's final `evidence` job writes `pr-evidence.json` (schema `docs/schemas/pr-evidence.schema.json`) as an artifact named `pr-evidence`, retained 90 days:

- PR number, head SHA, base SHA, merge-ref commit SHA
- `merge_tree`: `git rev-parse HEAD^{tree}` of the checked-out merge ref
- conclusion of every required job
- digest and Trivy summary of the image scanned on the PR

### Evidence gate on main

`releasectl evidence-gate` runs in the first release job, with a read-only token. It fails closed on any of these:

1. `GET /repos/{owner}/{repo}/commits/{sha}/pulls` does not return exactly one PR, merged into `main`, whose merge commit SHA equals the pushed SHA.
2. For that PR's head SHA, any required check name lacks a completed check run with conclusion `success` from the GitHub Actions app (`app.slug == "github-actions"`) in the run of `.github/workflows/pr.yml`.
3. The newest successful `pr.yml` run for that head SHA has no `pr-evidence` artifact, or the artifact fails schema validation.
4. `evidence.merge_tree` differs from `git rev-parse HEAD^{tree}` on the pushed commit.
5. The evidence head SHA differs from the PR head SHA reported by the API.

The artifact content is used only for the tree and the image summary. Pass or fail comes from the check-runs API, which the PR's code cannot write.

### Recovery when evidence is missing or mismatched

`verify-main.yml` (`workflow_dispatch`, environment `publish` restricted to `main`, no AWS permissions) runs the complete PR suite against the current main commit and uploads `pr-evidence.json` with `source: verify-main`. The gate accepts that evidence when its run is for the same commit SHA and its tree matches. Then the release is rerun. No input, label or variable can mark evidence as present.

## Consequences

- An admin cannot skip the checks: the ruleset blocks direct pushes, and if protection were ever loosened, the gate still finds no matching evidence and publishes nothing.
- Squash-only is a firm requirement. Changing the merge method needs a new ADR.
- Merge queues are not used. They would give the tree match a different mechanism.
- The gate's logic lives in `Release.Core` and is unit-tested against recorded API responses, including a forged tree and a check run from a different app.
