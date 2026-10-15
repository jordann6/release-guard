#!/usr/bin/env bash
# Removes the local delivery environment: the k3d cluster and its registry.
set -euo pipefail
k3d cluster delete release-guard >/dev/null 2>&1 || true
k3d registry delete k3d-rg-registry >/dev/null 2>&1 || true
echo "down: local cluster and registry removed"
