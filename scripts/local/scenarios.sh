#!/usr/bin/env bash
# Phase 4 acceptance: every release and recovery path, end to end on the local
# cluster, each outcome asserted against the guard's recorded state and the cluster.
#
# Usage: scripts/local/scenarios.sh [--fresh]
#   --fresh  tear down and recreate the local environment first (recommended)
# Results: artifacts/local-scenarios.md
set -euo pipefail
# shellcheck source=scripts/local/lib.sh
source "$(dirname "$0")/lib.sh"

if [[ "${1:-}" == "--fresh" ]]; then
  "$repo_root/scripts/local/down.sh"
  "$repo_root/scripts/local/up.sh"
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
results="$repo_root/artifacts/local-scenarios.md"
mkdir -p "$(dirname "$results")"
{
  echo "| # | Scenario | Expected | Observed | Seconds |"
  echo "|---|---|---|---|---|"
} > "$results"
step=0
failures=0
started=0

begin() { step=$((step + 1)); started=$(date +%s); echo; echo "=== $step. $1"; current="$1"; }
record() {
  local expected="$1" observed="$2" mark="pass"
  if [[ "$expected" != "$observed" ]]; then mark="FAIL"; failures=$((failures + 1)); fi
  echo "    expected: $expected | observed: $observed | $mark"
  echo "| $step | $current | $expected | $observed ($mark) | $(( $(date +%s) - started )) |" >> "$results"
}

status_json() { releasectl status --table "$table" --dynamo-endpoint "$dynamo"; }
lock_state() { status_json | jq -r '.lock.state'; }
running_image() { kubectl --context "$kube_context" -n orders get deploy orders-api -o jsonpath='{.spec.template.spec.containers[0].image}'; }
applied_revision() { kubectl --context "$kube_context" -n orders get kustomization orders-app -o jsonpath='{.status.lastAppliedRevision}'; }
synthetic_commit() { printf '%s-%s' "$1" "$(date +%s%N)" | shasum | cut -c1-40; }

# variant NAME KIND: a copy of the working tree with one deliberate difference.
variant() {
  local name="$1" kind="$2" dir="$work/variants/$1"
  mkdir -p "$dir"
  rsync -a --exclude .git --exclude bin --exclude obj --exclude TestResults --exclude artifacts "$repo_root/" "$dir/"
  case "$kind" in
    good)
      # A distinct build: different bytes, same behavior.
      jq --arg v "$name" '. + {Build: {Variant: $v}}' "$dir/src/Orders.Api/appsettings.json" > "$dir/a.json"
      mv "$dir/a.json" "$dir/src/Orders.Api/appsettings.json" ;;
    broken)
      # Needs a setting the environment does not have: pods crash on start, the
      # rollout stalls, migrations still succeed.
      jq --arg v "$name" '. + {Build: {Variant: $v}}' "$dir/src/Orders.Api/appsettings.json" > "$dir/a.json"
      mv "$dir/a.json" "$dir/src/Orders.Api/appsettings.json"
      perl -0pi -e 's/app\.UseExceptionHandler\(\);/if (string.IsNullOrEmpty(app.Configuration["Orders:PaymentsEndpoint"]))\n{\n    throw new InvalidOperationException("Orders:PaymentsEndpoint is required.");\n}\n\napp.UseExceptionHandler();/' "$dir/src/Orders.Api/Program.cs" ;;
    vulnerable)
      # Runtime image pinned back to .NET 10.0.0, which has fixable critical CVEs.
      sed -i '' 's|aspnet:10.0-noble-chiseled@sha256:[a-f0-9]*|aspnet:10.0.0-noble-chiseled@sha256:5730fa91fab5ec91f69661b86897249ad6a01c8da6da557695e9da4c6bc83621|' "$dir/src/Orders.Api/Dockerfile" ;;
  esac
  echo "$dir"
}

