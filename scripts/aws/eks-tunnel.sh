#!/usr/bin/env bash
# Opens a private path to the EKS API (ADR-0003): an SSM port forward through a
# cluster node to the private endpoint. The API is never made public and GitHub
# runners never use this. Leave it running in its own terminal; Ctrl-C closes it.
#
# Writes a kubeconfig at ~/.kube/release-guard-aws.yaml (mode 0600) that talks to
# https://127.0.0.1:$TUNNEL_PORT with the cluster's real TLS server name.
# Requires the AWS CLI with the session-manager-plugin.
# shellcheck source=scripts/aws/env.sh
source "$(dirname "$0")/env.sh"

cluster="$(contract_field cluster_name)"
endpoint="$(aws eks describe-cluster --name "$cluster" --query cluster.endpoint --output text)"
host="${endpoint#https://}"
instance="$(aws ec2 describe-instances \
  --filters "Name=tag:eks:cluster-name,Values=$cluster" "Name=instance-state-name,Values=running" \
  --query 'Reservations[0].Instances[0].InstanceId' --output text)"
if [[ -z "$instance" || "$instance" == "None" ]]; then
  echo "eks-tunnel: no running node found for $cluster" >&2
  exit 1
fi

umask 077
aws eks update-kubeconfig --name "$cluster" --kubeconfig "$kubeconfig" --alias release-guard-aws >/dev/null
kubectl config --kubeconfig "$kubeconfig" set-cluster "$(kubectl config --kubeconfig "$kubeconfig" view -o jsonpath='{.clusters[0].name}')" \
  --server "https://127.0.0.1:$tunnel_port" --tls-server-name "$host" >/dev/null

echo "eks-tunnel: $cluster API via node $instance on 127.0.0.1:$tunnel_port (kubeconfig $kubeconfig)"
exec aws ssm start-session --target "$instance" \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "host=$host,portNumber=443,localPortNumber=$tunnel_port"
