#!/usr/bin/env bash
# Creates the deployment environments from ADR-0004 and reads them back.
# Every environment accepts deployments from main only. production-override also
# needs an approval from the repository owner. Idempotent.
#
# Usage: scripts/github/apply-environments.sh [owner/repo]
set -euo pipefail

repo="${1:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}"
owner_id="$(gh api "users/${repo%%/*}" --jq .id)"

apply() {
  local env="$1" reviewers="$2"
  jq -n --argjson reviewers "$reviewers" '{
      wait_timer: 0,
      reviewers: $reviewers,
      prevent_self_review: false,
      deployment_branch_policy: { protected_branches: false, custom_branch_policies: true }
    }' | gh api -X PUT "repos/$repo/environments/$env" --input - --silent

  # Exactly one branch policy: main.
  local policies
  policies="$(gh api "repos/$repo/environments/$env/deployment-branch-policies" --jq '.branch_policies[] | "\(.id) \(.name)"')"
  while read -r id name; do
    [[ -z "${id:-}" || "$name" == "main" ]] && continue
    gh api -X DELETE "repos/$repo/environments/$env/deployment-branch-policies/$id" --silent
  done <<< "$policies"
  if ! grep -q ' main$' <<< "$policies"; then
    gh api -X POST "repos/$repo/environments/$env/deployment-branch-policies" \
      -f name=main -f type=branch --silent
  fi
  echo "apply-environments: $env applied"
}

apply publish '[]'
apply production '[]'
# A one-person repository: the owner approves their own override, and the approval
# is still recorded against the run (ADR-0004).
apply production-override "[{\"type\":\"User\",\"id\":$owner_id}]"

echo "apply-environments: read back"
status=0
for env in publish production production-override; do
  summary="$(gh api "repos/$repo/environments/$env" --jq '{
      name,
      reviewers: [.protection_rules[]? | select(.type == "required_reviewers") | .reviewers[].reviewer.login],
      custom_branches: .deployment_branch_policy.custom_branch_policies }')"
  branches="$(gh api "repos/$repo/environments/$env/deployment-branch-policies" --jq '[.branch_policies[].name]')"
  echo "$summary branches=$branches"
  [[ "$branches" == '["main"]' ]] || { echo "apply-environments: $env branch policy is not main-only" >&2; status=1; }
done
exit "$status"