# build NAME KIND: builds, pushes and scans a variant; remembers its commit and digest.
build() {
  local name="$1" kind="$2" dir
  dir="$(variant "$name" "$kind")"
  synthetic_commit "$name" > "$work/$name.commit"
  build_image "$dir" "$name" "$work/$name.trivy.json" > "$work/$name.digest"
}
digest_of() { cat "$work/$1.digest"; }

# ship NAME [promote flags...]: promotes a built variant and waits; prints the
# verdict (Pass, Fail, Timeout) or the guard outcome when it did not start.
ship() {
  local name="$1" out outcome seq
  shift
  set +e
  out="$(promote "$(cat "$work/$name.commit")" "$(digest_of "$name")" "$work/$name.trivy.json" "$@")"
  set -e
  echo "$out" > "$work/$name.promote.json"
  outcome="$(jq -r '.outcome' <<< "$out")"
  seq="$(jq -r '.seq // empty' <<< "$out")"
  echo "    promote $name: $outcome: $(jq -r '.reason' <<< "$out")" >&2
  if [[ "$outcome" != "Started" && "$outcome" != "Resumed" ]]; then
    echo "$outcome"
    return
  fi
  set +e
  releasectl wait --table "$table" --dynamo-endpoint "$dynamo" --seq "$seq" --timeout 00:06:00 --interval 00:00:05 > "$work/$name.wait.json"
  set -e
  echo "    verdict: $(jq -r '.verdict.result // "none"'  "$work/$name.wait.json"): $(jq -r '.verdict.reason // ""' "$work/$name.wait.json")" >&2
  jq -r '.verdict.result // "NoVerdict"' "$work/$name.wait.json"
}

# release NAME KIND [promote flags...]: build then ship.
release() {
  local name="$1" kind="$2"
  shift 2
  build "$name" "$kind"
  ship "$name" "$@"
}

grant() {
  releasectl override --table "$table" --dynamo-endpoint "$dynamo" --digest "$(digest_of "$1")" --reason "$2" --hours "$3" >/dev/null
}

begin "First deployment (--initialize)"
record "Pass, Healthy" "$(release good-1 good --initialize), $(lock_state)"

begin "Second release"
record "Pass, Healthy" "$(release good-2 good), $(lock_state)"
healthy_digest="$(digest_of good-2)"

begin "Vulnerable image is refused before anything is published"
before="$(status_json | jq -r '.lock.last_seq')"
record "Blocked, no release allocated" "$(release vulnerable vulnerable), $( [[ "$(status_json | jq -r '.lock.last_seq')" == "$before" ]] && echo 'no release allocated' || echo 'release allocated')"

begin "Two concurrent promotions: exactly one starts"
a="$(variant race-a good)"; b="$(variant race-b good)"
da="$(build_image "$a" race-a "$work/race-a.trivy.json")"; db="$(build_image "$b" race-b "$work/race-b.trivy.json")"
set +e
promote "$(synthetic_commit race-a)" "$da" "$work/race-a.trivy.json" > "$work/race-a.json" &
promote "$(synthetic_commit race-b)" "$db" "$work/race-b.trivy.json" > "$work/race-b.json" &
wait
set -e
outcomes="$(jq -r '.outcome' "$work/race-a.json" "$work/race-b.json" | sort | paste -sd, -)"
winner_seq="$(jq -r 'select(.outcome == "Started") | .seq' "$work/race-a.json" "$work/race-b.json")"
releasectl wait --table "$table" --dynamo-endpoint "$dynamo" --seq "$winner_seq" --timeout 00:06:00 --interval 00:00:05 >/dev/null || true
healthy_digest="$(jq -r 'select(.outcome == "Started") | .image' "$work/race-a.json" "$work/race-b.json" | sed 's/.*@//')"
record "Blocked,Started then Healthy" "$outcomes then $(lock_state)"

begin "Failed rollout is detected by the verifier"
record "Fail, Failed" "$(release broken-1 broken), $(lock_state)"

begin "Next promotion is blocked"
record "Blocked" "$(release good-3 good)"

