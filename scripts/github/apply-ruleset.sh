#!/usr/bin/env bash
# Applies main-branch protection (ADR-0002) through the GitHub API and reads it back.
# Idempotent: updates the ruleset in place when it already exists.
#
# Usage: scripts/github/apply-ruleset.sh [owner/repo]
set -euo pipefail

repo="${1:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}"
name="main-protection"
actions_app_id=15368 # GitHub Actions: checks must come from Actions, not any app.

# Required checks: every gate in checks.yml, as reported under the pr.yml caller job.
checks=(
  "checks / guardrails / ci / Secret scan (gitleaks)"
  "checks / format"
  "checks / build"
  "checks / unit-tests"
  "checks / integration-tests"
  "checks / migration-safety"
  "checks / n-minus-1"
  "checks / sast"
  "checks / image-scan"
  "checks / evidence"
)

required_checks="$(printf '%s\n' "${checks[@]}" \
  | jq -R --argjson app "$actions_app_id" '{context: ., integration_id: $app}' | jq -s .)"

body="$(jq -n --arg name "$name" --argjson checks "$required_checks" '{
  name: $name,
  target: "branch",
  enforcement: "active",
  bypass_actors: [],
  conditions: { ref_name: { include: ["~DEFAULT_BRANCH"], exclude: [] } },
  rules: [
    { type: "deletion" },
    { type: "non_fast_forward" },
    { type: "required_linear_history" },
    { type: "pull_request", parameters: {
        required_approving_review_count: 0,
        dismiss_stale_reviews_on_push: true,
        require_code_owner_review: false,
        require_last_push_approval: false,
        required_review_thread_resolution: false,
        allowed_merge_methods: ["squash"] } },
    { type: "required_status_checks", parameters: {
        strict_required_status_checks_policy: true,
        do_not_enforce_on_create: false,
        required_status_checks: $checks } }
  ]
}')"

# Squash only at the repository level too, and title squash commits after the PR.
gh api -X PATCH "repos/$repo" --silent \
  -F allow_squash_merge=true -F allow_merge_commit=false -F allow_rebase_merge=false \
  -F delete_branch_on_merge=true -f squash_merge_commit_title=PR_TITLE \
  -f squash_merge_commit_message=PR_BODY

existing="$(gh api "repos/$repo/rulesets" --jq ".[] | select(.name == \"$name\") | .id")"
if [[ -n "$existing" ]]; then
  echo "$body" | gh api -X PUT "repos/$repo/rulesets/$existing" --input - --silent
  id="$existing"
  echo "apply-ruleset: updated ruleset $id"
else
  id="$(echo "$body" | gh api -X POST "repos/$repo/rulesets" --input - --jq .id)"
  echo "apply-ruleset: created ruleset $id"
fi

echo "apply-ruleset: read back"
gh api "repos/$repo/rulesets/$id" --jq '{
  enforcement,
  bypass_actors,
  target: .conditions.ref_name.include,
  rules: [.rules[] | .type],
  merge_methods: (.rules[] | select(.type == "pull_request") | .parameters.allowed_merge_methods),
  strict: (.rules[] | select(.type == "required_status_checks") | .parameters.strict_required_status_checks_policy),
  required_checks: [.rules[] | select(.type == "required_status_checks") | .parameters.required_status_checks[].context]
}'
gh api "repos/$repo" --jq '{allow_squash_merge, allow_merge_commit, allow_rebase_merge, delete_branch_on_merge}'

# Fail if the read-back does not match what was requested.
actual_bypass="$(gh api "repos/$repo/rulesets/$id" --jq '.bypass_actors | length')"
actual_checks="$(gh api "repos/$repo/rulesets/$id" --jq '[.rules[] | select(.type == "required_status_checks") | .parameters.required_status_checks[]] | length')"
if [[ "$actual_bypass" != "0" || "$actual_checks" != "${#checks[@]}" ]]; then
  echo "apply-ruleset: read-back mismatch (bypass=$actual_bypass checks=$actual_checks)" >&2
  exit 1
fi
echo "apply-ruleset: verified"
