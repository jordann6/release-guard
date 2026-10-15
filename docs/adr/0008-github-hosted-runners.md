# ADR-0008: GitHub-hosted runners

- **Status:** proposed
- **Date:** 2026-10-15

## Context

The release and pull request workflows need runners to build and scan the application, evaluate release evidence, and publish or promote release artifacts. The EKS API endpoint is private. The cluster receives deployments through Flux, which runs inside EKS and pulls OCI artifacts from ECR (ADR-0003).

The runner therefore needs access to GitHub and, for the credentialed release jobs, the AWS APIs allowed by its environment-scoped OIDC role. It does not need a network route to the cluster, Kubernetes credentials, or access to the EKS API. Opening the private API to runner address ranges would add exposure and operational work without enabling a required step.

## Decision

Use GitHub-hosted runners for pull request checks and the application release pipeline. Keep delivery pull-based: the authorized workflow publishes a release artifact to ECR, and Flux inside EKS retrieves and applies it. Do not provide runners with private cluster network access or Kubernetes credentials.

## Alternatives rejected

| Option | Why not |
| ------ | ------- |
| Self-hosted runners inside the VPC | They would need ongoing patching, capacity management, and isolation controls. Pull-based delivery provides no cluster access requirement that justifies this operational cost. |
| Allow GitHub-hosted runner addresses through the EKS API endpoint | This would expose a private control plane to changing external address ranges and still would not be needed by the release workflow. |

## Consequences

- Runner jobs remain outside the cluster network. Flux and the in-cluster verifier perform deployment and health verification from inside EKS.
- AWS access remains limited to jobs that assume the environment-scoped OIDC roles described in ADR-0004. Those roles do not grant Kubernetes or EKS API access.
- GitHub-hosted runner availability and the workflow's public AWS API dependencies are accepted operational dependencies. Revisit this decision if a future delivery step requires private network access that cannot be performed by an in-cluster component.