begin "Expired override is refused"
grant good-3 "Short approval window to prove expiry is enforced" 0.0005
sleep 3
record "Blocked" "$(ship good-3 --override)"

begin "Rollback restores the last verified release as a higher release"
set +e
releasectl rollback --table "$table" --dynamo-endpoint "$dynamo" --deploy-repo "$deploy_repo" --provider generic --insecure-registry > "$work/rollback.json"
set -e
rb_seq="$(jq -r '.seq' "$work/rollback.json")"
releasectl wait --table "$table" --dynamo-endpoint "$dynamo" --seq "$rb_seq" --timeout 00:06:00 --interval 00:00:05 >/dev/null || true
sleep 45  # past at least one more Flux reconcile interval: desired state must stay on the restored release
image_ok="$( [[ "$(running_image)" == "$cluster_repo@$healthy_digest" ]] && echo 'restored image running' || echo "wrong image $(running_image)")"
rev_ok="$( [[ "$(applied_revision)" == "$(jq -r '.tag' "$work/rollback.json")@"* ]] && echo "Flux on $(jq -r '.tag' "$work/rollback.json")" || echo "Flux on $(applied_revision)")"
record "Healthy, restored image running, Flux on $(jq -r '.tag' "$work/rollback.json")" "$(lock_state), $image_ok, $rev_ok"

begin "Approved override promotes a fix after another failure"
release broken-2 broken >/dev/null
build good-4 good
grant good-4 "Approved fix for broken-2, verified locally" 1
record "Pass, Override, Healthy" "$(ship good-4 --override), $(status_json | jq -r '.last_healthy.kind'), $(lock_state)"

begin "Interrupted promotion resumes on retry without publishing twice"
dir="$(variant good-5 good)"; commit="$(synthetic_commit good-5)"
digest="$(build_image "$dir" good-5 "$work/good-5.trivy.json")"
set +e
deploy_repo_saved="$deploy_repo"; deploy_repo="localhost:5999/unreachable"
promote "$commit" "$digest" "$work/good-5.trivy.json" > "$work/good-5.first.json" 2>/dev/null
deploy_repo="$deploy_repo_saved"
mid_state="$(lock_state)"
promote "$commit" "$digest" "$work/good-5.trivy.json" > "$work/good-5.retry.json"
set -e
retry_seq="$(jq -r '.seq' "$work/good-5.retry.json")"
releasectl wait --table "$table" --dynamo-endpoint "$dynamo" --seq "$retry_seq" --timeout 00:06:00 --interval 00:00:05 > "$work/good-5.wait.json" || true
record "Deploying, then Resumed, Pass" "$mid_state, then $(jq -r '.outcome' "$work/good-5.retry.json"), $(jq -r '.verdict.result' "$work/good-5.wait.json")"

begin "Abandoned promotion times out at the lease and blocks; rollback recovers"
dir="$(variant good-6 good)"; commit="$(synthetic_commit good-6)"
digest="$(build_image "$dir" good-6 "$work/good-6.trivy.json")"
deploy_repo="localhost:5999/unreachable"
promote "$commit" "$digest" "$work/good-6.trivy.json" --lease 00:00:20 >/dev/null 2>&1 || true
deploy_repo="$deploy_repo_saved"
sleep 25
timed_out="$(lock_state)"
blocked="$(release good-7 good)"
set +e
releasectl rollback --table "$table" --dynamo-endpoint "$dynamo" --deploy-repo "$deploy_repo" --provider generic --insecure-registry > "$work/rollback2.json"
set -e
releasectl wait --table "$table" --dynamo-endpoint "$dynamo" --seq "$(jq -r '.seq' "$work/rollback2.json")" --timeout 00:06:00 --interval 00:00:05 >/dev/null || true
record "VerifyTimeout, Blocked, Healthy" "$timed_out, $blocked, $(lock_state)"

echo
cat "$results"
echo
if [[ $failures -gt 0 ]]; then
  echo "scenarios: $failures failed"
  exit 1
fi
echo "scenarios: all $step passed"
