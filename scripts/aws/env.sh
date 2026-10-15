#!/usr/bin/env bash
# Shared settings for the AWS operator scripts. Source, do not execute.
# shellcheck disable=SC2034  # used by the sourcing scripts
set -euo pipefail

aws_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
landing_zone="${LANDING_ZONE_DIR:-$HOME/aws-landing-zone}"
contract_parameter="/delivery/release-guard/prod/contract"
kubeconfig="$HOME/.kube/release-guard-aws.yaml"
tunnel_port="${TUNNEL_PORT:-18443}"
flux_version="v2.9.6"
flux_images=(
  "source-controller@sha256:6a6693172589f8ff26123a231d5fa6ceb194a6efb4dc647cdf057c959f76a2e3:v1.9.6"
  "kustomize-controller@sha256:2ebeaa341da77d52b6abbbba5efcee0450d47f8b42f0e6f33b08f9020262d606:v1.9.6"
)

# Every command runs against the prod account through the operator's profile.
export AWS_PROFILE="${AWS_PROFILE:-release-guard-prod}"

contract() {
  aws ssm get-parameter --name "$contract_parameter" --query Parameter.Value --output text
}

contract_field() {
  contract | jq -er --arg f "$1" '.[$f]'
}

workload_output() {
  terraform -chdir="$landing_zone/workload" output -raw "$1"
}

kc() {
  kubectl --kubeconfig "$kubeconfig" "$@"
}
